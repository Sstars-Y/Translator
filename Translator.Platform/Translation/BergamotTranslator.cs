using BergamotTranslatorSharp;
using Translator.Core.Abstractions;
using Translator.Core.Models;

namespace Translator.Platform.Translation;

/// <summary>模型文件的位置与状态。</summary>
public sealed record ModelPaths(string Directory, string ConfigPath)
{
    public bool IsPresent =>
        File.Exists(ConfigPath) &&
        File.Exists(Path.Combine(Directory, "model.enzh.intgemm.alphas.bin"));
}

/// <summary>
/// Bergamot 离线翻译后端。
///
/// 阶段 0 实测结论（本机 Windows 11 26200）：
///   - en-zh Release 模型存在于 Mozilla 官方注册表
///   - 加载 113 ms，单句 8-10 ms，质量良好
///   - 常驻工作集约 325 MB，私有内存约 552 MB → 需要懒加载 + 空闲卸载
///
/// 术语不在这里处理：实测 Bergamot 内置 dictionary 会把术语译文前置而非
/// 就地替换，所以术语由 Core 的 TermLayer 用占位符方案自己实现。
/// </summary>
public sealed class BergamotTranslator : ITranslator
{
    private readonly ModelPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BlockingService? _service;
    private DateTimeOffset _lastUse;
    private bool _disposed;

    /// <summary>空闲多久后卸载模型，释放内存。null 表示不自动卸载。</summary>
    public TimeSpan? IdleUnloadAfter { get; set; } = TimeSpan.FromMinutes(5);

    public string BackendName => "Bergamot (offline)";
    public bool IsReady => _service is not null;
    public bool IsLoaded => _service is not null;

    public BergamotTranslator(ModelPaths paths) => _paths = paths;

    public async Task WarmUpAsync(CancellationToken ct = default)
        => await EnsureLoadedAsync(ct).ConfigureAwait(false);

    public void Unload()
    {
        _gate.Wait();
        try
        {
            _service?.Dispose();
            _service = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>如果空闲超时，卸载模型。由后台计时器周期性调用。</summary>
    public void UnloadIfIdle()
    {
        if (IdleUnloadAfter is null || _service is null) return;
        if (DateTimeOffset.Now - _lastUse < IdleUnloadAfter.Value) return;
        Unload();
    }

    private async Task<BlockingService> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_service is not null) return _service;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_service is not null) return _service;

            if (!_paths.IsPresent)
                throw new FileNotFoundException(
                    $"找不到翻译模型。请先运行首次启动向导下载模型到 {_paths.Directory}", _paths.ConfigPath);

            _service = await Task.Run(() => new BlockingService(new[] { _paths.ConfigPath }), ct)
                .ConfigureAwait(false);
            return _service;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranslationResult> TranslateAsync(string text, CancellationToken ct = default)
    {
        var results = await TranslateBatchAsync([text], ct).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        try
        {
            var service = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            _lastUse = DateTimeOffset.Now;

            var output = await Task.Run(() =>
            {
                lock (service)
                {
                    // 逐条调用而非批量：实测批量接口在混合长度文本上收益有限，
                    // 逐条能更好地隔离单条失败。
                    var list = new string[texts.Count];
                    for (int i = 0; i < texts.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        list[i] = service.Translate(texts[i]);
                    }
                    return list;
                }
            }, ct).ConfigureAwait(false);

            _lastUse = DateTimeOffset.Now;

            return texts.Select((src, i) => new TranslationResult(
                src,
                output[i] ?? string.Empty,
                FromCache: false,
                BackendName)).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 失败时给出每条的错误，让上层决定是否显示
            return texts.Select(t => new TranslationResult(t, "", false, BackendName, ex.Message)).ToList();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service?.Dispose();
        _service = null;
        _gate.Dispose();
    }
}
