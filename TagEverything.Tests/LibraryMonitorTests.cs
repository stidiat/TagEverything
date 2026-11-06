using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Services;

namespace TagEverything.Tests;

public class LibraryMonitorTests
{
    [Fact]
    public async Task Reconcile_RecoversUniqueOfflineRenameWithoutLosingTags()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var source = await library.SourceAsync("unique offline content");
        var record = await library.Operations.ImportAsync(library.Root, source, ["keep"]);
        var renamed = Path.Combine(library.Root, "renamed.txt");
        File.Move(Path.Combine(library.Root, record.RelativePath), renamed);
        using var monitor = new LibraryMonitorService(library.Root);
        try
        {
            await monitor.ReconcileAsync();
            await using var db = library.Open();
            var restored = await db.Files.Include(f => f.Tags).SingleAsync();
            Assert.Equal(record.Id, restored.Id); Assert.Equal("renamed.txt", restored.RelativePath);
            Assert.Equal("keep", Assert.Single(restored.Tags).Name);
        }
        finally { await monitor.StopAsync(); }
    }

    [Fact]
    public async Task Monitor_SynchronizesDirectoryRenameContentDeletionAndUntrackedFiles()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var folder = Path.Combine(library.Root, "before"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "file.txt"); await File.WriteAllTextAsync(path, "before");
        var analysis = new ImportAnalysisService(new(), new());
        var context = await analysis.AnalyzeAsync(path); context.RegisterInPlace = true;
        await library.Operations.ImportAsync(library.Root, context, ["keep"]);
        using var monitor = new LibraryMonitorService(library.Root, analysis);
        var events = 0; monitor.LibraryChanged += (_, _) => Interlocked.Increment(ref events);
        try
        {
            var after = Path.Combine(library.Root, "after"); Directory.Move(folder, after);
            await UntilAsync(async () => { await using var db = library.Open(); return (await db.Files.SingleAsync()).RelativePath == Path.Combine("after", "file.txt"); });
            var renamedPath = Path.Combine(after, "file.txt");
            await File.WriteAllTextAsync(renamedPath, "updated content");
            var expected = await new HashService().CalculateSha256Async(renamedPath);
            await UntilAsync(async () => { await using var db = library.Open(); return (await db.Files.SingleAsync()).Hash == expected; });
            var external = Path.Combine(library.Root, "new.txt"); await File.WriteAllTextAsync(external, "not managed");
            await UntilAsync(() => Task.FromResult(monitor.UntrackedFiles.Contains(external)));
            await using (var db = library.Open()) Assert.Equal(1, await db.Files.CountAsync());
            Directory.Delete(after, true);
            await UntilAsync(async () => { await using var db = library.Open(); return (await db.Files.SingleAsync()).IsMissing; });
            await using (var db = library.Open()) Assert.Equal("keep", Assert.Single((await db.Files.Include(f => f.Tags).SingleAsync()).Tags).Name);
            Assert.InRange(events, 1, 10);
        }
        finally { await monitor.StopAsync(); }
    }

    [Fact]
    public async Task Reconcile_PreservesAmbiguousOfflineRenamesAndAllowsExplicitRelink()
    {
        await using var library = new TestLibrary();
        await library.InitializeAsync();
        var source = await library.SourceAsync();
        var first = await library.Operations.ImportAsync(library.Root, source, ["first"]);
        var second = await library.Operations.ImportAsync(library.Root, source, ["second"]);
        var movedFirst = Path.Combine(library.Root, "first.txt");
        var movedSecond = Path.Combine(library.Root, "second.txt");
        File.Move(Path.Combine(library.Root, first.RelativePath), movedFirst);
        File.Move(Path.Combine(library.Root, second.RelativePath), movedSecond);
        using var monitor = new LibraryMonitorService(library.Root);
        try
        {
            await monitor.ReconcileAsync();
            await monitor.StopAsync();
            await using var db = library.Open();
            var missing = await db.Files.AsNoTracking().Include(f => f.Tags).OrderBy(f => f.Id).ToListAsync();
            Assert.Equal(2, missing.Count);
            Assert.All(missing, f => Assert.True(f.IsMissing));
            Assert.Equal(["first", "second"], missing.Select(f => Assert.Single(f.Tags).Name));
            Assert.Contains(movedFirst, monitor.UntrackedFiles);
            Assert.Contains(movedSecond, monitor.UntrackedFiles);

            await library.Operations.RelinkAsync(library.Root, first.Id, movedFirst);
            await Assert.ThrowsAsync<IOException>(() => library.Operations.RelinkAsync(library.Root, second.Id, movedFirst));
            var linked = await db.Files.AsNoTracking().Include(f => f.Tags).SingleAsync(f => f.Id == first.Id);
            Assert.False(linked.IsMissing);
            Assert.Equal("first.txt", linked.RelativePath);
            Assert.Equal("first", Assert.Single(linked.Tags).Name);

            // Removing a missing record never touches a file that has appeared at its former path.
            var replacement = Path.Combine(library.Root, second.RelativePath);
            await File.WriteAllTextAsync(replacement, "replacement");
            await library.Operations.DeleteAsync(library.Root, [missing[1]]);
            Assert.Equal("replacement", await File.ReadAllTextAsync(replacement));
            Assert.Equal(1, await db.Files.CountAsync());
        }
        finally { await monitor.StopAsync(); }
    }

    [Fact]
    public async Task FullReconcileFindsSameSizeAndTimestampChange_AndSwitchDropsOldEvents()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        await using var second = new TestLibrary(); await second.InitializeAsync();
        var path = Path.Combine(library.Root, "file.txt"); await File.WriteAllTextAsync(path, "aaaa");
        var analysis = new ImportAnalysisService(new(), new());
        var context = await analysis.AnalyzeAsync(path); context.RegisterInPlace = true;
        await library.Operations.ImportAsync(library.Root, context, []);
        using var monitor = new LibraryMonitorService(library.Root, analysis);
        try
        {
            await File.WriteAllTextAsync(path, "bbbb"); File.SetLastWriteTimeUtc(path, context.LastWriteTime);
            await monitor.ReconcileAsync();
            await using (var db = library.Open()) Assert.Equal(await new HashService().CalculateSha256Async(path), (await db.Files.SingleAsync()).Hash);
            monitor.UpdatePath(second.Root);
            var external = Path.Combine(second.Root, "new.txt"); await File.WriteAllTextAsync(external, "new library");
            await UntilAsync(() => Task.FromResult(monitor.UntrackedFiles.Contains(external)));
            Assert.DoesNotContain(path, monitor.UntrackedFiles);
            await using var previous = library.Open(); Assert.Single(await previous.Files.ToListAsync());
        }
        finally { await monitor.StopAsync(); }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(12)) { if (await condition()) return; await Task.Delay(80); }
        Assert.True(await condition(), "Monitor did not converge within 12 seconds.");
    }
}
