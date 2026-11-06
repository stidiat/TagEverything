using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;

namespace TagEverything.Tests;

public class FileSearchTests
{
    [Theory]
    [InlineData("%", "100%完成.txt")]
    [InlineData("_", "draft_v2.txt")]
    [InlineData("\\", "path\\notes.txt")]
    [InlineData("#50%", "100%完成.txt")]
    [InlineData("#draft_", "draft_v2.txt")]
    [InlineData("DRAFT", "draft_v2.txt")]
    public async Task Search_TreatsSpecialCharactersLiterally(string text, string expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateLibraryAsync(connection);

        var results = await FileSearchService.ApplyFilters(db.Files, text, Array.Empty<string>()).ToListAsync();

        Assert.Equal(expected, Assert.Single(results).OriginalName);
    }

    [Fact]
    public async Task Search_CombinesAllTagsAndWords_AndIgnoresEmptyHash()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateLibraryAsync(connection);

        var results = await FileSearchService.ApplyFilters(
            db.Files, "# \tdraft\nv2", new[] { "WORK", "draft_review", "work" }).ToListAsync();
        Assert.Equal("draft_v2.txt", Assert.Single(results).OriginalName);

        var noMatch = await FileSearchService.ApplyFilters(
            db.Files, "draft", new[] { "work", "50%" }).ToListAsync();
        Assert.Empty(noMatch);
    }

    [Fact]
    public async Task Search_EmptyFiltersReturnAllFiles_AndHonorCancellation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateLibraryAsync(connection);
        var query = FileSearchService.ApplyFilters(db.Files, null, Array.Empty<string>());
        Assert.Equal(4, await query.CountAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(cancellation.Token));
    }

    private static async Task<LibraryDbContext> CreateLibraryAsync(SqliteConnection connection)
    {
        var db = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var work = new Tag { Name = "work" };
        db.Files.AddRange(
            new FileRecord { OriginalName = "100%完成.txt", Tags = { new Tag { Name = "50%" }, work } },
            new FileRecord { OriginalName = "draft_v2.txt", Tags = { new Tag { Name = "draft_review" }, work } },
            new FileRecord { OriginalName = "path\\notes.txt" },
            new FileRecord { OriginalName = "other.txt", Tags = { new Tag { Name = "draftXreview" } } });
        await db.SaveChangesAsync();
        return db;
    }
}
