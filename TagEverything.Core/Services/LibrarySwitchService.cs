using Microsoft.EntityFrameworkCore;
using TagEverything.Core.Data;

namespace TagEverything.Core.Services;

public sealed class LibrarySwitchService(LibraryOperationsService operations)
{
    public async Task<IReadOnlyList<string>> InitializeAsync(string root, CancellationToken token = default)
    {
        Directory.CreateDirectory(root);
        using (await LibraryWriteLock.AcquireAsync(root, token))
        {
            await using var db = new LibraryDbContext(root);
            await db.Database.MigrateAsync(token);
        }
        return await operations.RecoverAsync(root, token);
    }

    public async Task<IReadOnlyList<string>> SwitchAsync(string previous, string destination, Action<string> apply, CancellationToken token = default)
    {
        var recovery = await InitializeAsync(destination, token);
        try { apply(destination); }
        catch (Exception original)
        {
            try { apply(previous); }
            catch (Exception rollback) { throw new AggregateException("切库失败，恢复旧设置也失败。", original, rollback); }
            throw;
        }
        return recovery;
    }
}
