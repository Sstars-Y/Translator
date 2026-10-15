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
    private EdgeAnchor _anchor = EdgeAnchor.None;

    /// <summary>贴边时留的边距（DIP）。</summary>
    private const double EdgeMargin = 12;

    /// <summary>
    /// 吸附到哪条竖直边。None = 没让它贴边（只保证不出屏）；
    /// Unspecified = 位置来自上次记住的坐标，等拿到真实窗口宽度后再判定一次它贴的是哪条边。
    /// </summary>
    private enum EdgeAnchor { None, Left, Right, Unspecified }

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

        // 尺寸随内容变（SizeToContent），位置却由用户决定：
        // 尺寸一变就重新对齐吸附的那条边，并保证永远留在工作区内。
        SizeChanged += (_, _) => KeepAnchoredEdge();
        // 任务栏自动隐藏、分辨率/工作区变化：重新落屏，免得悬浮栏留在屏幕外。
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
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

        // 形态变了尺寸就变了：只做落屏校正，绝不主动挪位置（贴边只发生在拖动松手之后）。
        EnsureInsideWorkArea();
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

    /// <summary>
    /// 当前 DPI 缩放。窗口还没 Show 时 PresentationSource 为空，
    /// 这时只能信 MonitorEnumerator 报的每屏 DPI，否则 DIP 和物理像素会被算混。
    /// </summary>
    private double GetDpiScale(MonitorInfo? monitor = null)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target) return target.TransformToDevice.M11;

        var m = monitor ?? _currentMonitor;
        return m?.DpiScale > 0 ? m.DpiScale : 1.0;
    }

    /// <summary>窗口中心所在的那块屏；取不到时退回当前屏。</summary>
    private MonitorInfo? MonitorAtWindow()
    {
        try
        {
            var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
            return MonitorEnumerator.MonitorsUnderPoint((int)center.X, (int)center.Y);
        }
        catch (InvalidOperationException)
        {
            return _currentMonitor;
        }
    }

    /// <summary>把悬浮栏吸到最近的竖直边缘。只在拖动松手时调用，平时绝不自动挪动。</summary>
    private void SnapToNearestEdge()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var monitor = MonitorAtWindow() ?? _currentMonitor;
        if (monitor is null) return;
        _currentMonitor = monitor;

        double scale = GetDpiScale(monitor);
        double workLeft = monitor.WorkingArea.X / scale;
        double workTop = monitor.WorkingArea.Y / scale;
        double workRight = monitor.WorkingArea.Right / scale;
        double workBottom = monitor.WorkingArea.Bottom / scale;

        // 吸附到最近的水平边（左/右）
        double centerX = Left + ActualWidth / 2;
        double workCenterX = (workLeft + workRight) / 2;

        if (centerX < workCenterX)
        {
            _anchor = EdgeAnchor.Left;
            Left = workLeft + EdgeMargin;
        }
        else
        {
            _anchor = EdgeAnchor.Right;
            Left = workRight - ActualWidth - EdgeMargin;
        }

        // 垂直方向只限制在工作区内，不主动挪
        Top = Math.Clamp(Top, workTop + EdgeMargin,
            Math.Max(workTop + EdgeMargin, workBottom - ActualHeight - EdgeMargin));
    }

    /// <summary>
    /// 窗口尺寸变化后重新对齐：保持不变的是吸附的那条边，而不是 Left
    /// （SizeToContent 下状态文字一变宽，Left 不动就会把窗口顶出屏幕）。
    /// </summary>
    private void KeepAnchoredEdge()
    {
        var monitor = _currentMonitor ?? MonitorAtWindow();
        if (monitor is null) return;

        double scale = GetDpiScale(monitor);
        if (scale <= 0) scale = 1.0;

        if (_anchor == EdgeAnchor.Unspecified)
        {
            // 记住的位置当初可能就是贴边的，趁现在有真实宽度判定一次
            _anchor = InferAnchor(monitor, scale);
        }

        if (ActualWidth > 0 && _anchor is EdgeAnchor.Left or EdgeAnchor.Right)
        {
            double workLeft = monitor.WorkingArea.X / scale;
            double workRight = monitor.WorkingArea.Right / scale;

            Left = _anchor == EdgeAnchor.Right
                ? workRight - ActualWidth - EdgeMargin
                : workLeft + EdgeMargin;
        }

        ClampToWorkArea(monitor, scale);
    }

    /// <summary>只保证不出屏，不改变用户摆放的位置。</summary>
    private void EnsureInsideWorkArea()
    {
        var monitor = _currentMonitor ?? MonitorAtWindow();
        if (monitor is null) return;

        ClampToWorkArea(monitor, GetDpiScale(monitor));
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Dispatcher.BeginInvoke(EnsureInsideWorkArea);

    private void RestorePosition()
    {
        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0) return;

        _currentMonitor = monitors.FirstOrDefault(m =>
            _settings.BarPositions.ContainsKey(m.DeviceName)) ?? monitors[0];

        // 窗口此时还没 Show()，PresentationSource 为空，只能先信 MonitorEnumerator 的每屏 DPI。
        double scale = GetDpiScale(_currentMonitor);

        if (_settings.BarPositions.TryGetValue(_currentMonitor.DeviceName, out var pos))
        {
            Left = pos.IsPhysical ? pos.Left / scale : pos.Left;
            Top = pos.IsPhysical ? pos.Top / scale : pos.Top;
            // 贴的是哪条边等 Show() 拿到真实宽度后再判（见 KeepAnchoredEdge）
            _anchor = EdgeAnchor.Unspecified;
        }
        else
        {
            // 默认放主屏右上角，并按右边缘对齐
            var m = _currentMonitor;
            Left = (m.WorkingArea.Right / scale) - 160;
            Top = (m.WorkingArea.Y / scale) + 60;
            _anchor = EdgeAnchor.Right;
        }

        ClampToWorkArea(_currentMonitor, scale);
    }

    /// <summary>从记住的位置推断它当时贴的是哪条边（距边一个边距以内才算贴边）。</summary>
    private EdgeAnchor InferAnchor(MonitorInfo monitor, double scale)
    {
        if (ActualWidth <= 0) return EdgeAnchor.Unspecified;

        double workLeft = monitor.WorkingArea.X / scale;
        double workRight = monitor.WorkingArea.Right / scale;

        if (Math.Abs(Left - workLeft) <= EdgeMargin + 8) return EdgeAnchor.Left;
        if (Math.Abs(workRight - (Left + ActualWidth)) <= EdgeMargin + 8) return EdgeAnchor.Right;

        return EdgeAnchor.None;
    }

    /// <summary>把窗口夹回显示器工作区内，保证它一定看得见（尺寸还未知时用保守估计）。</summary>
    private void ClampToWorkArea(MonitorInfo monitor, double scale)
    {
        if (scale <= 0) scale = 1.0;

        double width = ActualWidth > 0 ? ActualWidth : 200;
        double height = ActualHeight > 0 ? ActualHeight : 48;

        double workLeft = monitor.WorkingArea.X / scale;
        double workTop = monitor.WorkingArea.Y / scale;
        double workRight = monitor.WorkingArea.Right / scale;
        double workBottom = monitor.WorkingArea.Bottom / scale;

        if (double.IsNaN(Left) || double.IsInfinity(Left)) Left = workLeft;
        if (double.IsNaN(Top) || double.IsInfinity(Top)) Top = workTop;

        Left = Math.Clamp(Left, workLeft, Math.Max(workLeft, workRight - width));
        Top = Math.Clamp(Top, workTop, Math.Max(workTop, workBottom - height));
    }

    /// <summary>Show() 之后调用：这时才能拿到真实 DPI 与窗口尺寸，做最后一次落屏校正。</summary>
    public void EnsureOnScreen()
    {
        var monitor = MonitorAtWindow() ?? _currentMonitor
            ?? MonitorEnumerator.GetMonitors().FirstOrDefault();
        if (monitor is null) return;

        _currentMonitor = monitor;
        KeepAnchoredEdge();
    }

    private void SavePosition()
    {
        if (_currentMonitor is null) return;
        double scale = GetDpiScale();
        _settings.BarPositions[_currentMonitor.DeviceName] =
            new BarPosition(Left * scale, Top * scale) { IsPhysical = true };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // 换屏或系统缩放变了：位置要按新 DPI 重算，否则会漂
        Dispatcher.BeginInvoke(KeepAnchoredEdge);
    }

    /// <summary>
    /// 供布局算法避让悬浮栏（屏幕绝对物理像素）。
    /// PointToScreen 已经是物理像素，而 ActualWidth/Height 是 DIP，要按缩放换算。
    /// </summary>
    public RectInt GetBarBounds()
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            double scale = GetDpiScale(_currentMonitor);
            return new RectInt(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                Math.Max(1, (int)Math.Round(ActualWidth * scale)),
                Math.Max(1, (int)Math.Round(ActualHeight * scale)));
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }
}
