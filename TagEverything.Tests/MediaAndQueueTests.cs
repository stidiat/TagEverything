using Microsoft.EntityFrameworkCore;
using ImageMagick;
using TagEverything.Core.Models;
using TagEverything.Core.Services;

namespace TagEverything.Tests;

public class MediaAndQueueTests
{
    [Fact]
    public async Task Media_RequiresBothExecutablesAndReusesOfflineCache()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var app = Path.Combine(library.Root, "app"); var cache = Path.Combine(library.Root, "cache");
        Directory.CreateDirectory(app); Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(Path.Combine(app, "ffmpeg.exe"), "fake");
        Assert.False(MetadataExtractionService.HasExecutablePair(app));
        await File.WriteAllTextAsync(Path.Combine(cache, "ffmpeg.exe"), "fake");
        await File.WriteAllTextAsync(Path.Combine(cache, "ffprobe.exe"), "fake");
        var downloads = 0;
        var service = new MetadataExtractionService(app, cache, _ => { downloads++; throw new IOException("offline"); });
        await service.EnsureFFmpegInitializedAsync();
        Assert.Equal(0, downloads);
        File.Delete(Path.Combine(cache, "ffprobe.exe"));
        var missing = new MetadataExtractionService(app, cache, _ => throw new IOException("offline"));
        var error = await Assert.ThrowsAsync<IOException>(() => missing.EnsureFFmpegInitializedAsync());
        Assert.Contains("离线", error.Message);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("tiff")]
    [InlineData("webp")]
    public async Task Media_ImageAnalysisHasDHashAndDetectsActualImage(string extension)
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var path = Path.Combine(library.Root, $"gradient.{extension}");
        using var image = new MagickImage("gradient:red-black", new MagickReadSettings { Width = 90, Height = 80 });
        image.Write(path);
        var context = await new ImportAnalysisService(new(), new()).AnalyzeAsync(path);
        Assert.NotNull(context.Thumbnail);
        Assert.Single(DuplicateDetectionService.ReadHashes(context.MetadataJson, context.PHash));
        Assert.Contains("dHash-64", context.MetadataJson);
    }

    [Fact]
    public async Task Database_LoadsPatchedNativeSqlite()
    {
        await using var library = new TestLibrary();
        await library.InitializeAsync();
        await using var db = library.Open();
        var version = await db.Database.SqlQueryRaw<string>("SELECT sqlite_version() AS Value").SingleAsync();
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2), $"Loaded SQLite {version}");
    }

    [Fact]
    public async Task Similarity_MultipleFramesThresholdAndFeedbackPreventFalsePositives()
    {
        Assert.Null(DuplicateDetectionService.Compare([1, 2, 3], [1, ulong.MaxValue, 3], 10));
        Assert.Null(DuplicateDetectionService.Compare([1, 2, 3], [1], 64));
        Assert.Equal(1, DuplicateDetectionService.Compare([1, 2, 3], [0, 3, 2], 1));
        Assert.Null(DuplicateDetectionService.Compare([1], [0], 0));
        await using var library = new TestLibrary(); await library.InitializeAsync();
        await using var db = library.Open();
        db.Files.Add(new FileRecord { Hash = "existing", PHash = 15 }); await db.SaveChangesAsync();
        var feedback = new SimilarityFeedbackService();
        await feedback.DismissAsync(library.Root, "incoming", "existing");
        var dismissed = await feedback.ReadAsync(library.Root);
        Assert.Contains(SimilarityFeedbackService.PairKey("existing", "incoming"), dismissed);
        var result = await new DuplicateDetectionService().FindDuplicateAsync(db, "incoming", 14, dismissedPairs: dismissed);
        Assert.Null(result.File);
        Assert.True((await new DuplicateDetectionService().FindDuplicateAsync(db, "existing", 14, dismissedPairs: dismissed)).IsExact);
    }

    [Fact]
    public async Task Queue_ContinuesAfterFailureAndSupportsRetryAndCancellation()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var source = await library.SourceAsync();
        var missing = Path.Combine(library.Root, "missing.txt");
        var queue = new ImportQueueService(new(new(), new()));
        var confirmations = 0;
        var result = await queue.RunAsync([missing, source.FilePath], false, (_, _) => { confirmations++; return Task.CompletedTask; });
        Assert.Single(result.Failures); Assert.Equal(1, confirmations);
        await File.WriteAllTextAsync(missing, "now available");
        var retry = await queue.RunAsync(result.Failures.Select(f => f.Path), false, (_, _) => Task.CompletedTask);
        Assert.Empty(retry.Failures); Assert.Equal(1, retry.Completed);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunAsync([missing, source.FilePath], false,
            (_, _) => { cancelled.Cancel(); return Task.CompletedTask; }, token: cancelled.Token));
    }

    [Fact]
    public async Task Catalog_PaginatesWithoutLoadingThumbnailBlobs()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        await using var db = library.Open();
        db.Files.AddRange(Enumerable.Range(0, 205).Select(i => new FileRecord { OriginalName = $"file{i}", Thumbnail = new byte[4096] }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await new FileCatalogService().LoadPageAsync(db, null, [], 999);
        Assert.Equal(3, result.Page); Assert.Equal(205, result.TotalFiles); Assert.Equal(5, result.Files.Count);
        Assert.All(result.Files, f => Assert.Null(f.Thumbnail));
        Assert.Empty(db.ChangeTracker.Entries());
    }
}
