using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using System.Drawing;
using Translator.Core.Configuration;
using Translator.Core.Diagnostics;

namespace Translator.App;

/// <summary>
/// 应用入口。负责：单实例、托盘图标、全局异常处理、生命周期。
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private WinForms.NotifyIcon? _trayIcon;
    private AppController? _controller;
    private AppLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 单实例 ----
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            name: @"Global\TranslatorScreenTranslate_SingleInstance", out bool isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("屏幕翻译已在运行。", "屏幕翻译",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // ---- 日志（隐私默认：不记录原文） ----
        _log = new AppLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Translator", "logs", "app.log"))
        {
            MinimumLevel = LogLevel.Info,
        };
        _log.Info("======== 应用启动 ========");

        // ---- 全局异常处理 ----
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log?.Error("未处理的域异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        // ---- 配置 ----
        var settings = AppSettings.Load();
        _log.LogSourceText = settings.LogSourceText;

        // ---- 托盘图标必须先于向导创建，否则向导关闭后 WPF 没有窗口锚点就会立即退出 ----
        SetupTrayIcon(settings);

        // ---- 首次启动向导 ----
        if (!settings.FirstRunCompleted)
        {
            var wizard = new Views.FirstRunWindow(settings, _log);
            wizard.Completed += (_, updated) =>
            {
                updated.Save();
                settings = updated;
                _log.LogSourceText = settings.LogSourceText;
            };
            wizard.ShowDialog();
        }

        // ---- 主控 ----
        _controller = new AppController(settings, _log);

        try
        {
            _ = _controller.StartAsync();
        }
        catch (Exception ex)
        {
            _log.Error("启动失败", ex);
            System.Windows.MessageBox.Show($"启动失败：{ex.Message}", "屏幕翻译",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void SetupTrayIcon(AppSettings settings)
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = "屏幕翻译",
            Icon = SystemIcons.Application,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏悬浮栏", null, (_, _) => ToggleBar());
        menu.Items.Add("框选工作区", null, (_, _) => _ = _controller?.StartRegionCapturePublicAsync()!);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => _controller?.OpenSettingsPublic());
        menu.Items.Add("打开术语文件", null, (_, _) => OpenTermsFile());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ToggleBar();
    }

    private void ToggleBar()
        => Dispatcher.Invoke(() => _controller?.ToggleBarVisibility());

    private static void OpenTermsFile()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Translator", "terms.json");
        if (!File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenLogFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Translator", "logs");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("界面线程异常", e.Exception);

        // 捕获相关的异常比较常见（受保护内容、独占全屏），给明确说明
        var message = e.Exception switch
        {
            _ when e.Exception.Message.Contains("BitBlt") =>
                "捕获失败：该区域可能受保护（例如播放器、银行页面），或正处于独占全屏。",
            _ => $"发生错误：{e.Exception.Message}\n详情见日志。",
        };

        System.Windows.MessageBox.Show(message, "屏幕翻译", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("======== 应用退出 ========");

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _controller?.Dispose();
        _log?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
