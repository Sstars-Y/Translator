using Translator.Core.Models;

namespace Translator.Core.Abstractions;

/// <summary>一帧屏幕捕获的结果，位图为 BGRA32 紧凑排列。</summary>
public sealed class CapturedFrame : IDisposable
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required RectInt SourceBounds { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public int BytesPerPixel => 4;

    /// <summary>读取某像素，用于变化检测的降采样比较。</summary>
    public uint GetPixel(int x, int y)
    {
        int offset = y * Stride + x * BytesPerPixel;
        return (uint)(Pixels[offset] | (Pixels[offset + 1] << 8) | (Pixels[offset + 2] << 16) | (Pixels[offset + 3] << 24));
    }

    public void Dispose() { }
}

/// <summary>
/// 屏幕捕获。WGC 与 Desktop Duplication 两种实现都藏在这个接口后面。
/// </summary>
public interface ICapture : IDisposable
{
    /// <summary>当前可用的捕获后端名称，用于诊断与设置界面显示。</summary>
    string BackendName { get; }

    /// <summary>是否支持去除捕获边框（WGC 的黄色边框）。</summary>
    bool SupportsBorderRemoval { get; }

    /// <summary>捕获指定区域的一帧。</summary>
    Task<CapturedFrame> CaptureAsync(RectInt region, CancellationToken ct = default);

    /// <summary>枚举当前显示器。</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();
}

/// <summary>OCR 引擎。Windows 自带 OCR 藏在这个接口后面，便于将来换引擎。</summary>
public interface IOcr : IDisposable
{
    /// <summary>引擎是否已就绪（语言包存在）。</summary>
    bool IsAvailable { get; }

    /// <summary>实际使用的识别语言标签。</summary>
    string LanguageTag { get; }

    /// <summary>识别前是否做放大预处理。</summary>
    bool UseUpscale { get; set; }

    /// <summary>检查系统是否安装了指定 OCR 语言包。</summary>
    bool IsLanguageInstalled(string languageTag);

    /// <summary>识别一帧。</summary>
    Task<OcrResult> RecognizeAsync(CapturedFrame frame, CancellationToken ct = default);
}

/// <summary>翻译后端。Bergamot 与将来的 [OI] 兼容接口都藏在这里。</summary>
public interface ITranslator : IDisposable
{
    string BackendName { get; }
    bool IsReady { get; }

    /// <summary>单条翻译。</summary>
    Task<TranslationResult> TranslateAsync(string text, CancellationToken ct = default);

    /// <summary>批量翻译，顺序与输入一致。</summary>
    Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>预热模型（首次启动时后台加载）。</summary>
    Task WarmUpAsync(CancellationToken ct = default);

    /// <summary>空闲卸载以释放内存。</summary>
    void Unload();
}

/// <summary>术语条目作用域。</summary>
public enum TermScope
{
    /// <summary>全局生效。</summary>
    Global,

    /// <summary>仅在某领域/场景下生效。</summary>
    Domain,

    /// <summary>预留：按进程名绑定（游戏模式用）。</summary>
    Process,
}

/// <summary>一条术语。</summary>
public sealed record TermEntry(string Source, string Target, TermScope Scope, string? ScopeValue = null)
{
    /// <summary>是否要求整词匹配。</summary>
    public bool WholeWord { get; init; } = true;

    /// <summary>是否区分大小写。</summary>
    public bool CaseSensitive { get; init; }
}

/// <summary>术语库。作用于翻译之前，且缓存键必须包含它的版本。</summary>
public interface ITermStore
{
    /// <summary>版本号，术语变化时递增，用于让缓存失效。</summary>
    string Version { get; }

    /// <summary>当前选中的领域，null 表示只应用全局术语。</summary>
    string? ActiveDomain { get; set; }

    /// <summary>加载术语（全局 + 所有领域）。</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>取当前生效的术语集合。</summary>
    IReadOnlyList<TermEntry> GetActiveTerms();

    /// <summary>把术语应用到文本上，返回处理后的文本与命中的术语。</summary>
    TermApplication Apply(string text);

    /// <summary>把译文里的术语占位符还原成指定译文。</summary>
    string Restore(string translatedText);

    /// <summary>还原并清理可能残留的占位符标记。</summary>
    string RestoreAndClean(string translatedText);
}

/// <summary>术语应用结果：替换后的文本，以及命中的术语列表。</summary>
public sealed record TermApplication(string Text, IReadOnlyList<TermEntry> Hits)
{
    public bool HasHits => Hits.Count > 0;
}
