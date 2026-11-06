using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ImageMagick;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace TagEverything.Core.Services;

public sealed record MediaPreview(byte[]? Thumbnail, IReadOnlyList<ulong> DHashes)
{
    public ulong PrimaryHash => DHashes.FirstOrDefault();
}

public class MetadataExtractionService
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly string _appDirectory;
    private readonly string _cacheDirectory;
    private readonly Func<string, Task> _download;
    private Task? _downloadTask;
    private bool _initialized;

    public MetadataExtractionService() : this(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TagEverything", "ffmpeg")) { }

    public MetadataExtractionService(string appDirectory, string cacheDirectory, Func<string, Task>? download = null)
    {
        _appDirectory = appDirectory;
        _cacheDirectory = cacheDirectory;
        _download = download ?? (path => FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, path));
    }

    public static bool HasExecutablePair(string directory) =>
        File.Exists(Path.Combine(directory, "ffmpeg.exe")) && File.Exists(Path.Combine(directory, "ffprobe.exe"));

    public async Task EnsureFFmpegInitializedAsync(CancellationToken token = default)
    {
        if (_initialized) return;
        await _initialization.WaitAsync(token);
        try
        {
            if (_initialized) return;
            var location = new[] { Path.Combine(_appDirectory, "bin"), _appDirectory, _cacheDirectory }.FirstOrDefault(HasExecutablePair);
            if (location == null)
            {
                Directory.CreateDirectory(_cacheDirectory);
                try
                {
                    // The downloader has no cancellation API. A cancelled caller leaves the shared download reusable.
                    _downloadTask ??= _download(_cacheDirectory);
                    await _downloadTask.WaitAsync(TimeSpan.FromMinutes(2), token);
                    if (!HasExecutablePair(_cacheDirectory)) throw new IOException("下载结果缺少 ffmpeg.exe 或 ffprobe.exe。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (_downloadTask?.IsCompleted != false) _downloadTask = null;
                    throw new IOException("无法准备视频组件。离线时请将 ffmpeg.exe 和 ffprobe.exe 同时放入程序 bin 目录；仍可导入文件并使用系统图标。", ex);
                }
                location = _cacheDirectory;
            }
            FFmpeg.SetExecutablesPath(location);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    public async Task<string> ExtractMetadataAsync(string filePath, bool isVideo, CancellationToken token = default)
    {
        var file = new FileInfo(filePath);
        var json = JObject.FromObject(new { Size = file.Length, CreationTime = file.CreationTimeUtc, ModificationTime = file.LastWriteTimeUtc });
        if (isVideo)
        {
            try
            {
                await EnsureFFmpegInitializedAsync(token);
                var info = await FFmpeg.GetMediaInfo(filePath, token);
                var video = info.VideoStreams.FirstOrDefault();
                if (video != null)
                {
                    json["Duration"] = info.Duration.TotalSeconds;
                    json["Bitrate"] = video.Bitrate;
                    json["Resolution"] = $"{video.Width}x{video.Height}";
                    json["Codec"] = video.Codec;
                    json["Framerate"] = video.Framerate;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { json["Error"] = ex.Message; }
        }
        return json.ToString(Formatting.None);
    }

    private static ulong ComputeDHash(MagickImage image)
    {
        using var clone = image.Clone();
        clone.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true }, FilterType.Catrom);
        using var pixels = clone.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new IOException("无法读取预览像素。");
        int Luminance(int pixel)
        {
            var offset = pixel * 3;
            return (2126 * rgb[offset] + 7152 * rgb[offset + 1] + 722 * rgb[offset + 2] + 5000) / 10000;
        }
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var pixel = y * 9 + x;
                hash = (hash << 1) | (Luminance(pixel) > Luminance(pixel + 1) ? 1UL : 0UL);
            }
        return hash;
    }

    public async Task<MediaPreview> ExtractPreviewAsync(string path, string extension, CancellationToken token = default)
    {
        try
        {
            if (IsImageFile(extension)) return await Task.Run(() => ReadPreview(path), token);
            if (!IsVideoFile(extension)) return new(null, []);
            await EnsureFFmpegInitializedAsync(token);
            var info = await FFmpeg.GetMediaInfo(path, token);
            var hashes = new List<ulong>();
            byte[]? thumbnail = null;
            foreach (var fraction in new[] { .1, .5, .9 })
            {
                token.ThrowIfCancellationRequested();
                var temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
                try
                {
                    var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(path, temporary,
                        TimeSpan.FromSeconds(Math.Max(0, info.Duration.TotalSeconds * fraction)));
                    await conversion.Start(token);
                    var frame = await Task.Run(() => ReadPreview(temporary), token);
                    thumbnail ??= frame.Thumbnail;
                    hashes.AddRange(frame.DHashes);
                }
                finally
                {
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            return new(thumbnail, hashes);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(null, []); }
    }

    private static MediaPreview ReadPreview(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        var format = RasterFormat(header);
        if (format == MagickFormat.Unknown) return new(null, []);
        input.Position = 0;
        using var image = new MagickImage(input, new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1 });
        var hash = ComputeDHash(image);
        image.AutoOrient();
        image.Resize(480, 320);
        image.Strip();
        image.Quality = 80;
        return new(image.ToByteArray(MagickFormat.Jpeg), [hash]);
    }

    // Restrict the decoder to the supported raster formats, even for files with misleading extensions.
    private static MagickFormat RasterFormat(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return MagickFormat.Jpeg;
        if (header.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return MagickFormat.Png;
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)) return MagickFormat.Gif;
        if (header.StartsWith("BM"u8)) return MagickFormat.Bmp;
        if (header.StartsWith("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8)) return MagickFormat.WebP;
        if ((header[0] == 'I' && header[1] == 'I' && header[2] is 42 or 43 && header[3] == 0) ||
            (header[0] == 'M' && header[1] == 'M' && header[2] == 0 && header[3] is 42 or 43)) return MagickFormat.Tiff;
        return MagickFormat.Unknown;
    }

    public bool IsVideoFile(string extension) => new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" }.Contains(extension.ToLowerInvariant());
    public bool IsImageFile(string extension) => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" }.Contains(extension.ToLowerInvariant());
}
