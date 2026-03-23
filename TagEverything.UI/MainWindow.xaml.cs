using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using TagEverything.Core.Models;
using TagEverything.UI.ViewModels;
using TagEverything.UI.Views;

namespace TagEverything.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        private bool _isDragging = false;
        private Point _startPoint;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            this.DragEnter += (s, e) => {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropOverlay.Visibility = Visibility.Visible;
            };
            this.DragLeave += (s, e) => DropOverlay.Visibility = Visibility.Collapsed;
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
                foreach (var file in files)
                {
                    await HandleFileImportAsync(file);
                }
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
                foreach (var file in dialog.FileNames)
                {
                    await HandleFileImportAsync(file);
                }
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

        private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scv)
            {
                scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
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

        private async System.Threading.Tasks.Task HandleFileImportAsync(string filePath)
        {
            var metaService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TagEverything.Core.Services.MetadataExtractionService>(App.ServiceProvider);
            var hashService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TagEverything.Core.Services.HashService>(App.ServiceProvider);
            
            var fileInfo = new System.IO.FileInfo(filePath);
            bool isVideo = metaService.IsVideoFile(fileInfo.Extension);

            try
            {
                var fileHash = await hashService.CalculateSha256Async(filePath);
                var metadataJson = await metaService.ExtractMetadataAsync(filePath, isVideo);
                var thumbnail = await metaService.ExtractThumbnailAsync(filePath, fileInfo.Extension);

                var importContext = new FileImportContext
                {
                    FilePath = filePath,
                    FileHash = fileHash,
                    Thumbnail = thumbnail,
                    IsVideo = isVideo,
                    MetadataJson = metadataJson,
                    Size = fileInfo.Length,
                    CreationTime = fileInfo.CreationTime,
                    LastWriteTime = fileInfo.LastWriteTime
                };

                using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(App.ServiceProvider);
                var dbContext = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<TagEverything.Core.Data.LibraryDbContext>(scope.ServiceProvider);
                
                // Find duplicate
                var similarFile = dbContext.Files.FirstOrDefault(f => f.Hash == fileHash || (f.Size == importContext.Size && f.Size > 0));

                if (similarFile != null)
                {
                    var duplicateWindow = new DuplicateComparisonWindow(similarFile, importContext)
                    {
                        Owner = this
                    };
                    
                    duplicateWindow.ShowDialog();
                    
                    if (!duplicateWindow.ProceedWithImport)
                    {
                        return; // Cancelled
                    }
                }

                // Show FileDetailsWindow
                var detailsWindow = new FileDetailsWindow(importContext);
                detailsWindow.Owner = this;
                
                // Use ShowDialog to block the loop until this file is completely processed
                detailsWindow.ShowDialog();
                
                if (detailsWindow.Confirmed)
                {
                    await _viewModel.LoadFilesAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"解析文件失败: {ex.Message}");
            }
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
                    
                    // Trigger search
                    _ = _viewModel.LoadFilesAsync();
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
