using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed class LibraryMonitorErrorEventArgs(string operation, Exception exception) : EventArgs
{
    public string Operation { get; } = operation;
    public Exception Exception { get; } = exception;
}

public sealed class LibraryMonitorService : IDisposable
{
    private sealed record Change(string Root, string? OldPath = null, string? NewPath = null, bool Full = false);
    private string _root;
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<Change> _changes = Channel.CreateBounded<Change>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly ImportAnalysisService _analysis;
    private bool _disposed;
    private int _fullRescan;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _retries = new(StringComparer.OrdinalIgnoreCase);
    public event EventHandler? LibraryChanged;
    public event EventHandler<LibraryMonitorErrorEventArgs>? MonitoringError;
    public IReadOnlyList<string> UntrackedFiles { get; private set; } = Array.Empty<string>();

    public LibraryMonitorService(string libraryPath) : this(libraryPath, new(new HashService(), new MetadataExtractionService())) { }
    public LibraryMonitorService(string libraryPath, ImportAnalysisService analysis)
    {
        _root = Path.GetFullPath(libraryPath);
        _analysis = analysis;
        Directory.CreateDirectory(_root);
        _watcher = new(_root) { IncludeSubdirectories = true, Filter = "*", NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Created += (_, e) => Enqueue(e.FullPath);
        _watcher.Deleted += (_, e) => Enqueue(e.FullPath);
        _watcher.Changed += (_, e) => Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => { if (!Internal(e.OldFullPath) && !Internal(e.FullPath)) Write(new(_root, e.OldFullPath, e.FullPath)); };
        _watcher.Error += (_, _) => Write(new(_root, Full: true));
        _watcher.EnableRaisingEvents = true;
        _worker = Task.Run(ProcessAsync);
        Write(new(_root));
    }

    public void UpdatePath(string newPath)
    {
        var root = Path.GetFullPath(newPath);
        Directory.CreateDirectory(root);
        _watcher.EnableRaisingEvents = false;
        _watcher.Path = root;
        _root = root;
        UntrackedFiles = Array.Empty<string>();
        _watcher.EnableRaisingEvents = true;
        Write(new(root));
    }

    private bool Internal(string path) => Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)[0].Equals("TagEverythingData", StringComparison.OrdinalIgnoreCase);
    private void Write(Change change)
    {
        if (!_disposed && !_changes.Writer.TryWrite(change)) Interlocked.Exchange(ref _fullRescan, 1);
    }
    private void Enqueue(string path)
    {
        if (!Internal(path)) { _retries.TryRemove(path, out _); Write(new(_root, NewPath: path)); }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(_stop.Token))
            {
                await Task.Delay(350, _stop.Token);
                var batch = new List<Change>();
                while (_changes.Reader.TryRead(out var item)) if (item.Root == _root) batch.Add(item);
                if (Interlocked.Exchange(ref _fullRescan, 0) != 0) batch.Add(new(_root, Full: true));
                if (batch.Count == 0) continue;
                var root = _root;
                try
                {
                    await ReconcileAsync(root, batch, _stop.Token);
                    if (!_disposed && root == _root) LibraryChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { if (!_disposed && root == _root) MonitoringError?.Invoke(this, new("文件同步", ex)); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task ReconcileAsync(CancellationToken token = default) => ReconcileAsync(_root, [new(_root, Full: true)], token);
    public void RequestRefresh() => Write(new(_root));

    private async Task ReconcileAsync(string root, List<Change> batch, CancellationToken token)
    {
        using var lease = await LibraryWriteLock.AcquireAsync(root, token);
        if (root != _root || _disposed) return;
        var operations = LibraryPaths.Internal(root, ".operations");
        if (Directory.Exists(operations) && Directory.EnumerateFiles(operations, "*.json").Any())
            throw new IOException("库中有待恢复操作，请重新打开库完成恢复后再同步。");
        await using var db = new LibraryDbContext(root);
        // Directory renames must update descendants before checking which paths disappeared.
        foreach (var change in batch.Where(c => c.OldPath != null && c.NewPath != null))
        {
            var oldRelative = Path.GetRelativePath(root, change.OldPath!);
            var newRelative = Path.GetRelativePath(root, change.NewPath!);
            var prefix = oldRelative + Path.DirectorySeparatorChar;
            var renamed = await db.Files.Where(f => !f.IsMissing && (f.RelativePath == oldRelative || f.RelativePath.StartsWith(prefix))).ToListAsync(token);
            foreach (var record in renamed)
            {
                record.RelativePath = newRelative + record.RelativePath[oldRelative.Length..];
                record.SavedName = Path.GetFileName(record.RelativePath);
                if (record.RelativePath == newRelative) record.OriginalName = record.SavedName;
            }
            await db.SaveChangesAsync(token);
        }
        db.ChangeTracker.Clear();
        // Exclude BLOBs from the inventory query.
        var inventory = await db.Files.AsNoTracking().Select(f => new FileRecord
        {
            Id = f.Id, RelativePath = f.RelativePath, Size = f.Size, FileModificationTime = f.FileModificationTime,
            Hash = f.Hash, IsMissing = f.IsMissing, FileIdentity = f.FileIdentity
        }).ToListAsync(token);
        var filesOnDisk = EnumerateExternalFiles(root).ToList();
        var managedPaths = inventory.Select(f => Path.GetFullPath(Path.Combine(root, f.RelativePath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availablePaths = inventory.ToDictionary(f => f.Id, f => FindContentPath(root, f.RelativePath));
        var missing = inventory.Where(f => availablePaths[f.Id] == null).ToList();
        if (missing.Count > 0)
        {
            // Recover offline/missed renames only for an unambiguous content hash match.
            var missingByHash = missing.GroupBy(f => f.Hash).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
            var missingSizes = missingByHash.Values.Select(f => f.Size).ToHashSet();
            var candidates = new Dictionary<string, List<string>>();
            foreach (var path in filesOnDisk.Where(p => !managedPaths.Contains(p)))
            {
                try
                {
                    if (!missingSizes.Contains(new FileInfo(path).Length)) continue;
                    var safePath = LibraryPaths.Resolve(root, Path.GetRelativePath(root, path), contentOnly: true);
                    using var file = WindowsFiles.OpenRead(safePath);
                    var hash = await HashService.CalculateSha256Async(file, token);
                    if (missingByHash.ContainsKey(hash)) { if (!candidates.TryGetValue(hash, out var list)) candidates[hash] = list = []; list.Add(path); }
                }
                catch (IOException) { }
            }
            foreach (var pair in candidates.Where(p => p.Value.Count == 1))
            {
                var record = missingByHash[pair.Key];
                var relative = Path.GetRelativePath(root, pair.Value[0]);
                availablePaths[record.Id] = FindContentPath(root, relative);
                if (availablePaths[record.Id] == null) continue;
                await db.Files.Where(f => f.Id == record.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.RelativePath, relative)
                    .SetProperty(f => f.SavedName, Path.GetFileName(relative)).SetProperty(f => f.OriginalName, Path.GetFileName(relative))
                    .SetProperty(f => f.IsMissing, true), token);
                record.RelativePath = relative;
                record.IsMissing = true;
            }
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touched = batch.Where(c => c.NewPath != null).Select(c => c.NewPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var full = batch.Any(c => c.Full);
        foreach (var record in inventory)
        {
            token.ThrowIfCancellationRequested();
            if (root != _root || _disposed) return;
            var path = availablePaths[record.Id];
            if (path == null)
            {
                if (!record.IsMissing)
                    await db.Files.Where(f => f.Id == record.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.IsMissing, true), token);
                continue;
            }
            paths.Add(path);
            try
            {
                using var file = WindowsFiles.OpenRead(path);
                var identity = WindowsFiles.Identity(file.SafeFileHandle);
                if (record.IsMissing && await HashService.CalculateSha256Async(file, token) != record.Hash) continue;
                if (record.IsMissing || full || touched.Contains(path) || file.Length != record.Size ||
                    File.GetLastWriteTimeUtc(file.SafeFileHandle) != record.FileModificationTime ||
                    (record.FileIdentity != null && record.FileIdentity != identity))
                {
                    var result = await _analysis.AnalyzeAsync(path, token: token);
                    await db.Files.Where(f => f.Id == record.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.Size, result.Size).SetProperty(f => f.Hash, result.FileHash)
                        .SetProperty(f => f.IsMissing, false).SetProperty(f => f.FileIdentity, identity)
                        .SetProperty(f => f.FileCreationTime, result.CreationTime.ToUniversalTime())
                        .SetProperty(f => f.FileModificationTime, result.LastWriteTime.ToUniversalTime())
                        .SetProperty(f => f.MetadataJson, result.MetadataJson).SetProperty(f => f.IsVideo, result.IsVideo)
                        .SetProperty(f => f.PHash, result.PHash).SetProperty(f => f.Thumbnail, result.Thumbnail), token);
                }
                else if (record.FileIdentity == null && identity != null &&
                    await HashService.CalculateSha256Async(file, token) == record.Hash)
                    await db.Files.Where(f => f.Id == record.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.FileIdentity, identity), token);
            }
            catch (IOException ex)
            {
                var attempts = _retries.AddOrUpdate(path, 1, (_, value) => value + 1);
                if (attempts <= 3) Write(new(root, NewPath: path));
                else if (attempts == 4) MonitoringError?.Invoke(this, new("文件被占用，待下次变更或手动刷新重试", ex));
            }
        }
        await db.Tags.Where(t => !t.Files.Any()).ExecuteDeleteAsync(token);
        if (root == _root) UntrackedFiles = filesOnDisk.Where(p => !paths.Contains(Path.GetFullPath(p))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindContentPath(string root, string relativePath)
    {
        try
        {
            var path = LibraryPaths.Resolve(root, relativePath, contentOnly: true);
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory) ? null : path;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnsafeLibraryPathException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateExternalFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        while (pending.TryPop(out var folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", options))
                if (!path.EndsWith(".library", StringComparison.OrdinalIgnoreCase)) yield return path;
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", options))
                if (!directory.Equals(Path.Combine(root, "TagEverythingData"), StringComparison.OrdinalIgnoreCase)) pending.Push(directory);
        }
    }
    public async Task StopAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
        _changes.Writer.TryComplete();
        _stop.Cancel();
    }
}
