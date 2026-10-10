using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Translator.App.Interop;
using Translator.Core.Configuration;
using Translator.Core.Models;
using Translator.Platform.Capture;
using Translator.Platform.Windowing;

namespace Translator.App.Views;

/// <summary>
/// 悬浮栏：三种形态（收起、紧凑、展开）。
/// 不抢焦点、不出现在 Alt+Tab、可拖动、按显示器记住位置。
/// </summary>
public partial class FloatingBarWindow : Window
{
    private AppSettings _settings = new();
    private MonitorInfo? _currentMonitor;
    private bool _isDragging;
    private Point _dragStart;
    private double _dragStartLeft, _dragStartTop;
    private BarForm _form = BarForm.Compact;

    /// <summary>工作区框选完成。</summary>
    public event EventHandler<RectInt>? RegionSelected;

    /// <summary>开始/暂停切换。</summary>
    public event EventHandler<bool>? PauseToggled;

    /// <summary>全部折叠/展开。</summary>
    public event EventHandler? CollapseAllRequested;

    /// <summary>手动翻译一次。</summary>
    public event EventHandler? TranslateOnceRequested;

    /// <summary>打开设置。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>领域选择变化。</summary>
    public event EventHandler<string?>? DomainChanged;

    /// <summary>透明度变化。</summary>
    public event EventHandler<double>? OpacityChanged;

    /// <summary>自动翻译开关变化。</summary>
    public event EventHandler<bool>? AutoTranslateChanged;

    public FloatingBarWindow()
    {
        InitializeComponent();
        OverlayWindow.Configure(this, clickThrough: false, excludeFromCapture: true);
    }

    public bool IsPaused { get; private set; }
    public BarForm Form => _form;

    public void Initialize(AppSettings settings, IReadOnlyList<string>? domains = null)
    {
        _settings = settings;
        _form = settings.BarForm;

        OpacitySlider.Value = settings.BubbleOpacity;
        AutoTranslateBox.IsChecked = settings.AutoTranslate;

        if (domains is not null)
        {
            DomainBox.ItemsSource = new[] { "（无）" }.Concat(domains).ToList();
            DomainBox.SelectedIndex = string.IsNullOrEmpty(settings.ActiveDomain)
                ? 0
                : Math.Max(0, domains.ToList().IndexOf(settings.ActiveDomain) + 1);
        }

        ApplyForm();
        RestorePosition();
    }

    /// <summary>切换悬浮栏形态。</summary>
    public void SetForm(BarForm form)
    {
        _form = form;
        _settings.BarForm = form;
        ApplyForm();
    }

    private void ApplyForm()
    {
        CollapsedShell.Visibility = _form == BarForm.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        ExpandedShell.Visibility = _form == BarForm.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        QuickSettings.Visibility = _form == BarForm.Expanded ? Visibility.Visible : Visibility.Collapsed;

        // 展开/收起时重新贴边
        if (_settings.BarSnapToEdges && IsLoaded) SnapToNearestEdge();
    }

    /// <summary>点状态点或把手时在收起与紧凑之间切换。</summary>
    public void ToggleCollapsed()
        => SetForm(_form == BarForm.Collapsed ? BarForm.Compact : BarForm.Collapsed);

    /// <summary>更新状态指示：运行中（绿）、暂停（灰）、错误（红）、加载中（橙）。</summary>
    public void SetStatus(string status, StatusKind kind = StatusKind.Ok)
    {
        var color = kind switch
        {
            StatusKind.Ok => Color.FromRgb(0x4C, 0xAF, 0x50),
            StatusKind.Paused => Color.FromRgb(0x9E, 0x9E, 0x9E),
            StatusKind.Busy => Color.FromRgb(0xFF, 0x98, 0x00),
            StatusKind.Error => Color.FromRgb(0xF4, 0x43, 0x36),
            _ => Colors.Gray,
        };

        CollapsedDot.Fill = new SolidColorBrush(color);
        StatusText.Text = status;
    }

    public enum StatusKind { Ok, Paused, Busy, Error }

    /// <summary>暂停/恢复按钮外观。</summary>
    private void SetPaused(bool paused)
    {
        IsPaused = paused;
        BtnPause.Content = paused ? "" : ""; // Play : Pause
        SetStatus(paused ? "已暂停" : "运行中", paused ? StatusKind.Paused : StatusKind.Ok);
    }

    // ---------- 事件 ----------

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        SetPaused(!IsPaused);
        PauseToggled?.Invoke(this, IsPaused);
    }

    private async void OnCapture(object sender, RoutedEventArgs e)
    {
        // 收起悬浮栏，避免被框选遮罩挡住
        var previous = _form;
        SetForm(BarForm.Collapsed);
        Hide();

        try
        {
            await Task.Delay(120); // 等窗口真正隐藏

            var monitor = _currentMonitor ?? MonitorEnumerator.GetMonitors().FirstOrDefault();
            if (monitor is null) return;

            var selector = new RegionSelectorWindow();
            selector.ShowFor(monitor);

            var result = selector.SelectedRegion;
            if (result is not null)
                RegionSelected?.Invoke(this, result.Value);
        }
        finally
        {
            Show();
            SetForm(previous);
        }
    }

    private void OnToggleCollapseAll(object sender, RoutedEventArgs e)
        => CollapseAllRequested?.Invoke(this, EventArgs.Empty);

    private void OnTranslateOnce(object sender, RoutedEventArgs e)
        => TranslateOnceRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenSettings(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnDomainChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = DomainBox.SelectedItem as string;
        if (value == "（无）") value = null;
        DomainChanged?.Invoke(this, value);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OpacityChanged?.Invoke(this, e.NewValue);

    private void OnAutoTranslateChanged(object sender, RoutedEventArgs e)
        => AutoTranslateChanged?.Invoke(this, AutoTranslateBox.IsChecked == true);

    // ---------- 拖动与吸附 ----------

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 双击在收起/紧凑间切换
        if (e.ClickCount == 2)
        {
            ToggleCollapsed();
            return;
        }

        _isDragging = true;
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;

        ((UIElement)sender).CaptureMouse();
        MouseMove += OnBarMouseMove;
        MouseUp += OnBarMouseUp;
    }

    private void OnBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var current = PointToScreen(e.GetPosition(this));
        Left = _dragStartLeft + (current.X - _dragStart.X) / GetDpiScale();
        Top = _dragStartTop + (current.Y - _dragStart.Y) / GetDpiScale();
    }

    private void OnBarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;

        MouseMove -= OnBarMouseMove;
        MouseUp -= OnBarMouseUp;
        Mouse.Capture(null);

        if (_settings.BarSnapToEdges) SnapToNearestEdge();
        SavePosition();
    }

    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    /// <summary>把悬浮栏吸到最近的屏幕边缘。</summary>
    private void SnapToNearestEdge()
    {
        var monitor = MonitorEnumerator.MonitorsUnderPoint(
            (int)PointToScreen(new Point(0, 0)).X, (int)PointToScreen(new Point(0, 0)).Y)
            ?? _currentMonitor;
        if (monitor is null) return;

        double scale = GetDpiScale();
        double workLeft = monitor.WorkingArea.X / scale;
        double workTop = monitor.WorkingArea.Y / scale;
        double workRight = monitor.WorkingArea.Right / scale;
        double workBottom = monitor.WorkingArea.Bottom / scale;

        const double margin = 12;

        // 吸附到最近的水平边（左/右）
        double centerX = Left + ActualWidth / 2;
        double workCenterX = (workLeft + workRight) / 2;

        Left = centerX < workCenterX
            ? workLeft + margin
            : workRight - ActualWidth - margin;

        // 垂直方向限制在工作区内
        Top = Math.Clamp(Top, workTop + margin, workBottom - ActualHeight - margin);
    }

    private void RestorePosition()
    {
        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0) return;

        _currentMonitor = monitors.FirstOrDefault(m =>
            _settings.BarPositions.ContainsKey(m.DeviceName)) ?? monitors[0];

        double scale = GetDpiScale();

        if (_settings.BarPositions.TryGetValue(_currentMonitor.DeviceName, out var pos))
        {
            Left = pos.IsPhysical ? pos.Left / scale : pos.Left;
            Top = pos.IsPhysical ? pos.Top / scale : pos.Top;
        }
        else
        {
            // 默认放主屏右上角
            var m = _currentMonitor;
            Left = (m.WorkingArea.Right / scale) - 160;
            Top = (m.WorkingArea.Y / scale) + 60;
        }
    }

    private void SavePosition()
    {
        if (_currentMonitor is null) return;
        double scale = GetDpiScale();
        _settings.BarPositions[_currentMonitor.DeviceName] =
            new BarPosition(Left * scale, Top * scale) { IsPhysical = true };
    }

    /// <summary>供布局算法避让悬浮栏。</summary>
    public RectInt GetBarBounds()
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            return new RectInt((int)topLeft.X, (int)topLeft.Y,
                (int)ActualWidth, (int)ActualHeight);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }
}
