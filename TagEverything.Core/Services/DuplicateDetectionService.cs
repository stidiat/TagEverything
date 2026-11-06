using System.Globalization;
using System.Numerics;
using Newtonsoft.Json.Linq;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed record DuplicateDetectionResult(FileRecord? File, bool IsExact, int? HammingDistance = null);

public class DuplicateDetectionService
{
    public static IReadOnlyList<ulong> ReadHashes(string metadata, ulong fallback)
    {
        try
        {
            var values = JObject.Parse(metadata)["DHashes"]?.Values<string>()
                .Where(s => s != null).Select(s => ulong.Parse(s!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToList();
            if (values is { Count: > 0 }) return values;
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or FormatException or OverflowException) { }
        return fallback == 0 ? Array.Empty<ulong>() : new[] { fallback };
    }

    public static int? Compare(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right, int threshold)
    {
        if (left.Count == 0 || right.Count == 0) return null;
        // Never compare a three-frame video against a single legacy frame as if equally reliable.
        if ((left.Count > 1) != (right.Count > 1)) return null;
        var count = Math.Min(left.Count, right.Count);
        var distances = Enumerable.Range(0, count).Select(i => BitOperations.PopCount(left[i] ^ right[i])).ToList();
        return distances.All(d => d <= threshold) ? (int)Math.Round(distances.Average()) : null;
    }

    public async Task<DuplicateDetectionResult> FindDuplicateAsync(LibraryDbContext dbContext, string fileHash, ulong pHash,
        CancellationToken cancellationToken = default, IReadOnlyList<ulong>? visualHashes = null,
        bool? isVideo = null, int similarityThreshold = 10, ISet<string>? dismissedPairs = null)
    {
        similarityThreshold = Math.Clamp(similarityThreshold, 0, 64);
        var available = dbContext.Files.AsNoTracking().Where(f => !f.IsMissing);
        var exact = await available.FirstOrDefaultAsync(f => f.Hash == fileHash, cancellationToken);
        if (exact != null) return new(exact, true, 0);
        var incoming = visualHashes ?? (pHash == 0 ? Array.Empty<ulong>() : new[] { pHash });
        if (incoming.Count == 0) return new(null, false);
        int? bestId = null;
        var bestDistance = int.MaxValue;
        var query = available;
        if (isVideo.HasValue) query = query.Where(f => f.IsVideo == isVideo.Value);
        await foreach (var candidate in query.Select(f => new { f.Id, f.Hash, f.PHash, f.MetadataJson }).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (dismissedPairs?.Contains(SimilarityFeedbackService.PairKey(fileHash, candidate.Hash)) == true) continue;
            var distance = Compare(incoming, ReadHashes(candidate.MetadataJson, candidate.PHash), similarityThreshold);
            if (distance.HasValue && distance.Value < bestDistance) { bestId = candidate.Id; bestDistance = distance.Value; }
        }
        var match = bestId.HasValue ? await available.FirstOrDefaultAsync(f => f.Id == bestId, cancellationToken) : null;
        return new(match, false, match == null ? null : bestDistance);
    }
}
