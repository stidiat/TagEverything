using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Services;
using TagEverything.Core.Models;
using TagEverything.UI.ViewModels;
using TagEverything.UI.Views;

namespace TagEverything.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly DuplicateDetectionService _duplicateDetectionService;

        private CancellationTokenSource? _importCts;
        private readonly Queue<(List<string> Paths, bool Register)> _pendingImports = new();
        private readonly List<ImportQueueFailure> _failedImports = new();
        private bool _closed;
        private bool _isDragging = false;
        private Point _startPoint;

        public MainWindow(
            MainViewModel viewModel,
            DuplicateDetectionService duplicateDetectionService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _duplicateDetectionService = duplicateDetectionService;
            DataContext = _viewModel;

            this.DragEnter += (s, e) => {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible;
            };
            this.DragLeave += (s, e) => DropOverlay.Visibility = Visibility.Collapsed;
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _importCts?.Cancel();
            _viewModel.Dispose();
            base.OnClosed(e);
        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewModel.IsListView)
            {
                // Sync View Model SelectedFilesCount with actual selected items count
                _viewModel.SelectedFilesCount = _viewModel.Files.Count(f => f.IsSelected);
            }
        }

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel.IsListView)
            {
                _viewModel.SelectedFile = FilesDataGrid.SelectedItem as FileRecord;
                _viewModel.SelectedFilesCount = FilesDataGrid.SelectedItems.Count;
            }
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                await RunImportsAsync(files, false);
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "选择要导入的文件"
            };

            if (dialog.ShowDialog() == true)
            {
                await RunImportsAsync(dialog.FileNames, false);
            }
        }

        private void MainScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only start dragging if clicking on the background, not on a ListBoxItem, DataGridRow or ScrollBar
            var isClickingOnItemOrScroll = false;
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is ListBoxItem || element is MaterialDesignThemes.Wpf.Card || element is System.Windows.Controls.DataGridRow || element is System.Windows.Controls.Primitives.ScrollBar)
                {
                    isClickingOnItemOrScroll = true;
                    break;
                }
                element = VisualTreeHelper.GetParent(element);
            }

            if (!isClickingOnItemOrScroll)
            {
                // If we click on an empty space, clear selection
                foreach (var item in _viewModel.Files)
                {
                    item.IsSelected = false;
                }

                if (_viewModel.IsListView)
                {
                    FilesDataGrid.SelectedItems.Clear();
                }

                _isDragging = true;
                _startPoint = e.GetPosition(SelectionCanvas);
                SelectionBox.Width = 0;
                SelectionBox.Height = 0;
                Canvas.SetLeft(SelectionBox, _startPoint.X);
                Canvas.SetTop(SelectionBox, _startPoint.Y);
                SelectionBox.Visibility = Visibility.Visible;
                
                // Need to focus listbox/datagrid to ensure keyboard events or selections work
                if (_viewModel.IsListView)
                    FilesDataGrid.Focus();
                else
                    FilesListBox.Focus();
                
                // Instead of capturing mouse immediately (which steals from scrollbar), we wait until they actually drag
            }
        }

        private void MainScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(SelectionCanvas);

                var x = Math.Min(currentPoint.X, _startPoint.X);
                var y = Math.Min(currentPoint.Y, _startPoint.Y);
                var width = Math.Abs(currentPoint.X - _startPoint.X);
                var height = Math.Abs(currentPoint.Y - _startPoint.Y);

                // Add a small threshold before we consider it a drag to avoid stealing simple clicks on the scrollbar
                if (width > 5 || height > 5)
                {
                    if (!MainGrid.IsMouseCaptured)
                    {
                        MainGrid.CaptureMouse();
                    }

                    Canvas.SetLeft(SelectionBox, x);
                    Canvas.SetTop(SelectionBox, y);
                    SelectionBox.Width = width;
                    SelectionBox.Height = height;

                    // Ensure the listbox updates during drag
                    UpdateSelection(new Rect(x, y, width, height));
                }
            }
            else if (_isDragging && e.LeftButton == MouseButtonState.Released)
            {
                EndDrag();
            }
        }

        private void MainScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }

        private void EndDrag()
        {
            if (_isDragging)
            {
                _isDragging = false;
                SelectionBox.Visibility = Visibility.Collapsed;
                MainGrid.ReleaseMouseCapture();
            }
        }

        private void UpdateSelection(Rect rect)
        {
            if (_viewModel.IsListView)
            {
                UpdateDataGridSelection(rect);
                return;
            }

            if (FilesListBox.Items.Count == 0) return;

            for (int i = 0; i < FilesListBox.Items.Count; i++)
            {
                var item = FilesListBox.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                var dataItem = FilesListBox.Items[i] as FileRecord;

                if (item != null && item.IsLoaded && dataItem != null)
                {
                    try
                    {
                        var transform = item.TransformToVisual(SelectionCanvas);
                        var itemBounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

                        bool intersects = rect.IntersectsWith(itemBounds);
                        
                        if (dataItem.IsSelected != intersects)
                        {
                            dataItem.IsSelected = intersects;
                            item.IsSelected = intersects; // 强制同步 ListBoxItem 的视觉状态
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        private void UpdateDataGridSelection(Rect rect)
        {
            if (FilesDataGrid.Items.Count == 0) return;

            for (int i = 0; i < FilesDataGrid.Items.Count; i++)
            {
                var row = FilesDataGrid.ItemContainerGenerator.ContainerFromIndex(i) as System.Windows.Controls.DataGridRow;
                var dataItem = FilesDataGrid.Items[i] as FileRecord;

                if (row != null && row.IsLoaded && dataItem != null)
                {
                    try
                    {
                        var transform = row.TransformToVisual(SelectionCanvas);
                        var itemBounds = transform.TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));

                        bool intersects = rect.IntersectsWith(itemBounds);
                        
                        if (intersects)
                        {
                            if (!FilesDataGrid.SelectedItems.Contains(dataItem))
                            {
                                FilesDataGrid.SelectedItems.Add(dataItem);
                            }
                        }
                        else
                        {
                            if (FilesDataGrid.SelectedItems.Contains(dataItem))
                            {
                                FilesDataGrid.SelectedItems.Remove(dataItem);
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        private async Task RunImportsAsync(IEnumerable<string> paths, bool register)
        {
            _pendingImports.Enqueue((paths.ToList(), register));
            if (_viewModel.IsImporting) return;
            _viewModel.IsImporting = true;
            _importCts = new CancellationTokenSource();
            var root = App.Settings.Current.LibraryPath;
            try
            {
                while (_pendingImports.TryDequeue(out var batch))
                {
                    var progress = new Progress<ImportQueueProgress>(p =>
                    {
                        _viewModel.ImportStatus = $"{p.Index}/{p.Total} · {p.FileName} · {p.Stage}";
                        _viewModel.ImportProgress = p.Percent;
                    });
                    var queue = App.ServiceProvider.GetRequiredService<ImportQueueService>();
                    var result = await queue.RunAsync(batch.Paths, batch.Register, async (context, token) =>
                    {
                        using var scope = App.ServiceProvider.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<TagEverything.Core.Data.LibraryDbContext>();
                        var feedback = App.ServiceProvider.GetRequiredService<SimilarityFeedbackService>();
                        var duplicate = await _duplicateDetectionService.FindDuplicateAsync(db, context.FileHash, context.PHash, token,
                            DuplicateDetectionService.ReadHashes(context.MetadataJson, context.PHash), context.IsVideo,
                            App.Settings.Current.SimilarityThreshold, await feedback.ReadAsync(root, token));
                        if (duplicate.File != null)
                        {
                            var comparison = new DuplicateComparisonWindow(duplicate.File, context, duplicate.IsExact) { Owner = this };
                            comparison.ShowDialog();
                            if (!comparison.ProceedWithImport) return;
                            if (comparison.MarkAsDifferent) await feedback.DismissAsync(root, duplicate.File.Hash, context.FileHash, token);
                        }
                        token.ThrowIfCancellationRequested();
                        var details = new FileDetailsWindow(context, root, token) { Owner = this };
                        details.ShowDialog();
                        if (!details.Confirmed && details.ImportError != null) throw details.ImportError;
                        if (details.Confirmed) await _viewModel.LoadFilesAsync();
                    }, progress, _importCts.Token);
                    _failedImports.AddRange(result.Failures);
                    _viewModel.FailedImportsCount = _failedImports.Count;
                    _viewModel.ImportFailures = string.Join("\n", _failedImports.Select(f => $"{System.IO.Path.GetFileName(f.Path)}：{f.Error}"));
                }
                _viewModel.ImportStatus = _failedImports.Count == 0 ? "导入队列已完成" : $"{_failedImports.Count} 个文件失败，可点击重试";
            }
            catch (OperationCanceledException) { _pendingImports.Clear(); _viewModel.ImportStatus = "已取消剩余导入任务"; }
            finally
            {
                _importCts.Dispose();
                _importCts = null;
                _viewModel.IsImporting = false;
                if (!_closed)
                {
                    App.ServiceProvider.GetRequiredService<LibraryMonitorService>().RequestRefresh();
                    await _viewModel.LoadFilesAsync();
                }
            }
        }

        private void CancelImport_Click(object sender, RoutedEventArgs e) => _importCts?.Cancel();
        private async void ImportUntracked_Click(object sender, RoutedEventArgs e)
        {
            var monitor = App.ServiceProvider.GetRequiredService<LibraryMonitorService>();
            await RunImportsAsync(monitor.UntrackedFiles.ToList(), true);
        }
        private async void RetryImports_Click(object sender, RoutedEventArgs e)
        {
            var failed = _failedImports.ToList();
            _failedImports.Clear();
            _viewModel.FailedImportsCount = 0;
            foreach (var group in failed.GroupBy(f => f.RegisterInPlace)) await RunImportsAsync(group.Select(f => f.Path), group.Key);
        }
        private void DeleteSelectedFiles_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = _viewModel.Files.Where(f => f.IsSelected).ToList();
            if (selectedItems.Any())
            {
                _viewModel.DeleteMultipleFilesCommand.Execute(selectedItems);
            }
        }

        private void EditSelectedTags_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = _viewModel.Files.Where(f => f.IsSelected).ToList();
            if (selectedItems.Any() && _viewModel.EditMultipleTagsCommand.CanExecute(selectedItems))
            {
                _viewModel.EditMultipleTagsCommand.Execute(selectedItems);
            }
        }

        private void DeleteSelectedFilesDataGrid_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FilesDataGrid.SelectedItems.Cast<FileRecord>().ToList();
            if (selectedItems.Any())
            {
                _viewModel.DeleteMultipleFilesCommand.Execute(selectedItems);
            }
        }

        private void EditSelectedTagsDataGrid_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FilesDataGrid.SelectedItems.Cast<FileRecord>().ToList();
            if (selectedItems.Any() && _viewModel.EditMultipleTagsCommand.CanExecute(selectedItems))
            {
                _viewModel.EditMultipleTagsCommand.Execute(selectedItems);
            }
        }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = SearchBox.Text;
            
            // Match #tagname followed by space or comma
            // Also match if it's at the end of the string when Enter is pressed (handled in KeyDown)
            var regex = new Regex(@"#([^, ]+)[, ]");
            var matches = regex.Matches(text);

            if (matches.Count > 0)
            {
                bool changed = false;
                foreach (Match match in matches)
                {
                    var tag = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        if (!_viewModel.SearchTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            _viewModel.SearchTags.Add(tag);
                        }
                        
                        // Remove the full matched string (including the trailing space/comma)
                        text = text.Replace(match.Value, "");
                        changed = true;
                    }
                }

                if (changed)
                {
                    // Clean up trailing/leading separators left behind by replace
                    text = text.Trim(new[] { ' ', ',' });
                    
                    SearchBox.Text = text;
                    SearchBox.CaretIndex = text.Length;
                    _viewModel.SearchQuery = text;
                    
                    _viewModel.UpdateFilteredAvailableTags();
                    
                }
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var text = SearchBox.Text;
                
                // If user hits enter and there's a pending tag at the end without a trailing space
                var regex = new Regex(@"#([^, ]+)$");
                var match = regex.Match(text);

                if (match.Success)
                {
                    var tag = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        if (!_viewModel.SearchTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            _viewModel.SearchTags.Add(tag);
                        }
                        
                        text = text.Replace(match.Value, "");
                        SearchBox.Text = text;
                        SearchBox.CaretIndex = text.Length;
                        _viewModel.SearchQuery = text;
                        _viewModel.UpdateFilteredAvailableTags();
                    }
                }

                // Trigger search on enter
                _ = _viewModel.LoadFilesAsync();
                TagSuggestionsPopup.IsOpen = false;
            }
        }

        private void SearchBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.FilteredAvailableTags.Any() && !TagSuggestionsPopup.IsOpen)
            {
                TagSuggestionsPopup.IsOpen = true;
            }
        }

        private void TagSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string tagName)
            {
                if (!_viewModel.SearchTags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                {
                    _viewModel.SearchTags.Add(tagName);
                    _viewModel.UpdateFilteredAvailableTags();
                    _ = _viewModel.LoadFilesAsync();
                }
                
                // Keep focus in search box to continue typing
                SearchBox.Focus();
                
                // Close popup if no more tags are available
                if (!_viewModel.FilteredAvailableTags.Any())
                {
                    TagSuggestionsPopup.IsOpen = false;
                }
            }
        }
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (TagSuggestionsPopup.IsOpen)
            {
                // If they click outside the popup and outside the search box, close the popup
                var clickedElement = e.OriginalSource as DependencyObject;
                
                bool clickedInsidePopup = false;
                bool clickedInsideSearchBox = false;

                var parent = clickedElement;
                while (parent != null)
                {
                    if (parent == TagSuggestionsPopup.Child)
                    {
                        clickedInsidePopup = true;
                        break;
                    }
                    if (parent == SearchBox)
                    {
                        clickedInsideSearchBox = true;
                        break;
                    }
                    parent = VisualTreeHelper.GetParent(parent);
                }

                if (!clickedInsidePopup && !clickedInsideSearchBox)
                {
                    TagSuggestionsPopup.IsOpen = false;
                }
            }
        }
    }
}
