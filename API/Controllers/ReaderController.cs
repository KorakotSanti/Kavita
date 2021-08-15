﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using API.Comparators;
using API.DTOs;
using API.DTOs.Reader;
using API.Entities;
using API.Extensions;
using API.Interfaces;
using API.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers
{
    public class ReaderController : BaseApiController
    {
        private readonly IDirectoryService _directoryService;
        private readonly ICacheService _cacheService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ChapterSortComparer _chapterSortComparer = new ChapterSortComparer();
        private readonly ChapterSortComparerZeroFirst _chapterSortComparerForInChapterSorting = new ChapterSortComparerZeroFirst();
        private readonly NaturalSortComparer _naturalSortComparer = new NaturalSortComparer();

        public ReaderController(IDirectoryService directoryService, ICacheService cacheService, IUnitOfWork unitOfWork)
        {
            _directoryService = directoryService;
            _cacheService = cacheService;
            _unitOfWork = unitOfWork;
        }

        [HttpGet("image")]
        public async Task<ActionResult> GetImage(int chapterId, int page)
        {
            if (page < 0) return BadRequest("Page cannot be less than 0");
            var chapter = await _cacheService.Ensure(chapterId);
            if (chapter == null) return BadRequest("There was an issue finding image file for reading");

            try
            {
                var (path, _) = await _cacheService.GetCachedPagePath(chapter, page);
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return BadRequest($"No such image for page {page}");

                var content = await _directoryService.ReadFileAsync(path);
                var format = Path.GetExtension(path).Replace(".", "");

                // Calculates SHA1 Hash for byte[]
                Response.AddCacheHeader(content);

                return File(content, "image/" + format);
            }
            catch (Exception)
            {
                _cacheService.CleanupChapters(new []{ chapterId });
                throw;
            }
        }

        [HttpGet("chapter-info")]
        public async Task<ActionResult<ChapterInfoDto>> GetChapterInfo(int seriesId, int chapterId)
        {
            // PERF: Write this in one DB call
            var chapter = await _cacheService.Ensure(chapterId);
            if (chapter == null) return BadRequest("Could not find Chapter");

            var volume = await _unitOfWork.SeriesRepository.GetVolumeDtoAsync(chapter.VolumeId);
            if (volume == null) return BadRequest("Could not find Volume");
            var mangaFile = (await _unitOfWork.VolumeRepository.GetFilesForChapterAsync(chapterId)).First();
            var series = await _unitOfWork.SeriesRepository.GetSeriesByIdAsync(seriesId);

            return Ok(new ChapterInfoDto()
            {
                ChapterNumber =  chapter.Range,
                VolumeNumber = volume.Number + string.Empty,
                VolumeId = volume.Id,
                FileName = Path.GetFileName(mangaFile.FilePath),
                SeriesName = series?.Name,
                IsSpecial = chapter.IsSpecial,
                Pages = chapter.Pages,
            });
        }


        [HttpPost("mark-read")]
        public async Task<ActionResult> MarkRead(MarkReadDto markReadDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            var volumes = await _unitOfWork.SeriesRepository.GetVolumes(markReadDto.SeriesId);
            user.Progresses ??= new List<AppUserProgress>();
            foreach (var volume in volumes)
            {
                foreach (var chapter in volume.Chapters)
                {
                    var userProgress = GetUserProgressForChapter(user, chapter);

                    if (userProgress == null)
                    {
                        user.Progresses.Add(new AppUserProgress
                        {
                            PagesRead = chapter.Pages,
                            VolumeId = volume.Id,
                            SeriesId = markReadDto.SeriesId,
                            ChapterId = chapter.Id
                        });
                    }
                    else
                    {
                        userProgress.PagesRead = chapter.Pages;
                        userProgress.SeriesId = markReadDto.SeriesId;
                        userProgress.VolumeId = volume.Id;
                    }
                }
            }

            _unitOfWork.UserRepository.Update(user);

            if (await _unitOfWork.CommitAsync())
            {
                return Ok();
            }


            return BadRequest("There was an issue saving progress");
        }

        private static AppUserProgress GetUserProgressForChapter(AppUser user, Chapter chapter)
        {
            AppUserProgress userProgress = null;
            try
            {
                userProgress =
                    user.Progresses.SingleOrDefault(x => x.ChapterId == chapter.Id && x.AppUserId == user.Id);
            }
            catch (Exception)
            {
                // There is a very rare chance that user progress will duplicate current row. If that happens delete one with less pages
                var progresses = user.Progresses.Where(x => x.ChapterId == chapter.Id && x.AppUserId == user.Id).ToList();
                if (progresses.Count > 1)
                {
                    user.Progresses = new List<AppUserProgress>()
                    {
                        user.Progresses.First()
                    };
                    userProgress = user.Progresses.First();
                }
            }

            return userProgress;
        }

        [HttpPost("mark-unread")]
        public async Task<ActionResult> MarkUnread(MarkReadDto markReadDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            var volumes = await _unitOfWork.SeriesRepository.GetVolumes(markReadDto.SeriesId);
            user.Progresses ??= new List<AppUserProgress>();
            foreach (var volume in volumes)
            {
                foreach (var chapter in volume.Chapters)
                {
                    var userProgress = GetUserProgressForChapter(user, chapter);

                    if (userProgress == null) continue;
                    userProgress.PagesRead = 0;
                    userProgress.SeriesId = markReadDto.SeriesId;
                    userProgress.VolumeId = volume.Id;
                }
            }

            _unitOfWork.UserRepository.Update(user);

            if (await _unitOfWork.CommitAsync())
            {
                return Ok();
            }


            return BadRequest("There was an issue saving progress");
        }

        [HttpPost("mark-volume-read")]
        public async Task<ActionResult> MarkVolumeAsRead(MarkVolumeReadDto markVolumeReadDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());

            var chapters = await _unitOfWork.VolumeRepository.GetChaptersAsync(markVolumeReadDto.VolumeId);
            foreach (var chapter in chapters)
            {
                user.Progresses ??= new List<AppUserProgress>();
                var userProgress = user.Progresses.SingleOrDefault(x => x.ChapterId == chapter.Id && x.AppUserId == user.Id);

                if (userProgress == null)
                {
                    user.Progresses.Add(new AppUserProgress
                    {
                        PagesRead = chapter.Pages,
                        VolumeId = markVolumeReadDto.VolumeId,
                        SeriesId = markVolumeReadDto.SeriesId,
                        ChapterId = chapter.Id
                    });
                }
                else
                {
                    userProgress.PagesRead = chapter.Pages;
                    userProgress.SeriesId = markVolumeReadDto.SeriesId;
                    userProgress.VolumeId = markVolumeReadDto.VolumeId;
                }
            }

            _unitOfWork.UserRepository.Update(user);

            if (await _unitOfWork.CommitAsync())
            {
                return Ok();
            }

            return BadRequest("Could not save progress");
        }

        [HttpGet("get-progress")]
        public async Task<ActionResult<ProgressDto>> GetProgress(int chapterId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            var progressBookmark = new ProgressDto()
            {
                PageNum = 0,
                ChapterId = chapterId,
                VolumeId = 0,
                SeriesId = 0
            };
            if (user.Progresses == null) return Ok(progressBookmark);
            var progress = user.Progresses.SingleOrDefault(x => x.AppUserId == user.Id && x.ChapterId == chapterId);

            if (progress != null)
            {
                progressBookmark.SeriesId = progress.SeriesId;
                progressBookmark.VolumeId = progress.VolumeId;
                progressBookmark.PageNum = progress.PagesRead;
                progressBookmark.BookScrollId = progress.BookScrollId;
            }
            return Ok(progressBookmark);
        }

        [HttpPost("progress")]
        public async Task<ActionResult> BookmarkProgress(ProgressDto progressDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());

            // Don't let user save past total pages.
            var chapter = await _unitOfWork.VolumeRepository.GetChapterAsync(progressDto.ChapterId);
            if (progressDto.PageNum > chapter.Pages)
            {
                progressDto.PageNum = chapter.Pages;
            }

            if (progressDto.PageNum < 0)
            {
                progressDto.PageNum = 0;
            }


            try
            {
                // TODO: Look into creating a progress entry when a new item is added to the DB so we can just look it up and modify it
               user.Progresses ??= new List<AppUserProgress>();
               var userProgress =
                  user.Progresses.SingleOrDefault(x => x.ChapterId == progressDto.ChapterId && x.AppUserId == user.Id);

               if (userProgress == null)
               {
                  user.Progresses.Add(new AppUserProgress
                  {
                     PagesRead = progressDto.PageNum,
                     VolumeId = progressDto.VolumeId,
                     SeriesId = progressDto.SeriesId,
                     ChapterId = progressDto.ChapterId,
                     BookScrollId = progressDto.BookScrollId,
                     LastModified = DateTime.Now
                  });
               }
               else
               {
                  userProgress.PagesRead = progressDto.PageNum;
                  userProgress.SeriesId = progressDto.SeriesId;
                  userProgress.VolumeId = progressDto.VolumeId;
                  userProgress.BookScrollId = progressDto.BookScrollId;
                  userProgress.LastModified = DateTime.Now;
               }

               _unitOfWork.UserRepository.Update(user);

               if (await _unitOfWork.CommitAsync())
               {
                  return Ok();
               }
            }
            catch (Exception)
            {
               await _unitOfWork.RollbackAsync();
            }

            return BadRequest("Could not save progress");
        }

        [HttpGet("get-bookmarks")]
        public async Task<ActionResult<IEnumerable<BookmarkDto>>> GetBookmarks(int chapterId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            if (user.Bookmarks == null) return Ok(Array.Empty<BookmarkDto>());
            return Ok(await _unitOfWork.UserRepository.GetBookmarkDtosForChapter(user.Id, chapterId));
        }

        [HttpPost("remove-bookmarks")]
        public async Task<ActionResult> RemoveBookmarks(int seriesId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            if (user.Bookmarks == null) return Ok("Nothing to remove");
            try
            {
                user.Bookmarks = user.Bookmarks.Where(bmk => bmk.SeriesId == seriesId).ToList();
                _unitOfWork.UserRepository.Update(user);

                if (await _unitOfWork.CommitAsync())
                {
                    return Ok();
                }
            }
            catch (Exception)
            {
                await _unitOfWork.RollbackAsync();
            }

            return BadRequest("Could not clear bookmarks");

        }

        [HttpGet("get-volume-bookmarks")]
        public async Task<ActionResult<IEnumerable<BookmarkDto>>> GetBookmarksForVolume(int volumeId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            if (user.Bookmarks == null) return Ok(Array.Empty<BookmarkDto>());
            return Ok(await _unitOfWork.UserRepository.GetBookmarkDtosForVolume(user.Id, volumeId));
        }

        [HttpGet("get-series-bookmarks")]
        public async Task<ActionResult<IEnumerable<BookmarkDto>>> GetBookmarksForSeries(int seriesId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            if (user.Bookmarks == null) return Ok(Array.Empty<BookmarkDto>());

            return Ok(await _unitOfWork.UserRepository.GetBookmarkDtosForSeries(user.Id, seriesId));
        }

        [HttpPost("bookmark")]
        public async Task<ActionResult> BookmarkPage(BookmarkDto bookmarkDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());

            // Don't let user save past total pages.
            var chapter = await _unitOfWork.VolumeRepository.GetChapterAsync(bookmarkDto.ChapterId);
            if (bookmarkDto.Page > chapter.Pages)
            {
                bookmarkDto.Page = chapter.Pages;
            }

            if (bookmarkDto.Page < 0)
            {
                bookmarkDto.Page = 0;
            }


            try
            {
                user.Bookmarks ??= new List<AppUserBookmark>();
               var userBookmark =
                  user.Bookmarks.SingleOrDefault(x => x.ChapterId == bookmarkDto.ChapterId && x.AppUserId == user.Id && x.Page == bookmarkDto.Page);

               if (userBookmark == null)
               {
                  user.Bookmarks.Add(new AppUserBookmark()
                  {
                     Page = bookmarkDto.Page,
                     VolumeId = bookmarkDto.VolumeId,
                     SeriesId = bookmarkDto.SeriesId,
                     ChapterId = bookmarkDto.ChapterId,
                  });
               }
               else
               {
                   userBookmark.Page = bookmarkDto.Page;
                   userBookmark.SeriesId = bookmarkDto.SeriesId;
                   userBookmark.VolumeId = bookmarkDto.VolumeId;
               }

               _unitOfWork.UserRepository.Update(user);

               if (await _unitOfWork.CommitAsync())
               {
                  return Ok();
               }
            }
            catch (Exception)
            {
               await _unitOfWork.RollbackAsync();
            }

            return BadRequest("Could not save bookmark");
        }

        [HttpPost("unbookmark")]
        public async Task<ActionResult> UnBookmarkPage(BookmarkDto bookmarkDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());

            if (user.Bookmarks == null) return Ok();
            try {
                user.Bookmarks = user.Bookmarks.Where(x =>
                    x.ChapterId == bookmarkDto.ChapterId
                    && x.AppUserId == user.Id
                    && x.Page != bookmarkDto.Page).ToList();


                _unitOfWork.UserRepository.Update(user);

                if (await _unitOfWork.CommitAsync())
                {
                    return Ok();
                }
            }
            catch (Exception)
            {
                await _unitOfWork.RollbackAsync();
            }

            return BadRequest("Could not remove bookmark");
        }

        /// <summary>
        /// Returns the next logical chapter from the series.
        /// </summary>
        /// <example>
        /// V1 → V2 → V3 chapter 0 → V3 chapter 10 → SP 01 → SP 02
        /// </example>
        /// <param name="seriesId"></param>
        /// <param name="volumeId"></param>
        /// <param name="currentChapterId"></param>
        /// <returns>chapter id for next manga</returns>
        [HttpGet("next-chapter")]
        public async Task<ActionResult<int>> GetNextChapter(int seriesId, int volumeId, int currentChapterId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            var volumes = await _unitOfWork.SeriesRepository.GetVolumesDtoAsync(seriesId, user.Id);
            var currentVolume = await _unitOfWork.SeriesRepository.GetVolumeAsync(volumeId);
            var currentChapter = await _unitOfWork.VolumeRepository.GetChapterAsync(currentChapterId);
            if (currentVolume.Number == 0)
            {
                // Handle specials by sorting on their Filename aka Range
                var chapterId = GetNextChapterId(currentVolume.Chapters.OrderBy(x => x.Range, _naturalSortComparer), currentChapter.Number);
                if (chapterId > 0) return Ok(chapterId);
            }

            foreach (var volume in volumes)
            {
                if (volume.Number == currentVolume.Number && volume.Chapters.Count > 1)
                {
                    // Handle Chapters within current Volume
                    // In this case, i need 0 first because 0 represents a full volume file.
                    var chapterId = GetNextChapterId(currentVolume.Chapters.OrderBy(x => double.Parse(x.Number), _chapterSortComparerForInChapterSorting), currentChapter.Number);
                    if (chapterId > 0) return Ok(chapterId);
                }

                if (volume.Number == currentVolume.Number + 1)
                {
                    // Handle Chapters within next Volume
                    // ! When selecting the chapter for the next volume, we need to make sure a c0 comes before a c1+
                    var chapters = volume.Chapters.OrderBy(x => double.Parse(x.Number), _chapterSortComparer).ToList();
                    if (currentChapter.Number.Equals("0") && chapters.Last().Number.Equals("0"))
                    {
                        return chapters.Last().Id;
                    }

                    return Ok(chapters.FirstOrDefault()?.Id);
                }
            }
            return Ok(-1);
        }

        private static int GetNextChapterId(IEnumerable<Chapter> chapters, string currentChapterNumber)
        {
            var next = false;
            var chaptersList = chapters.ToList();
            foreach (var chapter in chaptersList)
            {
                if (next)
                {
                    return chapter.Id;
                }
                if (currentChapterNumber.Equals(chapter.Number)) next = true;
            }

            return -1;
        }

        /// <summary>
        /// Returns the previous logical chapter from the series.
        /// </summary>
        /// <example>
        /// V1 ← V2 ← V3 chapter 0 ← V3 chapter 10 ← SP 01 ← SP 02
        /// </example>
        /// <param name="seriesId"></param>
        /// <param name="volumeId"></param>
        /// <param name="currentChapterId"></param>
        /// <returns>chapter id for next manga</returns>
        [HttpGet("prev-chapter")]
        public async Task<ActionResult<int>> GetPreviousChapter(int seriesId, int volumeId, int currentChapterId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            var volumes = await _unitOfWork.SeriesRepository.GetVolumesDtoAsync(seriesId, user.Id);
            var currentVolume = await _unitOfWork.SeriesRepository.GetVolumeAsync(volumeId);
            var currentChapter = await _unitOfWork.VolumeRepository.GetChapterAsync(currentChapterId);

            if (currentVolume.Number == 0)
            {
                var chapterId = GetNextChapterId(currentVolume.Chapters.OrderBy(x => x.Range, _naturalSortComparer).Reverse(), currentChapter.Number);
                if (chapterId > 0) return Ok(chapterId);
            }

            foreach (var volume in volumes.Reverse())
            {
                if (volume.Number == currentVolume.Number)
                {
                    var chapterId = GetNextChapterId(currentVolume.Chapters.OrderBy(x => double.Parse(x.Number), _chapterSortComparerForInChapterSorting).Reverse(), currentChapter.Number);
                    if (chapterId > 0) return Ok(chapterId);
                }
                if (volume.Number == currentVolume.Number - 1)
                {
                    return Ok(volume.Chapters.OrderBy(x => double.Parse(x.Number), _chapterSortComparerForInChapterSorting).LastOrDefault()?.Id);
                }
            }
            return Ok(-1);
        }

    }
}
