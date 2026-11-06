﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Ookii.Dialogs.Wpf;
using TagEverything.Core.Data;
using TagEverything.Core.Services;
using TagEverything.UI.Helpers;
using TagEverything.UI.ViewModels;

namespace TagEverything.UI
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;
        public static SettingsService Settings { get; private set; } = null!;

        public App()
        {
            // Register a custom assembly resolver to look in the bin subdirectory
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        private System.Reflection.Assembly? CurrentDomain_AssemblyResolve(object? sender, ResolveEventArgs args)
        {
            string assemblyName = new System.Reflection.AssemblyName(args.Name).Name + ".dll";
            string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", assemblyName);
            
            if (File.Exists(binPath))
            {
                return System.Reflection.Assembly.LoadFrom(binPath);
            }
            
            return null;
        }

        private async void OnStartup(object sender, StartupEventArgs e)
        {
            var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "bin");
            if (File.Exists(Path.Combine(nativeDirectory, "Magick.Native-Q8-x64.dll")))
                ImageMagick.MagickNET.SetNativeLibraryDirectory(nativeDirectory);

            // Pre-load SQLite library from bin folder if it exists
            string sqlitePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "e_sqlite3.dll");
            if (File.Exists(sqlitePath))
            {
                NativeLibrary.Load(sqlitePath);
            }

            Settings = new SettingsService();

            var libraryPathFromArguments = ResolveLibraryPathFromArguments(e.Args);
            if (!string.IsNullOrWhiteSpace(libraryPathFromArguments))
            {
                Settings.Current.LibraryPath = libraryPathFromArguments;
                Settings.Save();
            }

            if (string.IsNullOrEmpty(Settings.Current.LibraryPath))
            {
                var dialog = new VistaFolderBrowserDialog
                {
                    Description = "请选择库文件夹以存储文件和数据库",
                    UseDescriptionForTitle = true
                };

                if (dialog.ShowDialog() == true)
                {
                    Settings.Current.LibraryPath = dialog.SelectedPath;
                    Settings.Save();
                }
                else
                {
                    MessageBox.Show("必须选择库文件夹才能运行应用程序。");
                    Shutdown();
                    return;
                }
            }

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ServiceProvider = serviceCollection.BuildServiceProvider();

            try
            {
                var recovery = await ServiceProvider.GetRequiredService<LibrarySwitchService>()
                    .InitializeAsync(Settings.Current.LibraryPath);
                if (recovery.Count > 0) MessageBox.Show(string.Join("\n", recovery), "库恢复报告");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"无法初始化库数据库：{ex.Message}",
                    "启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(Settings);
            
            services.AddDbContext<LibraryDbContext>((sp, options) => {
                var settings = sp.GetRequiredService<SettingsService>();
                options.UseSqlite(LibraryDbContext.CreateConnectionString(
                    settings.Current.LibraryPath));
            }, ServiceLifetime.Transient);

            services.AddSingleton<LibraryOperationsService>();
            services.AddSingleton<LibrarySwitchService>();
            services.AddSingleton<TagService>();
            services.AddSingleton<ImportAnalysisService>();
            services.AddSingleton<ImportQueueService>();
            services.AddSingleton<SimilarityFeedbackService>();
            services.AddSingleton<ThumbnailStoreService>();
            services.AddSingleton<HashService>();
            services.AddSingleton<ShellPreviewService>();
            services.AddSingleton<MetadataExtractionService>();
            services.AddSingleton<DuplicateDetectionService>();
            services.AddSingleton(sp => new FileManagementService(sp.GetRequiredService<SettingsService>().Current.LibraryPath));
            services.AddSingleton(sp => new LibraryMonitorService(sp.GetRequiredService<SettingsService>().Current.LibraryPath, sp.GetRequiredService<ImportAnalysisService>()));

            services.AddTransient<MainViewModel>();
            services.AddTransient<MainWindow>();
        }

        private static string? ResolveLibraryPathFromArguments(string[] args)
        {
            var argument = args.FirstOrDefault(a =>
                Directory.Exists(a) ||
                string.Equals(Path.GetExtension(a), ".library", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(argument)) return null;

            if (Directory.Exists(argument))
            {
                return Path.GetFullPath(argument);
            }

            if (!File.Exists(argument)) return null;

            var markerDirectory = Path.GetDirectoryName(Path.GetFullPath(argument));

            try
            {
                var content = File.ReadAllText(argument).Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    string? configuredPath = null;
                    try
                    {
                        configuredPath = JsonConvert.DeserializeObject<AppSettings>(content)?.LibraryPath;
                    }
                    catch (JsonException)
                    {
                        configuredPath = content;
                    }

                    if (!string.IsNullOrWhiteSpace(configuredPath))
                    {
                        if (!Path.IsPathRooted(configuredPath) && markerDirectory != null)
                        {
                            configuredPath = Path.Combine(markerDirectory, configuredPath);
                        }

                        if (Directory.Exists(configuredPath))
                        {
                            return Path.GetFullPath(configuredPath);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // An empty/unreadable marker still identifies its containing folder.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to the marker's containing folder.
            }

            return markerDirectory;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (ServiceProvider is IDisposable disposableProvider)
            {
                disposableProvider.Dispose();
            }

            AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
            base.OnExit(e);
        }
    }
}
