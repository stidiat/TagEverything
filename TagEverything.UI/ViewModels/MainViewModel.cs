using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;
using TagEverything.UI.Views;

namespace TagEverything.UI.ViewModels
{
    public class TagSummary
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public string DisplayText => $"{Name} ({Count})";
    }

    public partial class MainViewModel : ObservableObject
    {
        private readonly LibraryDbContext _dbContext;
        private readonly LibraryMonitorService _monitorService;
        private CancellationTokenSource? _searchCts;

        [ObservableProperty]
        private ObservableCollection<FileRecord> _files = new();

        [ObservableProperty]
        private ObservableCollection<string> _searchTags = new();

        [ObservableProperty]
        private ObservableCollection<TagSummary> _availableTags = new();

        [ObservableProperty]
        private ObservableCollection<TagSummary> _filteredAvailableTags = new();

        private string _searchQuery = string.Empty;
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetProperty(ref _searchQuery, value))
                {
                    DebouncedSearchAsync();
                }
            }
        }

        [ObservableProperty]
        private FileRecord? _selectedFile;

        [ObservableProperty]
        private int _selectedFilesCount = 0;

        [ObservableProperty]
        private bool _isListView = false;

        public MainViewModel(LibraryDbContext dbContext, LibraryMonitorService monitorService)
        {
            _dbContext = dbContext;
            _monitorService = monitorService;
            
            _monitorService.LibraryChanged += (s, e) => 
            {
                App.Current.Dispatcher.InvokeAsync(async () => await LoadFilesAsync());
            };

            LoadFilesAsync().ConfigureAwait(false);
        }

        private async void DebouncedSearchAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                // Wait for 300ms to debounce (allow IME to finish or user to stop typing)
                await Task.Delay(300, token);

                if (!token.IsCancellationRequested)
                {
                    await LoadFilesAsync();
                }
            }
            catch (TaskCanceledException)
            {
                // Ignored
            }
        }

        [RelayCommand]
        private void RemoveSearchTag(string tag)
        {
            if (SearchTags.Contains(tag))
            {
                SearchTags.Remove(tag);
                UpdateFilteredAvailableTags();
                _ = LoadFilesAsync(); // Re-trigger search
            }
        }

        public void UpdateFilteredAvailableTags()
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                FilteredAvailableTags.Clear();
                foreach (var tag in AvailableTags)
                {
                    if (!SearchTags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        FilteredAvailableTags.Add(tag);
                    }
                }
            });
        }

        public async Task LoadFilesAsync()
        {
            var query = _dbContext.Files.Include(f => f.Tags).AsQueryable();

            // Load available tags summary for the dropdown (before filtering)
            var allTags = await _dbContext.Tags.Include(t => t.Files).ToListAsync();
            var summaries = allTags
                .Where(t => t.Files.Count > 0)
                .Select(t => new TagSummary { Name = t.Name, Count = t.Files.Count })
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Name)
                .ToList();
            
            App.Current.Dispatcher.Invoke(() =>
            {
                AvailableTags.Clear();
                foreach (var summary in summaries)
                {
                    AvailableTags.Add(summary);
                }
            });
            UpdateFilteredAvailableTags();

            // Filter by selected tags
            foreach (var tag in SearchTags)
            {
                query = query.Where(f => f.Tags.Any(t => t.Name == tag));
            }

            // Filter by remaining query (filename)
            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                var keywords = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var keyword in keywords)
                {
                    // Fallback to legacy tag search if they somehow type #tag without space
                    var isTagSearch = keyword.StartsWith("#");
                    var searchWord = isTagSearch ? keyword.Substring(1) : keyword;

                    if (isTagSearch)
                    {
                        query = query.Where(f => f.Tags.Any(t => EF.Functions.Like(t.Name, $"%{searchWord}%")));
                    }
                    else
                    {
                        query = query.Where(f => EF.Functions.Like(f.OriginalName, $"%{searchWord}%"));
                    }
                }
            }

            var files = await query.OrderByDescending(f => f.AddedAt).AsNoTracking().ToListAsync();
            
            App.Current.Dispatcher.Invoke(() =>
            {
                Files.Clear();
                foreach (var file in files)
                {
                    Files.Add(file);
                }
            });
        }

        // Kept for backward compatibility if needed, but not strictly required anymore
        [RelayCommand]
        private async Task SearchAsync()
        {
            await LoadFilesAsync();
        }

        [RelayCommand]
        private async Task OpenSettingsAsync()
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = Application.Current.MainWindow
            };
            
            settingsWindow.ShowDialog();
            
            if (settingsWindow.PathChanged)
            {
                await LoadFilesAsync();
            }
        }

        [RelayCommand]
        private void OpenFile(FileRecord record)
        {
            if (record == null) return;

            var fullPath = Path.Combine(App.Settings.Current.LibraryPath, record.RelativePath);
            if (File.Exists(fullPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Handle exception
                }
            }
        }

        [RelayCommand]
        private async Task EditTagsAsync(FileRecord record)
        {
            if (record == null) return;

            var editWindow = new TagEditWindow(record)
            {
                Owner = Application.Current.MainWindow
            };
            if (editWindow.ShowDialog() == true)
            {
                await LoadFilesAsync();
            }
            else
            {
                await LoadFilesAsync();
            }
        }

        [RelayCommand]
        private async Task EditMultipleTagsAsync(object param)
        {
            try
            {
                var files = param as IEnumerable<FileRecord> ?? Files.Where(f => f.IsSelected);
                var selectedList = files.ToList();

                if (!selectedList.Any()) return;

                var editWindow = new BatchTagEditWindow(selectedList)
                {
                    Owner = Application.Current.MainWindow
                };

                if (editWindow.ShowDialog() == true)
                {
                    using var scope = App.ServiceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                    var tagsToAdd = editWindow.TagsToAdd.ToList();
                    var tagsToRemove = editWindow.CommonTags.Where(t => t.IsMarkedForRemoval).Select(t => t.Name).ToList();

                    var fileIds = selectedList.Select(f => f.Id).ToList();
                    var dbFiles = await dbContext.Files.Include(f => f.Tags).Where(f => fileIds.Contains(f.Id)).ToListAsync();

                    foreach (var dbFile in dbFiles)
                    {
                        // Remove tags
                        foreach (var tagToRemove in tagsToRemove)
                        {
                            var tag = dbFile.Tags.FirstOrDefault(t => t.Name == tagToRemove);
                            if (tag != null)
                            {
                                dbFile.Tags.Remove(tag);
                            }
                        }

                        // Add tags
                        foreach (var tagToAdd in tagsToAdd)
                        {
                            if (!dbFile.Tags.Any(t => string.Equals(t.Name, tagToAdd, StringComparison.OrdinalIgnoreCase)))
                            {
                                // First check the local context for tracking, then the database
                                var existingTag = dbContext.Tags.Local.FirstOrDefault(t => string.Equals(t.Name, tagToAdd, StringComparison.OrdinalIgnoreCase));
                                if (existingTag == null)
                                {
                                    existingTag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Name == tagToAdd);
                                }
                                
                                if (existingTag == null)
                                {
                                    existingTag = new Tag { Name = tagToAdd };
                                    dbContext.Tags.Add(existingTag);
                                }
                                dbFile.Tags.Add(existingTag);
                            }
                        }
                    }

                    await dbContext.SaveChangesAsync();
                    await LoadFilesAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"批量修改标签失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task DeleteFileAsync(FileRecord record)
        {
            if (record == null) return;

            var result = MessageBox.Show($"确定要删除文件 \"{record.OriginalName}\" 吗？\n注意：这将同时从您的库文件夹中永久删除该文件！", 
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // Delete from DB using ExecuteDeleteAsync (avoids concurrency tracking issues)
                    using var scope = App.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                    
                    await db.Files.Where(f => f.Id == record.Id).ExecuteDeleteAsync();

                    // Delete physical file LAST to prevent FileSystemWatcher from racing ahead
                    var fullPath = Path.Combine(App.Settings.Current.LibraryPath, record.RelativePath);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }

                    // The LibraryMonitorService might also trigger a refresh, but we force one anyway
                    // Delay slightly to avoid racing with FileSystemWatcher
                    await Task.Delay(100);
                    await LoadFilesAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        [RelayCommand]
        private async Task DeleteMultipleFilesAsync(object param)
        {
            // Because parameter might be the entire ObservableCollection from the binding,
            // we filter out only those that are selected
            IEnumerable<FileRecord>? records = null;
            if (param is IEnumerable<FileRecord> collection)
            {
                records = collection.Where(f => f.IsSelected).ToList();
            }

            if (records == null || !records.Any()) return;

            var recordList = records.ToList();
            var count = recordList.Count;

            var result = MessageBox.Show($"确定要删除这 {count} 个文件吗？\n注意：这将同时从您的库文件夹中永久删除这些文件！", 
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    using var scope = App.ServiceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                    var idsToDelete = recordList.Select(r => r.Id).ToList();
                    
                    // Delete from DB first using ExecuteDeleteAsync
                    await db.Files.Where(f => idsToDelete.Contains(f.Id)).ExecuteDeleteAsync();

                    // Then delete physical files
                    foreach (var record in recordList)
                    {
                        var fullPath = Path.Combine(App.Settings.Current.LibraryPath, record.RelativePath);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                        }
                    }

                    // Force refresh
                    // Delay slightly to avoid racing with FileSystemWatcher
                    await Task.Delay(100);
                    await LoadFilesAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"批量删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
