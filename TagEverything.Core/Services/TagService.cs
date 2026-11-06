using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed class TagService
{
    public async Task ApplyAsync(LibraryDbContext db, IEnumerable<FileRecord> files,
        IEnumerable<string> add, IEnumerable<string> remove, CancellationToken token = default)
    {
        var additions = add.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var removals = remove.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            foreach (var tag in file.Tags.Where(t => removals.Contains(t.Name)).ToList()) file.Tags.Remove(tag);
            foreach (var name in additions)
            {
                if (file.Tags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                var tag = db.Tags.Local.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? await db.Tags.FirstOrDefaultAsync(t => EF.Functions.Collate(t.Name, "NOCASE") == name, token);
                if (tag == null) { tag = new Tag { Name = name }; db.Tags.Add(tag); }
                file.Tags.Add(tag);
            }
        }
    }
}
