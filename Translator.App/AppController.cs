using System.Windows;
using System.Windows.Threading;
using Translator.App.Interop;
using Translator.App.Views;
using Translator.Core;
using Translator.Core.Abstractions;
using Translator.Core.Caching;
using Translator.Core.Configuration;
using Translator.Core.Diagnostics;
using Translator.Core.Models;
using Translator.Core.Terms;
using Translator.Core.Text;
using Translator.Platform.Capture;
using Translator.Platform.Ocr;
using Translator.Platform.Translation;
using Translator.Platform.Windowing;

namespace Translator.App;

/// <summary>
/// 应用主控：把流水线、气泡层、悬浮栏、快捷键、隐私策略串起来。
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;

    private ICapture _capture = null!;
    private IOcr _ocr = null!;
    private ITranslator _translator = null!;
    private TermLayer _terms = null!;
    private TranslationCache _cache = null!;
    private TranslationPipeline _pipeline = null!;
    private WindowManager _windowManager = null!;
    private HotkeyManager? _hotkeys;

    private BubbleOverlayWindow? _bubbleWindow;
    private FloatingBarWindow? _barWindow;
    private DebugOverlayWindow? _debugWindow;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _debounceTimer;

    private RectInt? _region;
    private bool _isPaused;
    private bool _isBusy;
    private string? _lastForegroundProcess;
    private DateTimeOffset _lastActivity = DateTimeOffset.Now;

    public AppController(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;
    }

    public IReadOnlyList<string> AvailableDomains { get; private set; } = [];

    /// <summary>启动应用。</summary>
    public async Task StartAsync()
    {
        BuildPipeline();

        _barWindow = new FloatingBarWindow();
        _barWindow.Initialize(_settings, AvailableDomains);
        _barWindow.RegionSelected += (_, region) => _ = OnRegionSelectedAsync(region);
        _barWindow.PauseToggled += (_, paused) => SetPaused(paused);
        _barWindow.CollapseAllRequested += (_, _) => _bubbleWindow?.ToggleCollapseAll();
        _barWindow.TranslateOnceRequested += (_, _) => _ = TranslateOnceAsync();
        _barWindow.SettingsRequested += (_, _) => OpenSettings();
        _barWindow.DomainChanged += (_, domain) => OnDomainChanged(domain);
        _barWindow.OpacityChanged += (_, value) => _settings.BubbleOpacity = value;
        _barWindow.AutoTranslateChanged += (_, enabled) => _settings.AutoTranslate = enabled;
        _barWindow.Show();

        _bubbleWindow = new BubbleOverlayWindow();
        _bubbleWindow.Initialize(_settings);
        var monitor = _capture.GetMonitors().FirstOrDefault();
        if (monitor is not null) _bubbleWindow.AttachToMonitor(monitor);
        _bubbleWindow.Show();

        if (_settings.ShowDebugOverlay)
        {
            _debugWindow = new DebugOverlayWindow();
            _debugWindow.AttachToMonitor(monitor ?? _capture.GetMonitors().First());
            _debugWindow.Show();
        }

        RegisterHotkeys();
        StartTimers();

        _log.Info($"启动完成。捕获后端={_capture.BackendName}, OCR={_ocr.LanguageTag}, 翻译={_translator.BackendName}");

        if (_ocr is WindowsOcr wo && wo.IsFallbackLanguage)
        {
            _barWindow.SetStatus("缺少英文 OCR 语言包，识别质量下降", FloatingBarWindow.StatusKind.Busy);
            _log.Warn("未找到 en-US OCR 语言包，已退回系统默认语言引擎。");
        }

        // 后台预热模型，不阻塞界面
        _ = Task.Run(async () =>
        {
            try
            {
                await _translator.WarmUpAsync();
                _log.Info("翻译模型预热完成。");
            }
            catch (Exception ex)
            {
                _log.Error("翻译模型预热失败", ex);
                await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
                    _barWindow?.SetStatus("翻译模型未就绪，请在设置中下载", FloatingBarWindow.StatusKind.Error));
            }
        });
    }

    private void BuildPipeline()
    {
        // 捕获后端：GDI BitBlt（无黄框、Win10/11 通用）
        _capture = new GdiCapture();

        _ocr = new WindowsOcr(_settings.SourceLanguage switch
        {
            "en" => "en-US",
            _ => "en-US",
        });

        var modelDir = _settings.ModelDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Translator", "models", "en-zh");

        _translator = new BergamotTranslator(new ModelPaths(modelDir, Path.Combine(modelDir, "config.txt")))
        {
            IdleUnloadAfter = TimeSpan.FromMinutes(Math.Max(0, _settings.IdleUnloadMinutes)),
        };

        _terms = new TermLayer();
        LoadTerms();
        AvailableDomains = _terms.AllTerms
            .Where(t => t.Scope == TermScope.Domain && !string.IsNullOrEmpty(t.ScopeValue))
            .Select(t => t.ScopeValue!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cache = new TranslationCache(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Translator", "cache.json"));

        _windowManager = new WindowManager(
            _settings.SensitiveProcessBlacklist.Count > 0
                ? _settings.SensitiveProcessBlacklist
                : null);

        _pipeline = new TranslationPipeline(_capture, _ocr, _translator, _terms, _cache, new PipelineOptions
        {
            TargetLanguage = _settings.TargetLanguage,
            BubbleWidthFactor = _settings.BubbleWidthFactor,
            BubbleLineHeight = (int)Math.Round(_settings.BubbleFontSize * 1.6),
        });
    }

    /// <summary>从本地目录加载术语文件（阶段 6：先不做导入界面，手动编辑）。</summary>
    private void LoadTerms()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Translator", "terms.json");

            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, """
                    [
                      { "source": "Mana Reactor", "target": "魔能反应堆", "scope": "Global" },
                      { "source": "workspace", "target": "工作区", "scope": "Domain", "scopeValue": "engineering" }
                    ]
                    """);
                _log.Info($"已创建示例术语文件：{path}");
            }

            var json = File.ReadAllText(path);
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<TermEntry>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (entries is not null)
            {
                _terms.SetTerms(entries);
                _terms.ActiveDomain = _settings.ActiveDomain;
                _log.Info($"已加载 {entries.Count} 条术语，版本 {_terms.Version}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("加载术语文件失败", ex);
        }
    }

    private void RegisterHotkeys()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(_barWindow!).Handle;
        if (handle == IntPtr.Zero)
        {
            _log.Warn("悬浮栏窗口句柄尚未创建，快捷键未注册。");
            return;
        }

        _hotkeys = new HotkeyManager(handle);
        _hotkeys.RegisterAll([
            new HotkeyDefinition("capture", _settings.HotkeyCapture, () => _ = StartRegionCaptureAsync()),
            new HotkeyDefinition("pause", _settings.HotkeyTogglePause, () => SetPaused(!_isPaused)),
            new HotkeyDefinition("collapse", _settings.HotkeyCollapseAll, () => _bubbleWindow?.ToggleCollapseAll()),
        ]);

        foreach (var (id, reason) in _hotkeys.Conflicts)
        {
            _log.Warn($"快捷键未注册 [{id}]：{reason}");
            _barWindow?.SetStatus(reason, FloatingBarWindow.StatusKind.Busy);
        }
    }

    private void StartTimers()
    {
        // 轮询画面变化
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(120, _settings.DebounceMs)) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();

        // 空闲卸载模型，释放内存
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += (_, _) =>
        {
            if (_translator is BergamotTranslator bt) bt.UnloadIfIdle();
            _cache.Flush();
        };
        _idleTimer.Start();

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.DebounceMs) };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer!.Stop();
            await RunPipelineAsync();
        };
    }

    private async Task PollAsync()
    {
        if (_isPaused || _isBusy || _region is null) return;
        if (!_settings.AutoTranslate) return;

        // 前台窗口切换 / 敏感窗口 → 隐藏气泡并暂停
        var fg = WindowManager.GetForegroundWindow();
        if (fg.Handle == IntPtr.Zero) return;

        bool windowChanged = fg.ProcessName != _lastForegroundProcess;
        _lastForegroundProcess = fg.ProcessName;

        if (windowChanged)
        {
            _bubbleWindow?.HideAll();
            if (_settings.PauseOnSensitiveWindow &&
                (_windowManager.IsSensitiveWindow(fg.ProcessName) || _windowManager.IsSensitiveTitle(fg.Title)))
            {
                _log.Info($"检测到敏感窗口，已暂停：{fg.ProcessName}");
                _barWindow?.SetStatus("检测到敏感窗口，已暂停", FloatingBarWindow.StatusKind.Paused);
                return;
            }
        }

        if (fg.IsMinimized) return;

        // 用变化检测决定是否需要重算
        await RunPipelineAsync();
    }

    private async Task RunPipelineAsync()
    {
        if (_region is not RectInt region || _isBusy || _isPaused) return;

        _isBusy = true;
        try
        {
            var exclusions = new List<RectInt>();
            if (_barWindow is not null) exclusions.Add(_barWindow.GetBarBounds());
            if (_bubbleWindow is not null) exclusions.AddRange(_bubbleWindow.GetOccupiedRegions());

            var result = await _pipeline.RunAsync(region, exclusions);

            if (result.Placements.Count == 0 && result.Blocks.Count == 0)
                return; // 画面没变

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _bubbleWindow?.ShowAll();

                // 先注入译文，再布局渲染
                if (_bubbleWindow is not null)
                {
                    foreach (var (blockId, translated) in result.Translations)
                        _bubbleWindow.SetTranslation(blockId, translated);
                }

                _bubbleWindow?.ApplyLayout(result.Placements, result.Blocks);

                _debugWindow?.Render(result.Ocr, result.Blocks, result.Placements,
                    result.Occupied, result.Diagnostics, result.Candidates);

                var d = result.Diagnostics;
                _barWindow?.SetStatus(
                    $"{d.BlockCount} 段 · 译 {d.TranslatedCount} · 缓存 {d.CacheHits}\n" +
                    $"{d.OcrMs}ms OCR / {d.TranslateMs}ms 翻译");
            });

            _lastActivity = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            _log.Error("流水线执行失败", ex);
            _barWindow?.SetStatus("翻译出错，详见日志", FloatingBarWindow.StatusKind.Error);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task StartRegionCaptureAsync()
    {
        if (_barWindow is null) return;

        _barWindow.Hide();
        _bubbleWindow?.ClearAll();
        await Task.Delay(150);

        try
        {
            var monitor = MonitorEnumerator.MonitorsUnderPoint(
                (int)(SystemParameters.PrimaryScreenWidth / 2),
                (int)(SystemParameters.PrimaryScreenHeight / 2))
                ?? _capture.GetMonitors().FirstOrDefault();

            if (monitor is null) return;

            var selector = new RegionSelectorWindow();
            selector.ShowFor(monitor);

            if (selector.SelectedRegion is RectInt selected)
                await OnRegionSelectedAsync(selected);
        }
        finally
        {
            _barWindow.Show();
        }
    }

    private async Task OnRegionSelectedAsync(RectInt region)
    {
        _region = region;
        _bubbleWindow?.ClearAll();
        _log.Info($"已选定工作区 {region}");

        // 强制重算一次
        _pipeline.ForceRecompute = true;
        await RunPipelineAsync();
    }

    private async Task TranslateOnceAsync()
    {
        if (_region is null)
        {
            await StartRegionCaptureAsync();
            return;
        }
        _pipeline.ForceRecompute = true;
        await RunPipelineAsync();
    }

    private void SetPaused(bool paused)
    {
        _isPaused = paused;
        if (paused) _bubbleWindow?.HideAll();
        else _bubbleWindow?.ShowAll();
        _log.Info(paused ? "已暂停" : "已恢复");
    }

    private void OnDomainChanged(string? domain)
    {
        _terms.ActiveDomain = domain;
        _settings.ActiveDomain = domain;
        _cache.Clear(); // 领域变了，缓存键里的术语版本也变了
        _pipeline.ForceRecompute = true;
        _log.Info($"切换领域：{domain ?? "（无）"}");
    }

    private void OpenSettings()
    {
        var diagnostics = BuildDiagnostics();
        var window = new SettingsWindow(_settings, AvailableDomains, diagnostics);
        window.SettingsSaved += (_, updated) =>
        {
            updated.Save();
            ApplySettings(updated);
        };
        window.ClearCacheRequested += (_, _) =>
        {
            _cache.Clear();
            MessageBox.Show("翻译缓存已清除。", "屏幕翻译", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        window.ShowDialog();
    }

    private void ApplySettings(AppSettings updated)
    {
        _log.LogSourceText = updated.LogSourceText;
        _bubbleWindow?.UpdateTheme();
        _bubbleWindow?.ClearAll();

        // 快捷键可能改了，重新注册
        _hotkeys?.Dispose();
        RegisterHotkeys();

        _pipeline.ForceRecompute = true;
        _barWindow?.SetStatus("设置已更新", FloatingBarWindow.StatusKind.Ok);
    }

    private string BuildDiagnostics()
    {
        var cache = _cache.GetStats();
        var mem = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        return $"""
            捕获后端 : {_capture.BackendName}
            OCR 引擎 : {_ocr.LanguageTag}（可用={_ocr.IsAvailable}）
            翻译后端 : {_translator.BackendName}（已加载={_translator.IsReady}）
            术语版本 : {_terms.Version}  条目 {_terms.AllTerms.Count}
            缓存     : {cache.Count} 条，命中率 {cache.HitRate:P0}
            内存     : 工作集 {mem} MB
            工作区   : {(_region?.ToString() ?? "未选择")}
            """;
    }

    // ---------- 供托盘菜单调用的公开入口 ----------

    /// <summary>托盘：框选工作区。</summary>
    public Task StartRegionCapturePublicAsync() => StartRegionCaptureAsync();

    /// <summary>托盘：打开设置。</summary>
    public void OpenSettingsPublic() => OpenSettings();

    /// <summary>托盘：显示 / 隐藏悬浮栏。</summary>
    public void ToggleBarVisibility()
    {
        if (_barWindow is null) return;
        if (_barWindow.IsVisible) _barWindow.Hide();
        else _barWindow.Show();
    }

    public void Dispose()
    {
        _pollTimer?.Stop();
        _idleTimer?.Stop();
        _debounceTimer?.Stop();
        _hotkeys?.Dispose();
        _debugWindow?.Close();
        _cache?.Dispose();
        _capture?.Dispose();
        _ocr?.Dispose();
        _translator?.Dispose();
    }
}
