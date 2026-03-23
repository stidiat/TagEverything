using System.IO;

namespace TagEverything.Core.Models
{
    public class FileImportContext
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileHash { get; set; } = string.Empty;
        public byte[]? Thumbnail { get; set; }
        public bool IsVideo { get; set; }
        public string MetadataJson { get; set; } = string.Empty;
        public long Size { get; set; }
        public System.DateTime CreationTime { get; set; }
        public System.DateTime LastWriteTime { get; set; }
    }
}