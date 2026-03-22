using System;
using System.Collections.Generic;

namespace TagEverything.Core.Models
{
    public class FileRecord
    {
        public int Id { get; set; }
        public string OriginalName { get; set; } = string.Empty;
        public string SavedName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Hash { get; set; } = string.Empty;
        public bool IsVideo { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public DateTime FileCreationTime { get; set; }
        public DateTime FileModificationTime { get; set; }
        public string MetadataJson { get; set; } = string.Empty;
        public byte[]? Thumbnail { get; set; }

        public ICollection<Tag> Tags { get; set; } = new List<Tag>();
    }
}
