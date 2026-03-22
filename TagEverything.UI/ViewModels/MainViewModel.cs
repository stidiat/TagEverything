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
    public partial class MainViewModel : ObservableObject
    {
        private readonly LibraryDbContext _dbContext;
        private readonly LibraryMonitorService _monitorService;
        private CancellationTokenSource? _searchCts;

        [ObservableProperty]
        private ObservableCollection<FileRecord> _files = new();

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

        public async Task LoadFilesAsync()
        {
            var query = _dbContext.Files.Include(f => f.Tags).AsQueryable();

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                var keywords = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var keyword in keywords)
                {
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
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "选择新的库文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = App.Settings.Current.LibraryPath
            };

            if (dialog.ShowDialog() == true && dialog.SelectedPath != App.Settings.Current.LibraryPath)
            {
                App.Settings.Current.LibraryPath = dialog.SelectedPath;
                App.Settings.Save();

                // Re-run migrations for new DB
                using (var scope = App.ServiceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                    dbContext.Database.Migrate();
                }

                // Update Monitor and FileService
                App.ServiceProvider.GetRequiredService<FileManagementService>().UpdateLibraryRoot(dialog.SelectedPath);
                _monitorService.UpdatePath(dialog.SelectedPath);

                // Reload
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

            var editWindow = new TagEditWindow(record);
            // Must be owned by MainWindow
            editWindow.Owner = Application.Current.MainWindow;
            
            if (editWindow.ShowDialog() == true)
            {
                // Give DB time to write changes or refresh to fetch correctly
                await LoadFilesAsync();
            }
            else 
            {
                // Even if cancelled, we should reload files just in case the context 
                // got messy or a background file watcher event fired during the dialog.
                // EF Core sometimes has tracked entities getting stale.
                await LoadFilesAsync();
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
        private async Task DeleteMultipleFilesAsync(IEnumerable<FileRecord> records)
        {
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
