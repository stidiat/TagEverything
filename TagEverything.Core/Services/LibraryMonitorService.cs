using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services
{
    public class LibraryMonitorService : IDisposable
    {
        private string _libraryPath;
        private readonly FileSystemWatcher _watcher;
        private readonly IServiceProvider _serviceProvider;

        public event EventHandler? LibraryChanged;

        public LibraryMonitorService(string libraryPath, IServiceProvider serviceProvider)
        {
            _libraryPath = libraryPath;
            _serviceProvider = serviceProvider;

            if (!Directory.Exists(_libraryPath))
            {
                Directory.CreateDirectory(_libraryPath);
            }

            _watcher = new FileSystemWatcher(_libraryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*"
            };

            _watcher.Created += OnFileCreated;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;

            _watcher.EnableRaisingEvents = true;
        }

        public void UpdatePath(string newPath)
        {
            _watcher.EnableRaisingEvents = false;
            _libraryPath = newPath;
            if (!Directory.Exists(_libraryPath)) Directory.CreateDirectory(_libraryPath);
            _watcher.Path = _libraryPath;
            _watcher.EnableRaisingEvents = true;
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            // Ignore DB changes
            if (e.Name != null && e.Name.Contains("TagEverythingData")) return;

            Task.Run(async () =>
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                
                var oldRelative = Path.GetRelativePath(_libraryPath, e.OldFullPath);
                var record = dbContext.Files.FirstOrDefault(f => f.RelativePath == oldRelative);
                if (record != null)
                {
                    record.RelativePath = Path.GetRelativePath(_libraryPath, e.FullPath);
                    await dbContext.SaveChangesAsync();
                    LibraryChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            if (e.Name != null && e.Name.Contains("TagEverythingData")) return;

            Task.Run(async () =>
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                    
                    var relative = Path.GetRelativePath(_libraryPath, e.FullPath);
                    var record = dbContext.Files.FirstOrDefault(f => f.RelativePath == relative);
                    if (record != null)
                    {
                        dbContext.Files.Remove(record);
                        await dbContext.SaveChangesAsync();
                        LibraryChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (Exception)
                {
                    // Ignore concurrency exceptions if the file was already deleted by the UI thread
                }
            });
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            if (e.Name != null && e.Name.Contains("TagEverythingData")) return;

            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
