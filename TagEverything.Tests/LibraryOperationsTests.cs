using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;

namespace TagEverything.Tests;

internal sealed class TestLibrary : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TagEverythingIntegration", Guid.NewGuid().ToString("N"));
    public LibraryOperationsService Operations { get; } = new();
    public LibraryDbContext Open() => new(Root);
    public async Task InitializeAsync() { Directory.CreateDirectory(Root); await using var db = Open(); await db.Database.MigrateAsync(); }
    public async Task<FileImportContext> SourceAsync(string content = "content")
    {
        var path = Path.Combine(Root, "TagEverythingData", "source.txt");
        await File.WriteAllTextAsync(path, content);
        return await new ImportAnalysisService(new HashService(), new MetadataExtractionService()).AnalyzeAsync(path);
    }
    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        return ValueTask.CompletedTask;
    }
}

public class LibraryOperationsTests
{
    [Fact]
    public async Task WriteLock_RespectsFilesystemLockAndCancellation()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        using (var externalLock = new FileStream(Path.Combine(library.Root, "TagEverythingData", ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryWriteLock.AcquireAsync(library.Root, cancellation.Token));
        }
        using var lease = await LibraryWriteLock.AcquireAsync(library.Root);
    }

    [Fact]
    public async Task Recovery_RejectsEscapingPathsAndBlocksMutationsUntilResolved()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        LibraryOperationsService.WriteJournal(library.Root, new("delete", [new(1, "../outside.txt", "safe.txt", "hash")]));
        var report = await library.Operations.RecoverAsync(library.Root);
        Assert.Contains(report, message => message.Contains("恢复待处理"));
        var source = await library.SourceAsync();
        await Assert.ThrowsAsync<IOException>(() => library.Operations.ImportAsync(library.Root, source, []));
    }

    [Fact]
    public async Task ImportDeleteAndTags_CommitTogether()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var source = await library.SourceAsync();
        var record = await library.Operations.ImportAsync(library.Root, source, ["Work", "work", "notes"]);
        await using (var db = library.Open())
        {
            Assert.Equal(2, await db.Tags.CountAsync());
            Assert.Equal(source.FileHash, (await db.Files.SingleAsync()).Hash);
        }
        Assert.True(File.Exists(Path.Combine(library.Root, record.RelativePath)));
        await library.Operations.DeleteAsync(library.Root, [record]);
        await using var verification = library.Open();
        Assert.Empty(await verification.Files.ToListAsync());
        Assert.Empty(await verification.Tags.ToListAsync());
        Assert.False(File.Exists(Path.Combine(library.Root, record.RelativePath)));
        Assert.True(File.Exists(source.FilePath));
    }

    private sealed class FailingSaveContext(string root) : LibraryDbContext(root)
    {
        public override Task<int> SaveChangesAsync(CancellationToken token = default) => throw new IOException("simulated commit failure");
    }

    [Fact]
    public async Task ImportFailure_PreservesSourceAndRecoversCopiedFile()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var context = await library.SourceAsync();
        var operations = new LibraryOperationsService(root => new FailingSaveContext(root));
        await Assert.ThrowsAsync<IOException>(() => operations.ImportAsync(library.Root, context, ["tag"]));
        Assert.True(File.Exists(context.FilePath));
        await using var db = library.Open();
        Assert.Empty(await db.Files.ToListAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(library.Root, "TagEverythingData", "recovered")));
        Assert.Empty(Directory.GetFiles(Path.Combine(library.Root, "TagEverythingData", ".operations"), "*.json"));
    }

    private sealed class FailDelete : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("DELETE FROM \"Files\"")) throw new IOException("simulated delete failure");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task DeleteFailure_RestoresAllFilesAndRelations()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var context = await library.SourceAsync();
        var first = await library.Operations.ImportAsync(library.Root, context, ["keep"]);
        var second = await library.Operations.ImportAsync(library.Root, context, ["keep"]);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        var operations = new LibraryOperationsService(root => new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(LibraryDbContext.CreateConnectionString(root)).AddInterceptors(new FailDelete()).Options));
        await Assert.ThrowsAsync<IOException>(() => operations.DeleteAsync(library.Root, [first, second]));
        Assert.True(File.Exists(Path.Combine(library.Root, first.RelativePath)));
        Assert.True(File.Exists(Path.Combine(library.Root, second.RelativePath)));
        await using var db = library.Open();
        Assert.Equal(2, await db.Files.CountAsync());
        Assert.Equal(2, (await db.Tags.Include(t => t.Files).SingleAsync()).Files.Count);
    }

    [Fact]
    public async Task Recovery_HandlesInterruptedImportDeleteAndUnknownTrash()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var source = await library.SourceAsync();
        var record = await library.Operations.ImportAsync(library.Root, source, ["keep"]);
        var trash = Path.Combine(library.Root, "TagEverythingData", ".trash"); Directory.CreateDirectory(trash);
        File.Move(Path.Combine(library.Root, record.RelativePath), Path.Combine(trash, "staged.txt"));
        LibraryOperationsService.WriteJournal(library.Root, new("delete", [new(record.Id, record.RelativePath, "staged.txt", record.Hash)]));
        await File.WriteAllTextAsync(Path.Combine(library.Root, "incomplete.txt"), "not committed");
        var incompleteHash = await new HashService().CalculateSha256Async(Path.Combine(library.Root, "incomplete.txt"));
        LibraryOperationsService.WriteJournal(library.Root, new("import", [new(0, "incomplete.txt", "absent.partial", incompleteHash)]));
        await File.WriteAllTextAsync(Path.Combine(trash, "unknown.bin"), "unknown");
        await library.Operations.RecoverAsync(library.Root);
        Assert.True(File.Exists(Path.Combine(library.Root, record.RelativePath)));
        Assert.False(File.Exists(Path.Combine(library.Root, "incomplete.txt")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(library.Root, "TagEverythingData", "recovered")).Length);
        Assert.Empty(await library.Operations.RecoverAsync(library.Root));
    }

    [Fact]
    public async Task Recovery_RecognizesCommittedImportAndCommittedDeletion()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var source = await library.SourceAsync();
        var record = await library.Operations.ImportAsync(library.Root, source, []);
        LibraryOperationsService.WriteJournal(library.Root, new("import", [new(0, record.RelativePath, "absent.partial", record.Hash)]));
        await library.Operations.RecoverAsync(library.Root);
        Assert.True(File.Exists(Path.Combine(library.Root, record.RelativePath)));
        var trash = Path.Combine(library.Root, "TagEverythingData", ".trash"); Directory.CreateDirectory(trash);
        File.Move(Path.Combine(library.Root, record.RelativePath), Path.Combine(trash, "committed.txt"));
        await using (var db = library.Open()) await db.Files.ExecuteDeleteAsync();
        LibraryOperationsService.WriteJournal(library.Root, new("delete", [new(record.Id, record.RelativePath, "committed.txt", record.Hash)]));
        await library.Operations.RecoverAsync(library.Root);
        Assert.False(File.Exists(Path.Combine(trash, "committed.txt")));
    }

    [Fact]
    public async Task ImportCancellationAndChangedSource_DoNotCreateRecords()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var context = await library.SourceAsync(new string('x', 2 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<OperationProgress>(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.Operations.ImportAsync(library.Root, context, [], progress, cancellation.Token));
        await File.WriteAllTextAsync(context.FilePath, "changed");
        await Assert.ThrowsAsync<IOException>(() => library.Operations.ImportAsync(library.Root, context, []));
        await using var db = library.Open(); Assert.Empty(await db.Files.ToListAsync());
    }

    [Fact]
    public async Task RegisterInPlaceAndSwitchFailure_PreserveOriginalLibrary()
    {
        await using var library = new TestLibrary(); await library.InitializeAsync();
        var external = Path.Combine(library.Root, "external.txt"); await File.WriteAllTextAsync(external, "external");
        var context = await new ImportAnalysisService(new(), new()).AnalyzeAsync(external); context.RegisterInPlace = true;
        var record = await library.Operations.ImportAsync(library.Root, context, []);
        Assert.Equal("external.txt", record.RelativePath);
        Assert.True(File.Exists(external));
        await Assert.ThrowsAsync<IOException>(() => library.Operations.ImportAsync(library.Root, context, []));
        var active = library.Root;
        var destination = Path.Combine(library.Root, "other-library");
        await Assert.ThrowsAsync<IOException>(() => new LibrarySwitchService(library.Operations).SwitchAsync(library.Root, destination, p =>
        {
            active = p;
            if (p == destination) throw new IOException("simulated settings failure");
        }));
        Assert.Equal(library.Root, active);
        await using var db = library.Open(); Assert.Single(await db.Files.ToListAsync());
    }

    [Fact]
    public async Task Delete_RejectsSnapshotAfterRenameAndLeavesEntireBatchIntact()
    {
        await using var library = new TestLibrary();
        await library.InitializeAsync();
        var source = await library.SourceAsync();
        var first = await library.Operations.ImportAsync(library.Root, source, ["keep"]);
        var stale = await library.Operations.ImportAsync(library.Root, source, ["keep"]);
        var oldPath = Path.Combine(library.Root, stale.RelativePath);
        var newPath = Path.Combine(library.Root, "renamed.txt");
        File.Move(oldPath, newPath);
        File.Copy(newPath, oldPath);
        await using var db = library.Open();
        await db.Files.Where(f => f.Id == stale.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.RelativePath, "renamed.txt"));

        await Assert.ThrowsAsync<IOException>(() => library.Operations.DeleteAsync(library.Root, [first, stale]));

        Assert.True(File.Exists(Path.Combine(library.Root, first.RelativePath)));
        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        Assert.Equal(2, await db.Files.CountAsync());
    }

    [Fact]
    public async Task Delete_RejectsSamePathReplacementWithIdenticalBytesOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var library = new TestLibrary();
        await library.InitializeAsync();
        var record = await library.Operations.ImportAsync(library.Root, await library.SourceAsync(), ["keep"]);
        Assert.NotNull(record.FileIdentity);
        var path = Path.Combine(library.Root, record.RelativePath);
        var original = Path.Combine(library.Root, "original.txt");
        File.Move(path, original);
        File.Copy(original, path);
        File.SetLastWriteTimeUtc(path, record.FileModificationTime);

        await Assert.ThrowsAsync<IOException>(() => library.Operations.DeleteAsync(library.Root, [record]));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(original));
        await using var db = library.Open();
        Assert.Equal("keep", Assert.Single((await db.Files.Include(f => f.Tags).SingleAsync()).Tags).Name);
    }

    [Fact]
    public async Task Operations_RejectDirectoryLinkForRegistrationDeletionAndRecovery()
    {
        await using var library = new TestLibrary();
        await using var external = new TestLibrary();
        await library.InitializeAsync();
        Directory.CreateDirectory(external.Root);
        var folder = Path.Combine(library.Root, "folder");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "file.txt");
        await File.WriteAllTextAsync(path, "keep");
        var analysis = new ImportAnalysisService(new(), new());
        var context = await analysis.AnalyzeAsync(path);
        context.RegisterInPlace = true;
        var record = await library.Operations.ImportAsync(library.Root, context, ["keep"]);
        var outside = Path.Combine(external.Root, "file.txt");
        File.Move(path, outside);
        Directory.Delete(folder);
        Directory.CreateSymbolicLink(folder, external.Root);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => library.Operations.ImportAsync(library.Root, context, []));
            context.RegisterInPlace = false;
            await Assert.ThrowsAnyAsync<IOException>(() => library.Operations.ImportAsync(library.Root, context, []));
            await Assert.ThrowsAnyAsync<IOException>(() => library.Operations.DeleteAsync(library.Root, [record]));

            var trash = Path.Combine(library.Root, "TagEverythingData", ".trash");
            Directory.CreateDirectory(trash);
            var staged = Path.Combine(trash, "staged.txt");
            File.Copy(outside, staged);
            LibraryOperationsService.WriteJournal(library.Root, new("delete", [new(record.Id, record.RelativePath, "staged.txt", record.Hash)]));
            var report = await library.Operations.RecoverAsync(library.Root);
            Assert.Contains(report, message => message.Contains("恢复待处理"));
            Assert.True(File.Exists(staged));
            Assert.Equal("keep", await File.ReadAllTextAsync(outside));
            await using var db = library.Open();
            Assert.Equal(1, await db.Files.CountAsync());
        }
        finally { Directory.Delete(folder); }
    }
}
