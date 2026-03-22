using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.UI.Views
{
    public partial class TagEditWindow : Window
    {
        public ObservableCollection<string> Tags { get; set; } = new ObservableCollection<string>();
        
        private readonly FileRecord _fileRecord;

        public TagEditWindow(FileRecord record)
        {
            _fileRecord = record;
            InitializeComponent();
            DataContext = _fileRecord;
            TagsList.ItemsSource = Tags;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_fileRecord.Tags != null)
            {
                foreach (var tag in _fileRecord.Tags)
                {
                    Tags.Add(tag.Name);
                }
            }
        }

        private void TagInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ProcessTagInput();
                e.Handled = true;
            }
        }

        private void TagInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TagInputBox.Text.Contains(","))
            {
                ProcessTagInput();
            }
        }

        private void ProcessTagInput()
        {
            var text = TagInputBox.Text;
            var parts = text.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

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

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            ProcessTagInput();
            SaveButton.IsEnabled = false;

            try
            {
                using var scope = App.ServiceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                var record = await dbContext.Files.Include(f => f.Tags).FirstOrDefaultAsync(f => f.Id == _fileRecord.Id);
                if (record != null)
                {
                    // Remove existing tags that are not in the new list
                    var toRemove = record.Tags.Where(t => !Tags.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                    foreach (var tag in toRemove)
                    {
                        record.Tags.Remove(tag);
                    }

                    // Add new tags
                    foreach (var tagName in Tags)
                    {
                        if (!record.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var dbTag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower());
                            if (dbTag == null)
                            {
                                dbTag = new Tag { Name = tagName };
                                dbContext.Tags.Add(dbTag);
                            }
                            record.Tags.Add(dbTag);
                        }
                    }

                    await dbContext.SaveChangesAsync();
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}");
                SaveButton.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Do NOT mess with tracking context here, just close
            DialogResult = false;
            Close();
        }
    }
}