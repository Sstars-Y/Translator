using System.Windows;
using Translator.Core.Configuration;
using Translator.Core.Diagnostics;
using Translator.Platform.Ocr;
using Translator.Platform.Translation;

namespace Translator.App.Views;

/// <summary>
/// 首次启动向导：检测 OCR 语言包、下载翻译模型、说明快捷键。
/// 验收标准（路线图阶段 7）：一台全新的电脑，从安装到第一次翻译，
/// 用户不需要看文档。
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly ModelDownloader _downloader = new();
    private bool _modelReady;
    private bool _downloading;

    /// <summary>向导完成。</summary>
    public event EventHandler<AppSettings>? Completed;

    public FirstRunWindow(AppSettings settings, AppLog log)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;

        Hotkey1Text.Text = settings.HotkeyCapture;
        Hotkey2Text.Text = settings.HotkeyTogglePause;
        Hotkey3Text.Text = settings.HotkeyCollapseAll;

        Loaded += (_, _) => CheckEnvironment();
    }

    private void CheckEnvironment()
    {
        // ---- 翻译模型 ----
        var modelDir = _settings.ModelDirectory ?? DefaultModelDir();
        var paths = new ModelPaths(modelDir, Path.Combine(modelDir, "config.txt"));

        if (paths.IsPresent)
        {
            _modelReady = true;
            ModelStepIcon.Text = "✓";
            ModelStepIcon.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ModelStatusText.Text = $"已就绪：{modelDir}";
            DownloadButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelStatusText.Text = $"尚未下载。模型约 45 MB，下载后即可离线翻译。\n目标位置：{modelDir}";
        }

        // ---- OCR 语言包 ----
        var installed = WindowsOcr.GetInstalledLanguages();
        bool hasEnglish = installed.Any(l => l.Tag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        if (hasEnglish)
        {
            OcrStepIcon.Text = "✓";
            OcrStepIcon.Foreground = System.Windows.Media.Brushes.SeaGreen;
            OcrStatusText.Text = $"已安装：{string.Join("、", installed.Select(l => l.Tag))}";
        }
        else
        {
            OcrStepIcon.Foreground = System.Windows.Media.Brushes.Orange;
            OcrStatusText.Text =
                $"系统当前只装了：{string.Join("、", installed.Select(l => l.Tag))}。\n" +
                "缺少英文语言包时，引擎会用中文模型读英文，识别质量会下降" +
                "（例如把 “Results” 读成 “ResuIts”）。建议安装英文 OCR 包。";
            OcrHelpButton.Visibility = Visibility.Visible;
            _log.Warn("首次启动：未检测到英文 OCR 语言包。");
        }
    }

    private static string DefaultModelDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Translator", "models", "en-zh");

    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;

        DownloadButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        ModelProgress.Value = 0;

        var modelDir = _settings.ModelDirectory ?? DefaultModelDir();
        var progress = new Progress<DownloadProgress>(p =>
        {
            ModelStatusText.Text = p.TotalBytes is > 0
                ? $"正在下载 {p.FileName}（{p.FileIndex + 1}/{p.FileCount}）… {p.Percent:P0}"
                : $"正在处理 {p.FileName}（{p.FileIndex + 1}/{p.FileCount}）…";

            if (p.Percent is double pct) ModelProgress.Value = pct * 100;
        });

        try
        {
            var paths = await _downloader.DownloadAsync("en-zh", modelDir, progress);

            _settings.ModelDirectory = paths.Directory;
            _modelReady = paths.IsPresent;

            ModelStepIcon.Text = "✓";
            ModelStepIcon.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ModelStatusText.Text = $"下载完成：{paths.Directory}";
            ModelProgress.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;

            _log.Info($"模型下载完成：{paths.Directory}");
        }
        catch (Exception ex)
        {
            _log.Error("模型下载失败", ex);
            ModelStatusText.Text = $"下载失败：{ex.Message}\n可稍后在设置里重试。";
            ModelProgress.Visibility = Visibility.Collapsed;
            DownloadButton.IsEnabled = true;
        }
        finally
        {
            _downloading = false;
        }
    }

    private void OnShowOcrHelp(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "安装英文 OCR 语言包：\n\n" +
            "方式一（推荐）\n" +
            "  设置 → 时间和语言 → 语言和区域\n" +
            "  → 添加语言 → 英语（美国）\n" +
            "  → 只勾选“光学字符识别”，不必设为显示语言\n\n" +
            "方式二（管理员 PowerShell）\n" +
            "  Add-WindowsCapability -Online -Name Language.OCR~~~en-US~0.0.1.0\n\n" +
            "安装后重启本程序即可生效。",
            "安装英文 OCR 语言包", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        // 跳过也要标记完成，避免每次启动都弹
        _settings.FirstRunCompleted = true;
        Completed?.Invoke(this, _settings);
        Close();
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        _settings.FirstRunCompleted = true;

        if (!_modelReady && string.IsNullOrEmpty(_settings.ModelDirectory))
        {
            var answer = MessageBox.Show(this,
                "翻译模型还没下载，暂时无法翻译。\n要现在退出向导去下载吗？",
                "屏幕翻译", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes) return;
        }

        Completed?.Invoke(this, _settings);
        Close();
    }
}
