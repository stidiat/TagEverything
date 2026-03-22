using System.Collections.Generic;

namespace TagEverything.Core.Models
{
    public class Tag
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        public ICollection<FileRecord> Files { get; set; } = new List<FileRecord>();
    }
}
