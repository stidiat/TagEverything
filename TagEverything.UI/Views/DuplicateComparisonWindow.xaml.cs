using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TagEverything.Core.Models;

namespace TagEverything.UI.Views
{
    public class ComparisonItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public Brush HighlightBrush { get; set; } = Brushes.Black;
    }

    public partial class DuplicateComparisonWindow : Window
    {
        public bool ProceedWithImport { get; private set; } = false;

        public ImageSource? ExistingThumbnailImage { get; set; }
        public ImageSource? NewThumbnailImage { get; set; }

        public string ExistingFileName { get; set; } = string.Empty;
        public string NewFileName { get; set; } = string.Empty;

        public ObservableCollection<ComparisonItem> ExistingMetadata { get; set; } = new ObservableCollection<ComparisonItem>();
        public ObservableCollection<ComparisonItem> NewMetadata { get; set; } = new ObservableCollection<ComparisonItem>();

        public string MatchTypeText { get; set; } = "疑似重复文件";
        public Brush MatchTypeBrush { get; set; } = Brushes.Red;

        public DuplicateComparisonWindow(FileRecord existingFile, FileImportContext newFile, bool isExactMatch = true)
        {
            InitializeComponent();
            DataContext = this;

            if (isExactMatch)
            {
                MatchTypeText = "检测到完全重复 (Hash或大小相同)";
                MatchTypeBrush = Brushes.Red;
            }
            else
            {
                MatchTypeText = "检测到高度相似 (内容或画面相近)";
                MatchTypeBrush = Brushes.Orange;
            }

            ExistingFileName = existingFile.OriginalName;
            NewFileName = Path.GetFileName(newFile.FilePath);

            if (existingFile.Thumbnail != null && existingFile.Thumbnail.Length > 0)
            {
                ExistingThumbnailImage = LoadImage(existingFile.Thumbnail);
            }
            if (newFile.Thumbnail != null && newFile.Thumbnail.Length > 0)
            {
                NewThumbnailImage = LoadImage(newFile.Thumbnail);
            }

            BuildComparisonData(existingFile, newFile);
        }

        private void BuildComparisonData(FileRecord existingFile, FileImportContext newFile)
        {
            var matchBrush = Brushes.Red;
            var normalBrush = TryFindResource("MaterialDesignBody") as Brush ?? Brushes.Black;

            // Size
            var existingSize = FormatSize(existingFile.Size);
            var newSize = FormatSize(newFile.Size);
            bool sizeMatch = existingSize == newSize;

            ExistingMetadata.Add(new ComparisonItem { Key = "文件大小", Value = existingSize, HighlightBrush = sizeMatch ? matchBrush : normalBrush });
            NewMetadata.Add(new ComparisonItem { Key = "文件大小", Value = newSize, HighlightBrush = sizeMatch ? matchBrush : normalBrush });

            // Hash
            bool hashMatch = existingFile.Hash == newFile.FileHash;
            ExistingMetadata.Add(new ComparisonItem { Key = "SHA-256", Value = existingFile.Hash, HighlightBrush = hashMatch ? matchBrush : normalBrush });
            NewMetadata.Add(new ComparisonItem { Key = "SHA-256", Value = newFile.FileHash, HighlightBrush = hashMatch ? matchBrush : normalBrush });

            // Video metadata
            if (existingFile.IsVideo && newFile.IsVideo)
            {
                var existingJson = ParseJsonSafe(existingFile.MetadataJson);
                var newJson = ParseJsonSafe(newFile.MetadataJson);

                CompareField("时长 (秒)", existingJson?["Duration"]?.ToString(), newJson?["Duration"]?.ToString(), matchBrush, normalBrush);
                CompareField("分辨率", existingJson?["Resolution"]?.ToString(), newJson?["Resolution"]?.ToString(), matchBrush, normalBrush);
                CompareField("码率", existingJson?["Bitrate"]?.ToString(), newJson?["Bitrate"]?.ToString(), matchBrush, normalBrush);
                CompareField("帧率", existingJson?["Framerate"]?.ToString(), newJson?["Framerate"]?.ToString(), matchBrush, normalBrush);
            }
            else
            {
                // Dates
                var exCreate = existingFile.FileCreationTime.ToLocalTime().ToString("g");
                var newCreate = newFile.CreationTime.ToLocalTime().ToString("g");
                bool cMatch = exCreate == newCreate;
                ExistingMetadata.Add(new ComparisonItem { Key = "创建时间", Value = exCreate, HighlightBrush = cMatch ? matchBrush : normalBrush });
                NewMetadata.Add(new ComparisonItem { Key = "创建时间", Value = newCreate, HighlightBrush = cMatch ? matchBrush : normalBrush });

                var exMod = existingFile.FileModificationTime.ToLocalTime().ToString("g");
                var newMod = newFile.LastWriteTime.ToLocalTime().ToString("g");
                bool mMatch = exMod == newMod;
                ExistingMetadata.Add(new ComparisonItem { Key = "修改时间", Value = exMod, HighlightBrush = mMatch ? matchBrush : normalBrush });
                NewMetadata.Add(new ComparisonItem { Key = "修改时间", Value = newMod, HighlightBrush = mMatch ? matchBrush : normalBrush });
            }
        }

        private void CompareField(string key, string? exVal, string? newVal, Brush matchBrush, Brush normalBrush)
        {
            if (string.IsNullOrEmpty(exVal) && string.IsNullOrEmpty(newVal)) return;

            exVal ??= "未知";
            newVal ??= "未知";

            bool match = exVal == newVal && exVal != "未知";
            ExistingMetadata.Add(new ComparisonItem { Key = key, Value = exVal, HighlightBrush = match ? matchBrush : normalBrush });
            NewMetadata.Add(new ComparisonItem { Key = key, Value = newVal, HighlightBrush = match ? matchBrush : normalBrush });
        }

        private JObject? ParseJsonSafe(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<JObject>(json);
            }
            catch
            {
                return null;
            }
        }

        private string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private BitmapImage LoadImage(byte[] imageData)
        {
            var image = new BitmapImage();
            using (var mem = new MemoryStream(imageData))
            {
                mem.Position = 0;
                image.BeginInit();
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = null;
                image.StreamSource = mem;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            ProceedWithImport = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            ProceedWithImport = false;
            Close();
        }
    }
}