using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;

var count = args.Length > 0 ? int.Parse(args[0]) : 10000;
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
var root = Path.Combine(Path.GetTempPath(), "TagEverythingBenchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await using var db = new LibraryDbContext(root);
    await db.Database.MigrateAsync();
    var tag = new Tag { Name = "benchmark" };
    var sampleThumbnail = new byte[4096];
    db.Files.AddRange(Enumerable.Range(0, count).Select(i => new FileRecord
    {
        OriginalName = $"sample-{i:D6}.jpg", RelativePath = $"sample-{i:D6}.jpg", Hash = i.ToString(),
        Size = 1024 * (i + 1), Tags = { tag }, Thumbnail = sampleThumbnail
    }));
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var catalog = new FileCatalogService();
    await catalog.LoadPageAsync(db, "sample", ["benchmark"], 1);
    var measurements = new List<double>();
    var allocated = GC.GetTotalAllocatedBytes(true);
    for (var i = 0; i < 30; i++)
    {
        var clock = Stopwatch.StartNew();
        var page = await catalog.LoadPageAsync(db, "sample", ["benchmark"], 1 + i % Math.Max(1, count / 100));
        if (page.Files.Any(f => f.Thumbnail != null)) throw new Exception("Catalog loaded thumbnail BLOBs eagerly.");
        measurements.Add(clock.Elapsed.TotalMilliseconds);
    }
    measurements.Sort();
    var result = new
    {
        RecordedAtUtc = DateTime.UtcNow, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString(),
        FileCount = count, ThumbnailBytesPerFile = sampleThumbnail.Length, PageSize = 100, Iterations = measurements.Count,
        MedianMilliseconds = measurements[measurements.Count / 2], P95Milliseconds = measurements[(int)(measurements.Count * .95)],
        AllocatedBytesPerPage = (GC.GetTotalAllocatedBytes(true) - allocated) / measurements.Count,
        Scope = "SQLite catalog count + filtered metadata/tag page. Excludes UI, disk media decoding and library seeding."
    };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (output != null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, json); }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
