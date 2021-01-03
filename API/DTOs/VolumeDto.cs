﻿using System.Collections.Generic;

namespace API.DTOs
{
    public class VolumeDto
    {
        public int Id { get; set; }
        public int Number { get; set; }
        public string Name { get; set; }
        public string CoverImage { get; set; }
        public ICollection<string> Files { get; set; }
    }
}