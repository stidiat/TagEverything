using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TagEverything.Core.Models;

namespace TagEverything.UI.Views
{
    public class SelectableTag : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        
        private bool _isMarkedForRemoval;
        public bool IsMarkedForRemoval
        {
            get => _isMarkedForRemoval;
            set
            {
                if (_isMarkedForRemoval != value)
                {
                    _isMarkedForRemoval = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Opacity));
                    OnPropertyChanged(nameof(TextDecoration));
                    OnPropertyChanged(nameof(IconKind));
                }
            }
        }

        public double Opacity => IsMarkedForRemoval ? 0.5 : 1.0;
        public TextDecorationCollection? TextDecoration => IsMarkedForRemoval ? TextDecorations.Strikethrough : null;
        public string IconKind => IsMarkedForRemoval ? "Undo" : "Close";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class BatchTagEditWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<string> TagsToAdd { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<SelectableTag> CommonTags { get; set; } = new ObservableCollection<SelectableTag>();

        public string HeaderText { get; set; }

        public bool HasCommonTags => CommonTags.Any();

        private readonly List<FileRecord> _selectedFiles;

        public BatchTagEditWindow(List<FileRecord> selectedFiles)
        {
            _selectedFiles = selectedFiles;
            HeaderText = $"正在批量编辑 {selectedFiles.Count} 个文件";
            
            InitializeComponent();
            DataContext = this;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Find common tags
            if (_selectedFiles.Count > 0)
            {
                var commonTagNames = _selectedFiles.First().Tags.Select(t => t.Name).ToList();

                foreach (var file in _selectedFiles.Skip(1))
                {
                    var fileTagNames = file.Tags.Select(t => t.Name).ToList();
                    commonTagNames = commonTagNames.Intersect(fileTagNames).ToList();
                }

                foreach (var tagName in commonTagNames)
                {
                    CommonTags.Add(new SelectableTag { Name = tagName });
                }

                OnPropertyChanged(nameof(HasCommonTags));
            }
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
                if (!string.IsNullOrEmpty(tag) && !TagsToAdd.Contains(tag, StringComparer.OrdinalIgnoreCase) 
                    && !CommonTags.Any(ct => string.Equals(ct.Name, tag, StringComparison.OrdinalIgnoreCase)))
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

        private void ToggleCommonTagRemoval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is SelectableTag tag)
            {
                tag.IsMarkedForRemoval = !tag.IsMarkedForRemoval;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ProcessTagInput(); // Ensure any remaining text is processed
            DialogResult = true;
            Close();
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