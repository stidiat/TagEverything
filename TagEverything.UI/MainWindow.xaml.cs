using System;
using System.Collections.Generic;
using System.Linq;
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
            if (sender is ListBox listBox)
            {
                _viewModel.SelectedFilesCount = listBox.SelectedItems.Count;
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
            // Only start dragging if clicking on the background, not on a ListBoxItem
            var isClickingOnItem = false;
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is ListBoxItem || element is MaterialDesignThemes.Wpf.Card)
                {
                    isClickingOnItem = true;
                    break;
                }
                element = VisualTreeHelper.GetParent(element);
            }

            if (!isClickingOnItem)
            {
                // If we click on an empty space, clear selection
                FilesListBox.SelectedItems.Clear();

                _isDragging = true;
                _startPoint = e.GetPosition(SelectionCanvas);
                SelectionBox.Width = 0;
                SelectionBox.Height = 0;
                Canvas.SetLeft(SelectionBox, _startPoint.X);
                Canvas.SetTop(SelectionBox, _startPoint.Y);
                SelectionBox.Visibility = Visibility.Visible;
                
                // Need to focus listbox to ensure keyboard events or selections work
                FilesListBox.Focus();
                
                // Capture mouse to ensure we get all mouse move events even if we leave the window slightly
                MainScrollViewer.CaptureMouse();
                e.Handled = true;
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

                Canvas.SetLeft(SelectionBox, x);
                Canvas.SetTop(SelectionBox, y);
                SelectionBox.Width = width;
                SelectionBox.Height = height;

                // Ensure the listbox updates during drag
                UpdateSelection(new Rect(x, y, width, height));
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
                MainScrollViewer.ReleaseMouseCapture();
            }
        }

        private void UpdateSelection(Rect rect)
        {
            if (FilesListBox.Items.Count == 0) return;

            for (int i = 0; i < FilesListBox.Items.Count; i++)
            {
                var item = FilesListBox.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;

                if (item != null && item.IsLoaded)
                {
                    try
                    {
                        var transform = item.TransformToAncestor(SelectionCanvas);
                        var itemBounds = transform.TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

                        bool intersects = rect.IntersectsWith(itemBounds);
                        
                        if (intersects)
                        {
                            if (!item.IsSelected) item.IsSelected = true;
                        }
                        else
                        {
                            if (item.IsSelected) item.IsSelected = false;
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
            // Show FileDetailsWindow
            var detailsWindow = new FileDetailsWindow(filePath);
            detailsWindow.Owner = this;
            
            // Show non-blocking
            detailsWindow.Show();
            
            // Wait for it to close and check if confirmed
            detailsWindow.Closed += async (s, e) =>
            {
                if (detailsWindow.Confirmed)
                {
                    await _viewModel.LoadFilesAsync();
                }
            };
        }

        private void DeleteSelectedFiles_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && 
                menuItem.Parent is ContextMenu contextMenu && 
                contextMenu.PlacementTarget is ListBox listBox)
            {
                var selectedItems = listBox.SelectedItems.Cast<FileRecord>().ToList();
                if (selectedItems.Any())
                {
                    _viewModel.DeleteMultipleFilesCommand.Execute(selectedItems);
                }
            }
        }
    }
}
