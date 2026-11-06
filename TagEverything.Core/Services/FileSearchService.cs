using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public static class FileSearchService
{
    public static IQueryable<FileRecord> ApplyFilters(
        IQueryable<FileRecord> query, string? searchText, IEnumerable<string> selectedTags)
    {
        foreach (var tag in selectedTags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            query = query.Where(f => f.Tags.Any(t => EF.Functions.Collate(t.Name, "NOCASE") == tag));
        }

        foreach (var keyword in (searchText ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var isTag = keyword.StartsWith('#');
            var text = isTag ? keyword[1..] : keyword;
            if (text.Length == 0) continue;

            // Search user text literally, including SQL LIKE metacharacters.
            var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = isTag
                ? query.Where(f => f.Tags.Any(t => EF.Functions.Like(t.Name, pattern, "\\")))
                : query.Where(f => EF.Functions.Like(f.OriginalName, pattern, "\\"));
        }

        return query;
    }
}
