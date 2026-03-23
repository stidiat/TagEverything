using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Data;
using TagEverything.Core.Services;

namespace TagEverything.UI.Views
{
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        public bool PathChanged { get; private set; } = false;

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

        private void ChangePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "选择新的库文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = App.Settings.Current.LibraryPath
            };

            if (dialog.ShowDialog() == true && dialog.SelectedPath != App.Settings.Current.LibraryPath)
            {
                LibraryPath = dialog.SelectedPath;
                App.Settings.Save();

                // Re-run migrations for new DB
                using (var scope = App.ServiceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                    dbContext.Database.Migrate();
                }

                // Update Monitor and FileService
                App.ServiceProvider.GetRequiredService<FileManagementService>().UpdateLibraryRoot(dialog.SelectedPath);
                App.ServiceProvider.GetRequiredService<LibraryMonitorService>().UpdatePath(dialog.SelectedPath);

                PathChanged = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}