using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
    public partial class TagEditWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<string> TagsToAdd { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<SelectableTag> ExistingTags { get; set; } = new ObservableCollection<SelectableTag>();
        
        public bool HasExistingTags => ExistingTags.Any();

        private readonly FileRecord _fileRecord;

        public TagEditWindow(FileRecord record)
        {
            _fileRecord = record;
            InitializeComponent();
            DataContext = this;
        }

        public string FilePath => _fileRecord.OriginalName;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_fileRecord.Tags != null)
            {
                foreach (var tag in _fileRecord.Tags)
                {
                    ExistingTags.Add(new SelectableTag { Name = tag.Name });
                }
                OnPropertyChanged(nameof(HasExistingTags));
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
                if (!string.IsNullOrEmpty(tag) && !TagsToAdd.Contains(tag, StringComparer.OrdinalIgnoreCase)
                    && !ExistingTags.Any(et => string.Equals(et.Name, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    TagsToAdd.Add(tag);
                }
            }

            TagInputBox.Text = string.Empty;
        }

        private void RemoveNewTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string tag)
            {
                TagsToAdd.Remove(tag);
            }
        }

        private void ToggleExistingTagRemoval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is SelectableTag tag)
            {
                tag.IsMarkedForRemoval = !tag.IsMarkedForRemoval;
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
                    using var lease = await TagEverything.Core.Services.LibraryWriteLock.AcquireAsync(App.Settings.Current.LibraryPath);
                    await App.ServiceProvider.GetRequiredService<TagEverything.Core.Services.TagService>()
                        .ApplyAsync(dbContext, new[] { record }, TagsToAdd, ExistingTags.Where(t => t.IsMarkedForRemoval).Select(t => t.Name));
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
            DialogResult = false;
            Close();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
