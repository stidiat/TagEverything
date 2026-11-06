using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TagEverything.Core.Models
{
    public class FileRecord : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string OriginalName { get; set; } = string.Empty;
        public string SavedName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long Size { get; set; }
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string FileType => string.IsNullOrEmpty(System.IO.Path.GetExtension(OriginalName))
            ? "无扩展名"
            : System.IO.Path.GetExtension(OriginalName).TrimStart('.').ToUpperInvariant();
        public string Hash { get; set; } = string.Empty;
        public string? FileIdentity { get; set; }
        public bool IsMissing { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string DisplayName => IsMissing ? $"{OriginalName}（文件缺失）" : OriginalName;
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string? AvailableRelativePath => IsMissing ? null : RelativePath;
        public ulong PHash { get; set; }
        public bool IsVideo { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public DateTime FileCreationTime { get; set; }
        public DateTime FileModificationTime { get; set; }
        public string MetadataJson { get; set; } = string.Empty;
        public byte[]? Thumbnail { get; set; }

        private bool _isSelected;
        // This property is ignored by EF Core and used purely for UI binding state
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public bool IsSelected 
        { 
            get => _isSelected; 
            set 
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICollection<Tag> Tags { get; set; } = new List<Tag>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
