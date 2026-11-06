using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;

namespace TagEverything.UI.Helpers;

public sealed class ThumbnailStoreService
{
    public void ClearCache() => _cache.Clear();
    private sealed record Cached(byte[]? Bytes);
    private readonly ConcurrentDictionary<string, Cached> _cache = new();
    public async Task<byte[]?> ReadAsync(string root, int id, string revision, CancellationToken token)
    {
        var key = $"{root}|{id}|{revision}";
        if (_cache.TryGetValue(key, out var cached)) return cached.Bytes;
        var bytes = await Task.Run(async () =>
        {
            await using var db = new LibraryDbContext(root);
            return await db.Files.AsNoTracking().Where(f => f.Id == id).Select(f => f.Thumbnail).FirstOrDefaultAsync(token);
        }, token);
        if (_cache.Count >= 128) _cache.Clear();
        _cache[key] = new(bytes);
        return bytes;
    }
}
