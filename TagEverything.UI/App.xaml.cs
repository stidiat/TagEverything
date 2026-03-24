﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        private void OnStartup(object sender, StartupEventArgs e)
        {
            // Pre-load SQLite library from bin folder if it exists
            string sqlitePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "e_sqlite3.dll");
            if (File.Exists(sqlitePath))
            {
                NativeLibrary.Load(sqlitePath);
            }

            Settings = new SettingsService();

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

            // Run Migrations
            using (var scope = ServiceProvider.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                dbContext.Database.Migrate();
            }

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(Settings);
            
            services.AddDbContext<LibraryDbContext>((sp, options) => {
                var settings = sp.GetRequiredService<SettingsService>();
                var libraryPath = settings.Current.LibraryPath;
                var dataDir = Path.Combine(libraryPath, "TagEverythingData");
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                
                var oldDbPath = Path.Combine(libraryPath, "library.db");
                var currentDbPath = Path.Combine(dataDir, "library.db");
                if (File.Exists(oldDbPath) && !File.Exists(currentDbPath))
                {
                    File.Move(oldDbPath, currentDbPath);
                }
                
                options.UseSqlite($"Data Source={currentDbPath}");
            }, ServiceLifetime.Transient);

            services.AddSingleton<HashService>();
            services.AddSingleton<MetadataExtractionService>();
            services.AddSingleton(sp => new FileManagementService(sp.GetRequiredService<SettingsService>().Current.LibraryPath));
            services.AddSingleton(sp => new LibraryMonitorService(sp.GetRequiredService<SettingsService>().Current.LibraryPath, sp));

            services.AddTransient<MainViewModel>();
            services.AddTransient<MainWindow>();
        }
    }
}
