using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed record ImportQueueProgress(int Index, int Total, string FileName, string Stage, double Percent);
public sealed record ImportQueueFailure(string Path, string Error, bool RegisterInPlace);
public sealed record ImportQueueResult(IReadOnlyList<ImportQueueFailure> Failures, int Completed);

public sealed class ImportQueueService(ImportAnalysisService analysis)
{
    private readonly SemaphoreSlim _queue = new(1, 1);
    public async Task<ImportQueueResult> RunAsync(IEnumerable<string> paths, bool registerInPlace,
        Func<FileImportContext, CancellationToken, Task> confirm,
        IProgress<ImportQueueProgress>? progress = null, CancellationToken token = default)
    {
        await _queue.WaitAsync(token);
        try
        {
            var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var failures = new List<ImportQueueFailure>();
            var completed = 0;
            for (var i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var index = i + 1;
                var path = files[i];
                var itemProgress = new InlineProgress<OperationProgress>(p => progress?.Report(new(index, files.Count, Path.GetFileName(path), p.Stage, p.Percent)));
                try
                {
                    var context = await analysis.AnalyzeAsync(path, itemProgress, token);
                    context.RegisterInPlace = registerInPlace;
                    await confirm(context, token);
                    completed++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failures.Add(new(path, ex.Message, registerInPlace)); }
            }
            return new(failures, completed);
        }
        finally { _queue.Release(); }
    }
}

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
