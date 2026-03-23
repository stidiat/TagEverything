using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;

namespace TagEverything.UI.Views
{
    public partial class FileDetailsWindow : Window
    {
        public FileImportContext ImportContext { get; private set; }
        public bool Confirmed { get; private set; } = false;

        public ObservableCollection<string> Tags { get; set; } = new ObservableCollection<string>();

        public FileDetailsWindow(FileImportContext context)
        {
            ImportContext = context;
            InitializeComponent();
            DataContext = this;
            TagsList.ItemsSource = Tags;
        }

        public string FilePath => ImportContext.FilePath;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Populate UI
                AddMetadataItem("文件大小", FormatSize(ImportContext.Size));
                AddMetadataItem("SHA-256", ImportContext.FileHash);

                if (ImportContext.IsVideo)
                {
                    var dict = JsonConvert.DeserializeObject<JObject>(ImportContext.MetadataJson);
                    if (dict != null)
                    {
                        if (dict["Duration"] != null) AddMetadataItem("时长 (秒)", dict["Duration"]?.ToString() ?? "");
                        if (dict["Resolution"] != null) AddMetadataItem("分辨率", dict["Resolution"]?.ToString() ?? "");
                        if (dict["Bitrate"] != null) AddMetadataItem("码率", dict["Bitrate"]?.ToString() ?? "");
                        if (dict["Codec"] != null) AddMetadataItem("编码", dict["Codec"]?.ToString() ?? "");
                        if (dict["Framerate"] != null) AddMetadataItem("帧率", dict["Framerate"]?.ToString() ?? "");
                    }
                }
                else
                {
                    AddMetadataItem("创建时间", ImportContext.CreationTime.ToString("g"));
                    AddMetadataItem("修改时间", ImportContext.LastWriteTime.ToString("g"));
                }

                ConfirmButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解析文件失败: {ex.Message}");
                Close();
            }
        }

        private void AddMetadataItem(string label, string value)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(new TextBlock { Text = $"{label}: ", FontWeight = FontWeights.Bold, Width = 100 });
            panel.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
            MetadataPanel.Children.Add(panel);
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

        private void TagInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Just prevent default beep or whatever, don't process tag
                e.Handled = true;
            }
        }

        private void TagInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TagInputBox.Text.Contains(",") || TagInputBox.Text.Contains(" "))
            {
                ProcessTagInput();
            }
        }

        private void ProcessTagInput()
        {
            var text = TagInputBox.Text;
            var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var tag = part.Trim();
                if (!string.IsNullOrEmpty(tag) && !Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    Tags.Add(tag);
                }
            }

            TagInputBox.Text = string.Empty;
        }

        private void RemoveTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string tag)
            {
                Tags.Remove(tag);
            }
        }

        private async void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ProcessTagInput(); // Ensure any remaining text is added as a tag

            ConfirmButton.IsEnabled = false;

            try
            {
                var fileService = App.ServiceProvider.GetRequiredService<FileManagementService>();
                var (savedName, relativePath) = await fileService.ImportFileAsync(ImportContext.FilePath, false);

                using var scope = App.ServiceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                var fileRecord = new FileRecord
                {
                    OriginalName = Path.GetFileName(ImportContext.FilePath),
                    SavedName = savedName,
                    RelativePath = relativePath,
                    Size = ImportContext.Size,
                    Hash = ImportContext.FileHash,
                    IsVideo = ImportContext.IsVideo,
                    MetadataJson = ImportContext.MetadataJson,
                    Thumbnail = ImportContext.Thumbnail,
                    FileCreationTime = ImportContext.CreationTime.ToUniversalTime(),
                    FileModificationTime = ImportContext.LastWriteTime.ToUniversalTime()
                };

                foreach (var tagName in Tags)
                {
                    var tag = dbContext.Tags.FirstOrDefault(t => t.Name == tagName);
                    if (tag == null)
                    {
                        tag = new Tag { Name = tagName };
                        dbContext.Tags.Add(tag);
                    }
                    fileRecord.Tags.Add(tag);
                }

                dbContext.Files.Add(fileRecord);
                await dbContext.SaveChangesAsync();

                Confirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}");
                ConfirmButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
