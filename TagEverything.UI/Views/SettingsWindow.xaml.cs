using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Services;

namespace TagEverything.UI.Views
{
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private bool _switching;
        public bool PathChanged { get; private set; } = false;
        public int SimilarityThreshold
        {
            get => App.Settings.Current.SimilarityThreshold;
            set { App.Settings.Current.SimilarityThreshold = Math.Clamp(value, 0, 64); OnPropertyChanged(); }
        }

        public string LibraryPath
        {
            get => App.Settings.Current.LibraryPath;
            set
            {
                App.Settings.Current.LibraryPath = value;
                OnPropertyChanged();
            }
        }

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private async void ChangePath_Click(object sender, RoutedEventArgs e)
        {
            if (_switching) return;
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "选择新的库文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = App.Settings.Current.LibraryPath
            };

            if (dialog.ShowDialog() == true && dialog.SelectedPath != App.Settings.Current.LibraryPath)
            {
                _switching = true;
                ChangePathButton.IsEnabled = false;
                var previousPath = App.Settings.Current.LibraryPath;
                var fileService = App.ServiceProvider.GetRequiredService<FileManagementService>();
                var monitorService = App.ServiceProvider.GetRequiredService<LibraryMonitorService>();

                try
                {
                    var recovery = await App.ServiceProvider.GetRequiredService<LibrarySwitchService>()
                        .SwitchAsync(previousPath, dialog.SelectedPath, path =>
                        {
                            LibraryPath = path;
                            App.Settings.Save();
                            fileService.UpdateLibraryRoot(path);
                            monitorService.UpdatePath(path);
                        });
                    if (recovery.Count > 0) MessageBox.Show(string.Join("\n", recovery), "库恢复报告");
                    PathChanged = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"无法切换库目录：{ex.Message}",
                        "切换失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            App.Settings.Save();
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_switching) e.Cancel = true;
            else
            {
                try { App.Settings.Save(); }
                catch (Exception ex) { e.Cancel = true; MessageBox.Show($"保存设置失败：{ex.Message}"); }
            }
            base.OnClosing(e);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
