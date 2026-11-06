using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed record FilePage(IReadOnlyList<FileRecord> Files, int Page, int TotalPages, int TotalFiles);

public sealed class FileCatalogService
{
    public async Task<FilePage> LoadPageAsync(LibraryDbContext db, string? text, IEnumerable<string> tags,
        int requestedPage, int pageSize = 100, CancellationToken token = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        var query = FileSearchService.ApplyFilters(db.Files.AsNoTracking(), text, tags);
        var count = await query.CountAsync(token);
        var pages = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize));
        var page = Math.Clamp(requestedPage, 1, pages);
        var files = await query.OrderByDescending(f => f.AddedAt).ThenByDescending(f => f.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(f => new FileRecord
            {
                Id = f.Id, OriginalName = f.OriginalName, SavedName = f.SavedName, RelativePath = f.RelativePath,
                Size = f.Size, Hash = f.Hash, FileIdentity = f.FileIdentity, IsMissing = f.IsMissing,
                PHash = f.PHash, IsVideo = f.IsVideo, AddedAt = f.AddedAt,
                FileCreationTime = f.FileCreationTime, FileModificationTime = f.FileModificationTime,
                MetadataJson = f.MetadataJson, Tags = f.Tags.ToList()
            }).ToListAsync(token);
        return new(files, page, pages, count);
    }
}
