using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TagEverything.Core.Models;

namespace TagEverything.Core.Services;

public sealed class ImportAnalysisService(HashService hashService, MetadataExtractionService media)
{
    public async Task<FileImportContext> AnalyzeAsync(string path, IProgress<OperationProgress>? progress = null, CancellationToken token = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("待导入文件不存在。", path);
        var size = file.Length;
        var modified = file.LastWriteTimeUtc;
        progress?.Report(new("计算 SHA-256", 5));
        var hash = await hashService.CalculateSha256Async(path, token);
        var video = media.IsVideoFile(file.Extension);
        progress?.Report(new("读取文件信息", 40));
        var metadata = JObject.Parse(await media.ExtractMetadataAsync(path, video, token));
        progress?.Report(new("生成预览与 dHash 特征", 65));
        // If components are unavailable, don't retry the same download during thumbnail extraction.
        var preview = metadata["Error"] == null ? await media.ExtractPreviewAsync(path, file.Extension, token) : new MediaPreview(null, []);
        metadata["DHashes"] = JArray.FromObject(preview.DHashes.Select(h => h.ToString("X16")));
        metadata["VisualAlgorithm"] = "dHash-64";
        file.Refresh();
        if (!file.Exists || file.Length != size || file.LastWriteTimeUtc != modified) throw new IOException("解析期间源文件发生变化，请重试。");
        progress?.Report(new("解析完成", 100));
        return new FileImportContext
        {
            FilePath = path, FileHash = hash, PHash = preview.PrimaryHash, Thumbnail = preview.Thumbnail,
            IsVideo = video, MetadataJson = metadata.ToString(Formatting.None), Size = size,
            CreationTime = file.CreationTimeUtc, LastWriteTime = modified
        };
    }
}
