using System.Security.Cryptography;
using System.Text.Json;

namespace Translator.Platform.Translation;

/// <summary>注册表里的一个模型候选。</summary>
public sealed record ModelCandidate(string Architecture, string? ReleaseStatus, Dictionary<string, string> Files);

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(string FileName, long BytesReceived, long? TotalBytes, int FileIndex, int FileCount)
{
    public double? Percent => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
}

/// <summary>
/// 模型下载器。对应路线图阶段 7 的"下载翻译模型（带进度、支持断点续传和校验）"。
///
/// 阶段 0 实测：Mozilla 的旧仓库 firefox-translations-models 已不再维护，
/// 模型改由官方注册表发布。本类按注册表下载 en-zh 的 Release 候选。
/// </summary>
public sealed class ModelDownloader
{
    public const string RegistryUrl =
        "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json";

    private readonly HttpClient _http;

    public ModelDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>拉取注册表并返回指定方向的候选列表。</summary>
    public async Task<IReadOnlyList<ModelCandidate>> GetCandidatesAsync(
        string direction, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(RegistryUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("models", out var models) ||
            !models.TryGetProperty(direction, out var candidates))
            return [];

        var list = new List<ModelCandidate>();
        foreach (var c in candidates.EnumerateArray())
        {
            var files = new Dictionary<string, string>();
            if (c.TryGetProperty("files", out var f))
            {
                foreach (var prop in f.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("path", out var path) && path.GetString() is string p)
                        files[prop.Name] = p;
                }
            }

            list.Add(new ModelCandidate(
                c.TryGetProperty("architecture", out var a) ? a.GetString() ?? "" : "",
                c.TryGetProperty("releaseStatus", out var rs) ? rs.GetString() : null,
                files));
        }
        return list;
    }

    /// <summary>取 baseUrl，用于拼接文件地址。</summary>
    private async Task<string> GetBaseUrlAsync(CancellationToken ct)
    {
        var json = await _http.GetStringAsync(RegistryUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("baseUrl").GetString() ?? "";
    }

    /// <summary>
    /// 下载指定方向的 Release 模型（找不到 Release 就用第一个候选）到目标目录。
    /// 已存在且大小匹配的文件会跳过，实现断点续传的基本效果。
    /// </summary>
    public async Task<ModelPaths> DownloadAsync(
        string direction,
        string targetDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var candidates = await GetCandidatesAsync(direction, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"注册表里没有 {direction} 方向的模型。");

        var chosen = candidates.FirstOrDefault(c => c.ReleaseStatus == "Release") ?? candidates[0];
        var baseUrl = await GetBaseUrlAsync(ct).ConfigureAwait(false);

        Directory.CreateDirectory(targetDirectory);

        var files = chosen.Files.Values.ToList();
        for (int i = 0; i < files.Count; i++)
        {
            var relative = files[i];
            var fileName = Path.GetFileName(relative);
            var url = $"{baseUrl}/{relative}";

            // .gz 文件解压后落地，避免运行时再解压
            bool isGzip = fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            var finalName = isGzip ? fileName[..^3] : fileName;
            var finalPath = Path.Combine(targetDirectory, finalName);

            if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                progress?.Report(new DownloadProgress(finalName, new FileInfo(finalPath).Length, null, i, files.Count));
                continue;
            }

            var tempPath = finalPath + ".part";
            await DownloadFileAsync(url, tempPath, finalName, i, files.Count, progress, ct).ConfigureAwait(false);

            if (isGzip)
            {
                // 必须先把句柄全部释放再删 .part：Windows 下 File.OpenRead 的句柄不含
                // FileShare.Delete，边解压边删会抛 "being used by another process"。
                await using (var input = File.OpenRead(tempPath))
                await using (var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
                await using (var output = File.Create(finalPath))
                {
                    await gz.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                File.Delete(tempPath);
            }
            else
            {
                File.Move(tempPath, finalPath, overwrite: true);
            }
        }

        // 写配置文件
        var configPath = Path.Combine(targetDirectory, "config.txt");
        await File.WriteAllTextAsync(configPath, BuildConfig(direction), ct).ConfigureAwait(false);

        return new ModelPaths(targetDirectory, configPath);
    }

    private async Task DownloadFileAsync(
        string url, string tempPath, string displayName, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // 断点续传：已有 .part 就从它的长度继续
        long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
            total = (total ?? 0) + existing;
        else if (existing > 0)
            existing = 0; // 服务器不支持 Range，重新下载

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(tempPath, existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = existing;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            progress?.Report(new DownloadProgress(displayName, received, total, index, count));
        }
    }

    /// <summary>按 direction 生成 Bergamot 配置。文件名与注册表里的约定一致。</summary>
    public static string BuildConfig(string direction)
    {
        // 注册表用 "en-zh"，模型文件名用 "enzh"
        var compact = direction.Replace("-", "");
        return $"""
            relative-paths: true
            models:
            - model.{compact}.intgemm.alphas.bin
            vocabs:
            - srcvocab.{compact}.spm
            - trgvocab.{compact}.spm
            shortlist:
            - lex.50.50.{compact}.s2t.bin
            - false
            beam-size: 1
            normalize: 1.0
            word-penalty: 0
            max-length-break: 128
            mini-batch-words: 1024
            workspace: 128
            max-length-factor: 2.0
            skip-cost: true
            cpu-threads: 0
            quiet: true
            quiet-translation: true
            gemm-precision: int8shiftAlphaAll
            """;
    }

    /// <summary>计算文件的 SHA-256，用于校验（注册表提供 hash 时使用）。</summary>
    public static async Task<string> ComputeHashAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
