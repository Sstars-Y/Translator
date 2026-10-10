using System.Text.Json;
using System.Text.Json.Serialization;

namespace Translator.Core.Configuration;

/// <summary>悬浮栏的形态。</summary>
public enum BarForm
{
    /// <summary>收起：只留一个状态点。</summary>
    Collapsed,

    /// <summary>紧凑：五个图标。</summary>
    Compact,

    /// <summary>展开：快速设置。</summary>
    Expanded,
}

/// <summary>气泡的折叠状态。</summary>
public enum BubbleState
{
    Expanded,
    Collapsed,
    Hidden,
}

/// <summary>
/// 应用设置。存到 %AppData%\Translator\settings.json。
/// 隐私默认值：离线优先、日志不记录原文。
/// </summary>
public sealed class AppSettings
{
    // ---- 翻译 ----
    public string TargetLanguage { get; set; } = "zh-Hans";
    public string SourceLanguage { get; set; } = "en";
    public string? ActiveDomain { get; set; }

    // ---- 气泡外观 ----
    public double BubbleOpacity { get; set; } = 0.95;
    public double BubbleFontSize { get; set; } = 13;
    public double BubbleWidthFactor { get; set; } = 1.0;
    public bool BubbleAutoDarkMode { get; set; } = true;
    public bool ShowSourceIndicator { get; set; } = true;
    public BubbleState DefaultBubbleState { get; set; } = BubbleState.Expanded;

    // ---- 悬浮栏 ----
    public BarForm BarForm { get; set; } = BarForm.Compact;
    public double BarOpacity { get; set; } = 0.92;
    public bool BarSnapToEdges { get; set; } = true;
    /// <summary>按显示器记住悬浮栏位置：设备名 -> 位置。</summary>
    public Dictionary<string, BarPosition> BarPositions { get; set; } = new();

    // ---- 快捷键 ----
    public string HotkeyCapture { get; set; } = "Ctrl+Alt+T";
    public string HotkeyTogglePause { get; set; } = "Ctrl+Alt+P";
    public string HotkeyCollapseAll { get; set; } = "Ctrl+Alt+C";

    // ---- 行为 ----
    public bool AutoTranslate { get; set; } = true;
    public int DebounceMs { get; set; } = 350;
    public int IdleUnloadMinutes { get; set; } = 5;
    public bool StartWithWindows { get; set; }

    // ---- 隐私 ----
    public bool OfflineOnly { get; set; } = true;
    public bool LogSourceText { get; set; } // 默认 false，日志不记录原文
    public List<string> SensitiveProcessBlacklist { get; set; } = [];
    public bool PauseOnSensitiveWindow { get; set; } = true;

    // ---- 高级（v1.2 的 [OI] 兼容后端） ----
    public bool UseRemoteBackend { get; set; }
    public string? RemoteEndpoint { get; set; }
    public string? RemoteApiKey { get; set; }
    public string? RemoteModel { get; set; }

    // ---- 首次启动 ----
    public bool FirstRunCompleted { get; set; }
    public string? ModelDirectory { get; set; }

    [JsonIgnore]
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Translator", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 配置损坏时用默认值，不让程序起不来
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 写失败不影响运行
        }
    }
}

/// <summary>悬浮栏在某个显示器上的位置。</summary>
public sealed record BarPosition(double Left, double Top)
{
    /// <summary>是否按物理像素存储（跨 DPI 显示器时避免漂移）。</summary>
    public bool IsPhysical { get; init; }
}
