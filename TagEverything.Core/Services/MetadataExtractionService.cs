using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace TagEverything.Core.Services
{
    public class MetadataExtractionService
    {
        private bool _ffmpegInitialized = false;

        public async Task EnsureFFmpegInitializedAsync()
        {
            if (_ffmpegInitialized) return;

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            FFmpeg.SetExecutablesPath(appDir);

            if (!File.Exists(Path.Combine(appDir, "ffmpeg.exe")) || !File.Exists(Path.Combine(appDir, "ffprobe.exe")))
            {
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, appDir);
            }

            _ffmpegInitialized = true;
        }

        public async Task<string> ExtractMetadataAsync(string filePath, bool isVideo)
        {
            if (isVideo)
            {
                await EnsureFFmpegInitializedAsync();
                try
                {
                    var info = await FFmpeg.GetMediaInfo(filePath);
                    var videoStream = info.VideoStreams.FirstOrDefault();

                    if (videoStream != null)
                    {
                        var metadata = new
                        {
                            Duration = info.Duration.TotalSeconds,
                            Bitrate = videoStream.Bitrate,
                            Resolution = $"{videoStream.Width}x{videoStream.Height}",
                            Codec = videoStream.Codec,
                            Framerate = videoStream.Framerate,
                            Size = info.Size
                        };
                        return JsonConvert.SerializeObject(metadata);
                    }
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new { Error = ex.Message });
                }
            }

            var fileInfo = new FileInfo(filePath);
            var generalMetadata = new
            {
                Size = fileInfo.Length,
                CreationTime = fileInfo.CreationTimeUtc,
                ModificationTime = fileInfo.LastWriteTimeUtc
            };

            return JsonConvert.SerializeObject(generalMetadata);
        }

        public async Task<byte[]?> ExtractThumbnailAsync(string filePath, string extension)
        {
            try
            {
                if (IsVideoFile(extension))
                {
                    await EnsureFFmpegInitializedAsync();
                    var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
                    
                    var info = await FFmpeg.GetMediaInfo(filePath);
                    var snapshotTime = TimeSpan.FromSeconds(info.Duration.TotalSeconds > 5 ? 2 : info.Duration.TotalSeconds / 2);
                    
                    var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(filePath, tempFile, snapshotTime);
                    await conversion.Start();
                    
                    if (File.Exists(tempFile))
                    {
                        var bytes = await File.ReadAllBytesAsync(tempFile);
                        File.Delete(tempFile);
                        
                        using var image = Image.Load(bytes);
                        image.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Size = new Size(300, 0),
                            Mode = ResizeMode.Max
                        }));
                        using var ms = new MemoryStream();
                        image.SaveAsJpeg(ms);
                        return ms.ToArray();
                    }
                }
                else if (IsImageFile(extension))
                {
                    using var image = Image.Load(filePath);
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(300, 0),
                        Mode = ResizeMode.Max
                    }));
                    using var ms = new MemoryStream();
                    image.SaveAsJpeg(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                // Ignored, thumbnail generation can fail gracefully
            }
            return null;
        }

        public bool IsVideoFile(string extension)
        {
            var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv" };
            return videoExtensions.Contains(extension.ToLowerInvariant());
        }

        public bool IsImageFile(string extension)
        {
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
            return imageExtensions.Contains(extension.ToLowerInvariant());
        }
    }
}
