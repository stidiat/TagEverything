using System.Collections.Concurrent;

namespace TagEverything.Core.Services;

public static class LibraryWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static async Task<IDisposable> AcquireAsync(string root, CancellationToken token = default)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var gate = Gates.GetOrAdd(canonicalRoot, _ => new(1, 1));
        await gate.WaitAsync(token);
        IDisposable? directories = null;
        try
        {
            var data = LibraryPaths.Resolve(canonicalRoot, LibraryPaths.DataDirectory);
            Directory.CreateDirectory(canonicalRoot);
            directories = LibraryPaths.HoldDirectory(canonicalRoot, data, create: true);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var path = LibraryPaths.Resolve(canonicalRoot, Path.Combine(LibraryPaths.DataDirectory, ".write.lock"));
                    var fileLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new Lease(gate, fileLock, directories);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(50, token); }
            }
        }
        catch { directories?.Dispose(); gate.Release(); throw; }
    }
    private sealed class Lease(SemaphoreSlim gate, FileStream fileLock, IDisposable directories) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            fileLock.Dispose();
            directories.Dispose();
            gate.Release();
        }
    }
}
