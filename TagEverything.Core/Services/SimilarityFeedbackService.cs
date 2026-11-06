using System.Text.Json;

namespace TagEverything.Core.Services;

public sealed class SimilarityFeedbackService
{
    public static string PairKey(string left, string right) => string.CompareOrdinal(left, right) <= 0 ? left + ":" + right : right + ":" + left;
    public async Task<HashSet<string>> ReadAsync(string root, CancellationToken token = default)
    {
        var path = Path.Combine(root, "TagEverythingData", "similarity-feedback.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(path, token)) ?? [] : [];
    }
    public async Task DismissAsync(string root, string left, string right, CancellationToken token = default)
    {
        using var lease = await LibraryWriteLock.AcquireAsync(root, token);
        var pairs = await ReadAsync(root, token);
        pairs.Add(PairKey(left, right));
        var path = Path.Combine(root, "TagEverythingData", "similarity-feedback.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(pairs), token);
        File.Move(path + ".tmp", path, true);
    }
}
