using System;
using System.IO;
using System.Threading.Tasks;

namespace TagEverything.Core.Services
{
    public class FileManagementService
    {
        private string _libraryRootPath;

        public FileManagementService(string libraryRootPath)
        {
            _libraryRootPath = libraryRootPath;
        }

        public void UpdateLibraryRoot(string libraryRootPath)
        {
            _libraryRootPath = libraryRootPath;
        }

        public async Task<(string savedName, string relativePath)> ImportFileAsync(string sourceFilePath, bool moveFile = false)
        {
            if (!Directory.Exists(_libraryRootPath))
            {
                Directory.CreateDirectory(_libraryRootPath);
            }

            var fileInfo = new FileInfo(sourceFilePath);
            var now = DateTime.UtcNow;
            
            // Generate {UTC时间戳}_{原始文件名}
            var timestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
            var savedName = $"{timestamp}_{fileInfo.Name}";

            // Subdirectory 按年/月 - Removed as per user request, files go directly to root
            var targetDirPath = _libraryRootPath;
            
            if (!Directory.Exists(targetDirPath))
            {
                Directory.CreateDirectory(targetDirPath);
            }

            var targetFilePath = Path.Combine(targetDirPath, savedName);
            var relativePath = savedName; // Just the filename since it's in the root

            // Using Task.Run to offload IO
            await Task.Run(() =>
            {
                if (moveFile)
                {
                    File.Move(sourceFilePath, targetFilePath);
                }
                else
                {
                    File.Copy(sourceFilePath, targetFilePath);
                }
            });

            return (savedName, relativePath);
        }
    }
}
