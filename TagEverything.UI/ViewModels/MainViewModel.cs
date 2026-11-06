using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    }

    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private const int PageSize = 100;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LibraryMonitorService _monitorService;
        private readonly FileManagementService _fileManagementService;
        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _refreshCts;
        private bool _disposed;
        [ObservableProperty]
        private int _untrackedFilesCount;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand), nameof(RefreshCommand))]
        private bool _isImporting;
        [ObservableProperty]
        private int _failedImportsCount;
        [ObservableProperty]
        private string _importFailures = string.Empty;
        [ObservableProperty]
        private string _importStatus = string.Empty;
        [ObservableProperty]
        private double _importProgress;

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
                    OnPropertyChanged(nameof(HasFilters));
                    CurrentPage = 1;
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

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
        private int _currentPage = 1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
        private int _totalPages = 1;

        [ObservableProperty]
        private int _totalFiles;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand), nameof(RefreshCommand))]
        private bool _isLoading;

        public bool HasFilters => SearchTags.Count > 0 || !string.IsNullOrWhiteSpace(SearchQuery);
        private bool CanPreviousPage() => !IsLoading && CurrentPage > 1;
        private bool CanNextPage() => !IsLoading && CurrentPage < TotalPages;
        private bool CanRefresh() => !IsLoading && !IsImporting;
        private bool CanOpenSettings() => !IsImporting;

        [RelayCommand(CanExecute = nameof(CanRefresh))]
        private async Task RefreshAsync()
        {
            IsLoading = true;
            using var cancellation = new CancellationTokenSource();
            _refreshCts = cancellation;
            try
            {
                await _monitorService.ReconcileAsync(cancellation.Token);
                UntrackedFilesCount = _monitorService.UntrackedFiles.Count;
                App.ServiceProvider.GetRequiredService<TagEverything.UI.Helpers.ThumbnailStoreService>().ClearCache();
                App.ServiceProvider.GetRequiredService<TagEverything.UI.Helpers.ShellPreviewService>().ClearCache();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!_disposed) MessageBox.Show($"重新核对库失败：{ex.Message}"); }
            finally { _refreshCts = null; await LoadFilesAsync(); }
        }

        [RelayCommand]
        private async Task ClearFiltersAsync()
        {
            SearchTags.Clear();
            SearchQuery = string.Empty;
            _searchCts?.Cancel();
            CurrentPage = 1;
            UpdateFilteredAvailableTags();
            await LoadFilesAsync();
        }
        public string PageStatus => TotalFiles == 0
            ? "0 个文件"
            : $"第 {CurrentPage} / {TotalPages} 页（共 {TotalFiles} 个文件）";

        public MainViewModel(
            IServiceScopeFactory scopeFactory,
            LibraryMonitorService monitorService,
            FileManagementService fileManagementService)
        {
            _scopeFactory = scopeFactory;
            _monitorService = monitorService;
            UntrackedFilesCount = monitorService.UntrackedFiles.Count;
            _fileManagementService = fileManagementService;
            SearchTags.CollectionChanged += OnSearchTagsChanged;
            _monitorService.LibraryChanged += OnLibraryChanged;
            _monitorService.MonitoringError += OnMonitoringError;
            _ = LoadFilesAsync();
        }

        private async void DebouncedSearchAsync()
        {
            var cts = new CancellationTokenSource();
            var previousSearch = Interlocked.Exchange(ref _searchCts, cts);
            previousSearch?.Cancel();
            var token = cts.Token;

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
            finally
            {
                Interlocked.CompareExchange(ref _searchCts, null, cts);
                cts.Dispose();
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
            if (_disposed) return;
            IsLoading = true;
            var cts = new CancellationTokenSource();
            var previousLoad = Interlocked.Exchange(ref _loadCts, cts);
            previousLoad?.Cancel();
            var token = cts.Token;

            var selectedTags = SearchTags.ToList();
            var searchQuery = SearchQuery;
            var requestedPage = Math.Max(1, CurrentPage);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                var summaries = await dbContext.Tags
                    .AsNoTracking()
                    .Where(t => t.Files.Any())
                    .Select(t => new TagSummary { Name = t.Name, Count = t.Files.Count })
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Name)
                    .ToListAsync(token);

                var result = await new FileCatalogService().LoadPageAsync(dbContext, searchQuery, selectedTags, requestedPage, PageSize, token);
                var files = result.Files;
                var page = result.Page;
                var totalPages = result.TotalPages;
                var totalFiles = result.TotalFiles;
                token.ThrowIfCancellationRequested();

                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _disposed) return;
                    AvailableTags.Clear();
                    foreach (var summary in summaries)
                    {
                        AvailableTags.Add(summary);
                    }

                    CurrentPage = page;
                    TotalPages = totalPages;
                    TotalFiles = totalFiles;
                    OnPropertyChanged(nameof(PageStatus));

                    SelectedFile = null;
                    SelectedFilesCount = 0;
                    Files.Clear();
                    foreach (var file in files)
                    {
                        Files.Add(file);
                    }

                    UpdateFilteredAvailableTags();
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A newer search or refresh superseded this load.
            }
            catch (Exception ex)
            {
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _disposed) return;
                    MessageBox.Show(
                        $"加载文件失败: {ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _loadCts, null, cts), cts))
                {
                    IsLoading = _refreshCts != null;
                }
                cts.Dispose();
            }
        }

        [RelayCommand(CanExecute = nameof(CanPreviousPage))]
        private async Task PreviousPageAsync()
        {
            if (CurrentPage <= 1) return;
            CurrentPage--;
            await LoadFilesAsync();
        }

        [RelayCommand(CanExecute = nameof(CanNextPage))]
        private async Task NextPageAsync()
        {
            if (CurrentPage >= TotalPages) return;
            CurrentPage++;
            await LoadFilesAsync();
        }

        [RelayCommand(CanExecute = nameof(CanOpenSettings))]
        private async Task OpenSettingsAsync()
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = Application.Current.MainWindow
            };
            
            settingsWindow.ShowDialog();
            
            if (settingsWindow.PathChanged)
            {
                await ClearFiltersAsync();
            }
        }

        [RelayCommand]
        private void OpenFile(FileRecord record)
        {
            if (record == null) return;
            if (record.IsMissing)
            {
                MessageBox.Show("文件缺失。可以通过右键菜单重新关联。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var fullPath = _fileManagementService.GetLibraryFilePath(record.RelativePath);
                if (!File.Exists(fullPath))
                {
                    MessageBox.Show("文件已不存在。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件: {ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool CanRelinkFile(FileRecord? record) => record?.IsMissing == true;

        [RelayCommand(CanExecute = nameof(CanRelinkFile))]
        private async Task RelinkFileAsync(FileRecord? record)
        {
            if (record?.IsMissing != true) return;
            var root = App.Settings.Current.LibraryPath;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择库内与原文件内容相同的文件",
                InitialDirectory = root
            };
            if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
            try
            {
                await App.ServiceProvider.GetRequiredService<LibraryOperationsService>().RelinkAsync(root, record.Id, dialog.FileName);
                _monitorService.RequestRefresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重新关联失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            await LoadFilesAsync();
        }

        [RelayCommand]
        private async Task EditTagsAsync(FileRecord record)
        {
            if (record == null) return;

            var editWindow = new TagEditWindow(record)
            {
                Owner = Application.Current.MainWindow
            };
            editWindow.ShowDialog();
            await LoadFilesAsync();
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
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

                    var tagsToAdd = editWindow.TagsToAdd.ToList();
                    var tagsToRemove = editWindow.CommonTags.Where(t => t.IsMarkedForRemoval).Select(t => t.Name).ToList();

                    var fileIds = selectedList.Select(f => f.Id).ToList();
                    var dbFiles = await dbContext.Files.Include(f => f.Tags).Where(f => fileIds.Contains(f.Id)).ToListAsync();

                    using var writeLease = await LibraryWriteLock.AcquireAsync(App.Settings.Current.LibraryPath);
                    await new TagService().ApplyAsync(dbContext, dbFiles, tagsToAdd, tagsToRemove);
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

            var prompt = record.IsMissing
                ? $"确定要移除缺失文件 \"{record.OriginalName}\" 的记录及其标签关联吗？"
                : $"确定要删除文件 \"{record.OriginalName}\" 吗？\n注意：这将同时从您的库文件夹中永久删除该文件！";
            var result = MessageBox.Show(prompt,
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await DeleteRecordsAsync(new[] { record });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    await LoadFilesAsync();
                }
            }
        }
        [RelayCommand]
        private async Task DeleteMultipleFilesAsync(object param)
        {
            IEnumerable<FileRecord>? records = null;
            if (param is IEnumerable<FileRecord> collection)
            {
                // Card view passes the complete Files collection; DataGrid passes only
                // SelectedItems and does not mirror selection into FileRecord.IsSelected.
                records = ReferenceEquals(param, Files)
                    ? collection.Where(f => f.IsSelected).ToList()
                    : collection.ToList();
            }

            if (records == null || !records.Any()) return;

            var recordList = records.ToList();
            var count = recordList.Count;

            var missing = recordList.Count(f => f.IsMissing);
            var result = MessageBox.Show($"确定要移除这 {count} 条记录吗？\n其中 {count - missing} 个文件将永久删除，{missing} 条缺失记录将移除标签关联。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await DeleteRecordsAsync(recordList);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"批量删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    await LoadFilesAsync();
                }
            }
        }

        private async Task DeleteRecordsAsync(IReadOnlyCollection<FileRecord> records)
        {
            await App.ServiceProvider.GetRequiredService<LibraryOperationsService>()
                .DeleteAsync(App.Settings.Current.LibraryPath, records);
            await LoadFilesAsync();
        }
        private void OnSearchTagsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            CurrentPage = 1;
            OnPropertyChanged(nameof(HasFilters));
        }

        private void OnLibraryChanged(object? sender, EventArgs e)
        {
            if (_disposed) return;
            App.Current.Dispatcher.InvokeAsync(async () =>
            {
                UntrackedFilesCount = _monitorService.UntrackedFiles.Count;
                await LoadFilesAsync();
            });
        }

        private void OnMonitoringError(object? sender, LibraryMonitorErrorEventArgs e)
        {
            if (_disposed) return;
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                    MessageBox.Show($"{e.Operation}失败: {e.Exception.Message}", "库监控错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SearchTags.CollectionChanged -= OnSearchTagsChanged;
            _monitorService.LibraryChanged -= OnLibraryChanged;
            _monitorService.MonitoringError -= OnMonitoringError;
            _searchCts?.Cancel();
            _loadCts?.Cancel();
            _refreshCts?.Cancel();
        }
    }
}
