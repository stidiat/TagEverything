using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed record OperationFile(int RecordId, string RelativePath, string StagedName, string Hash, string? FileIdentity = null);
public sealed record LibraryOperation(string Kind, List<OperationFile> Files);
public sealed record OperationProgress(string Stage, double Percent);

/// <summary>Journaled file/database operations. All paths in journals are validated on recovery.</summary>
public sealed class LibraryOperationsService
{
    private readonly Func<string, LibraryDbContext> _contextFactory;
    public LibraryOperationsService() : this(root => new LibraryDbContext(root)) { }
    public LibraryOperationsService(Func<string, LibraryDbContext> contextFactory) => _contextFactory = contextFactory;

    public async Task<FileRecord> ImportAsync(string root, FileImportContext context, IEnumerable<string> tags,
        IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        using var lease = await LibraryWriteLock.AcquireAsync(root, token);
        EnsureNoPendingJournal(root);
        var original = Path.GetFileName(context.FilePath);
        var extension = Path.GetExtension(original);
        var suffix = "_" + Guid.NewGuid().ToString("N");
        var baseName = Path.GetFileNameWithoutExtension(original);
        var relativeSource = Path.GetRelativePath(root, context.FilePath);
        var name = context.RegisterInPlace ? relativeSource
            : baseName[..Math.Min(baseName.Length, Math.Max(1, 255 - extension.Length - suffix.Length))] + suffix + extension;
        var destination = LibraryPaths.Resolve(root, name, contentOnly: true);
        using var destinationDirectory = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(destination)!);
        using var registered = context.RegisterInPlace ? WindowsFiles.OpenRead(destination) : null;
        var internalSource = !context.RegisterInPlace && LibraryPaths.IsInside(relativeSource)
            ? LibraryPaths.Resolve(root, relativeSource)
            : null;
        using var sourceDirectory = internalSource == null ? null : LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(internalSource)!);
        if (context.RegisterInPlace)
        {
            await using var existingDb = _contextFactory(root);
            if (await PathIsRegisteredAsync(existingDb, name, token)) throw new IOException("文件已入库，请刷新列表。");
        }
        var kind = context.RegisterInPlace ? "register" : "import";
        var item = new OperationFile(0, name, Guid.NewGuid().ToString("N") + ".partial", context.FileHash);
        var journal = WriteJournal(root, new(kind, [item]));
        using var operationDirectory = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(journal)!);
        var staged = LibraryPaths.Internal(root, ".operations", item.StagedName);
        try
        {
            string? identity;
            if (!context.RegisterInPlace)
            {
                await using (var input = internalSource != null
                    ? WindowsFiles.OpenRead(internalSource)
                    : new FileStream(context.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1024 * 1024];
                    long copied = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        hash.AppendData(buffer, 0, count);
                        copied += count;
                        progress?.Report(new("复制文件", input.Length == 0 ? 100 : copied * 100d / input.Length));
                    }
                    await output.FlushAsync(token);
                    output.Flush(true);
                    if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != context.FileHash)
                        throw new IOException("源文件在解析后发生变化，请重新导入。");
                    identity = WindowsFiles.Identity(output.SafeFileHandle);
                }
                token.ThrowIfCancellationRequested();
                File.Move(staged, destination);
                File.SetLastWriteTimeUtc(destination, context.LastWriteTime.ToUniversalTime());
            }
            else
            {
                if (await HashService.CalculateSha256Async(registered!, token) != context.FileHash)
                    throw new IOException("源文件已变化，请重新解析。");
                identity = WindowsFiles.Identity(registered!.SafeFileHandle);
            }
            await using var db = _contextFactory(root);
            var record = new FileRecord
            {
                OriginalName = original, SavedName = Path.GetFileName(name), RelativePath = name,
                Size = context.Size, Hash = context.FileHash, FileIdentity = identity, PHash = context.PHash,
                IsVideo = context.IsVideo, MetadataJson = context.MetadataJson, Thumbnail = context.Thumbnail,
                FileCreationTime = context.CreationTime.ToUniversalTime(), FileModificationTime = context.LastWriteTime.ToUniversalTime()
            };
            await new TagService().ApplyAsync(db, [record], tags, [], token);
            db.Files.Add(record);
            await db.SaveChangesAsync(token);
            // A crash after SaveChanges is resolved by matching the committed path/hash.
            File.Delete(journal);
            return record;
        }
        catch
        {
            // Preserve the journal if cleanup itself fails. Recovery retries next startup.
            await RecoverJournalAsync(root, journal, CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string root, IReadOnlyCollection<FileRecord> records, CancellationToken token = default)
    {
        if (records.Count == 0) return;
        using var lease = await LibraryWriteLock.AcquireAsync(root, token);
        EnsureNoPendingJournal(root);
        await using var db = _contextFactory(root);
        var ids = records.Select(r => r.Id).Distinct().ToList();
        var current = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, f.RelativePath, f.Hash, f.FileIdentity, f.IsMissing, f.FileModificationTime, f.Size })
            .ToDictionaryAsync(f => f.Id, token);
        foreach (var record in records)
        {
            if (!current.TryGetValue(record.Id, out var actual) || actual.RelativePath != record.RelativePath ||
                actual.Hash != record.Hash || actual.FileIdentity != record.FileIdentity || actual.IsMissing != record.IsMissing ||
                actual.FileModificationTime != record.FileModificationTime || actual.Size != record.Size)
                throw ChangedFile();
        }

        var items = current.Values.Where(f => !f.IsMissing)
            .Select(f => new OperationFile(f.Id, f.RelativePath, Guid.NewGuid().ToString("N") + ".staged", f.Hash, f.FileIdentity))
            .ToList();
        foreach (var item in items) LibraryPaths.Resolve(root, item.RelativePath, contentOnly: true);
        var trash = LibraryPaths.Internal(root, ".trash");
        using var trashDirectory = LibraryPaths.HoldDirectory(root, trash, create: true);
        var journal = WriteJournal(root, new("delete", items));
        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var source = LibraryPaths.Resolve(root, item.RelativePath, contentOnly: true);
                using var sourceDirectory = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(source)!);
                using var input = WindowsFiles.OpenForMove(source);
                if (OperatingSystem.IsWindows() && File.GetAttributes(input.SafeFileHandle).HasFlag(FileAttributes.ReadOnly))
                    throw new IOException($"无法删除只读文件：{item.RelativePath}");
                var actual = current[item.RecordId];
                if (input.Length != actual.Size || File.GetLastWriteTimeUtc(input.SafeFileHandle) != actual.FileModificationTime ||
                    (item.FileIdentity != null && WindowsFiles.Identity(input.SafeFileHandle) != item.FileIdentity) ||
                    await HashService.CalculateSha256Async(input, token) != item.Hash)
                    throw ChangedFile();

                token.ThrowIfCancellationRequested();
                WindowsFiles.Move(input, source, StagedPath(root, item, "delete"));
            }
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Files.Where(f => ids.Contains(f.Id)).ExecuteDeleteAsync(token);
            await db.Tags.Where(t => !t.Files.Any()).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await RecoverJournalAsync(root, journal, CancellationToken.None);
            throw;
        }
        await RecoverJournalAsync(root, journal, CancellationToken.None);
    }

    private static IOException ChangedFile() => new("文件已移动、修改或被替换，请刷新列表后重试。");

    public async Task RelinkAsync(string root, int recordId, string newPath, CancellationToken token = default)
    {
        using var lease = await LibraryWriteLock.AcquireAsync(root, token);
        EnsureNoPendingJournal(root);
        var relativePath = Path.GetRelativePath(root, newPath);
        var path = LibraryPaths.Resolve(root, relativePath, contentOnly: true);
        using var directory = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(path)!);
        await using var db = _contextFactory(root);
        var record = await db.Files.SingleOrDefaultAsync(f => f.Id == recordId && f.IsMissing, token)
            ?? throw new IOException("此文件记录已更新，请刷新列表。");
        if (await PathIsRegisteredAsync(db, relativePath, token, recordId))
            throw new IOException("该路径已有入库记录，请选择其他文件。");
        using var file = WindowsFiles.OpenRead(path);
        if (await HashService.CalculateSha256Async(file, token) != record.Hash)
            throw new IOException("所选文件与原文件内容不同。");
        record.RelativePath = relativePath;
        record.OriginalName = record.SavedName = Path.GetFileName(path);
        record.FileIdentity = WindowsFiles.Identity(file.SafeFileHandle);
        record.FileCreationTime = File.GetCreationTimeUtc(file.SafeFileHandle);
        record.FileModificationTime = File.GetLastWriteTimeUtc(file.SafeFileHandle);
        record.IsMissing = false;
        await db.SaveChangesAsync(token);
    }

    private static async Task<bool> PathIsRegisteredAsync(LibraryDbContext db, string path, CancellationToken token, int? except = null)
    {
        var paths = await db.Files.Where(f => f.Id != except).Select(f => f.RelativePath).ToListAsync(token);
        return paths.Any(existing => string.Equals(existing, path, LibraryPaths.Comparison));
    }

    public async Task<IReadOnlyList<string>> RecoverAsync(string root, CancellationToken token = default)
    {
        using var lease = await LibraryWriteLock.AcquireAsync(root, token);
        var messages = new List<string>();
        var directory = LibraryPaths.Internal(root, ".operations");
        using var operationDirectory = LibraryPaths.HoldDirectory(root, directory, create: true);
        foreach (var journal in Directory.EnumerateFiles(directory, "*.json"))
        {
            try { await RecoverJournalAsync(root, journal, token); messages.Add("已恢复操作：" + Path.GetFileName(journal)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { messages.Add("恢复待处理：" + ex.Message); }
        }
        // Unknown staged data is retained, never silently discarded.
        var trash = LibraryPaths.Internal(root, ".trash");
        if (!Directory.EnumerateFiles(directory, "*.json").Any() && Directory.Exists(trash))
            foreach (var file in Directory.EnumerateFiles(trash)) { Preserve(root, file); messages.Add("未知暂存文件已保留到 recovered。"); }
        if (!Directory.EnumerateFiles(directory, "*.json").Any())
            foreach (var partial in Directory.EnumerateFiles(directory, "*.partial")) { Preserve(root, partial); messages.Add("未知导入暂存已保留到 recovered。"); }
        return messages;
    }

    public static string WriteJournal(string root, LibraryOperation operation)
    {
        var directory = LibraryPaths.Internal(root, ".operations");
        using var operationDirectory = LibraryPaths.HoldDirectory(root, directory, create: true);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(operation);
        using (var stream = new FileStream(path + ".tmp", FileMode.CreateNew)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(path + ".tmp", path);
        return path;
    }

    private async Task RecoverJournalAsync(string root, string journal, CancellationToken token)
    {
        var safeJournal = LibraryPaths.Internal(root, ".operations", Path.GetFileName(journal));
        using var operationDirectory = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(safeJournal)!);
        using var input = WindowsFiles.OpenRead(safeJournal);
        var operation = await JsonSerializer.DeserializeAsync<LibraryOperation>(input, cancellationToken: token)
            ?? throw new IOException("操作日志为空。");
        input.Dispose();
        if (operation.Kind is not ("import" or "delete" or "register")) throw new IOException("未知操作日志类型。");
        if (operation.Kind == "register") { File.Delete(safeJournal); return; }
        var entries = operation.Files.Select(item => (
            Item: item,
            Path: LibraryPaths.Resolve(root, item.RelativePath, contentOnly: true),
            Staged: StagedPath(root, item, operation.Kind))).ToList();
        await using var db = _contextFactory(root);
        foreach (var (item, path, staged) in entries)
        {
            if (operation.Kind == "import")
            {
                var committed = await db.Files.AnyAsync(f => f.RelativePath == item.RelativePath && f.Hash == item.Hash, token);
                if (File.Exists(staged)) Preserve(root, staged);
                if (!committed && File.Exists(path))
                {
                    using var parent = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(path)!);
                    using var file = WindowsFiles.OpenForMove(path);
                    if (await HashService.CalculateSha256Async(file, token) == item.Hash) Preserve(root, path, file);
                }
            }
            else if (File.Exists(staged))
            {
                using var parent = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(staged)!);
                var current = await db.Files.Where(f => f.Id == item.RecordId)
                    .Select(f => new { f.RelativePath, f.Hash }).SingleOrDefaultAsync(token);
                if (current != null && current.RelativePath == item.RelativePath && current.Hash == item.Hash && !File.Exists(path))
                {
                    using var destinationParent = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(path)!, create: true);
                    using var file = WindowsFiles.OpenForMove(staged);
                    WindowsFiles.Move(file, staged, path);
                }
                else if (current == null)
                {
                    using var file = WindowsFiles.OpenForMove(staged);
                    if ((item.FileIdentity == null || WindowsFiles.Identity(file.SafeFileHandle) == item.FileIdentity) &&
                        await HashService.CalculateSha256Async(file, token) == item.Hash)
                        WindowsFiles.Delete(file, staged);
                    else Preserve(root, staged, file);
                }
                else Preserve(root, staged);
            }
        }
        File.Delete(safeJournal);
    }

    private static string StagedPath(string root, OperationFile file, string kind)
    {
        if (string.IsNullOrWhiteSpace(file.StagedName) || Path.GetFileName(file.StagedName) != file.StagedName)
            throw new IOException("操作日志暂存路径无效。");
        return LibraryPaths.Internal(root, kind == "delete" ? ".trash" : ".operations", file.StagedName);
    }

    private static void Preserve(string root, string path, FileStream? file = null)
    {
        LibraryPaths.Resolve(root, Path.GetRelativePath(root, path));
        var directory = LibraryPaths.Internal(root, "recovered");
        using var destinationParent = LibraryPaths.HoldDirectory(root, directory, create: true);
        using var sourceParent = LibraryPaths.HoldDirectory(root, Path.GetDirectoryName(path)!);
        using var source = file == null ? WindowsFiles.OpenForMove(path) : null;
        var name = Path.GetFileName(path);
        var destination = LibraryPaths.Internal(root, "recovered", Guid.NewGuid().ToString("N") + "_" + name[..Math.Min(name.Length, 222)]);
        WindowsFiles.Move(file ?? source!, path, destination);
    }

    private static void EnsureNoPendingJournal(string root)
    {
        var directory = LibraryPaths.Internal(root, ".operations");
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any())
            throw new IOException("库中有未完成操作，请重新打开库完成恢复后再导入或删除。");
    }
}
