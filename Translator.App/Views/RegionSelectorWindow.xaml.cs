using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Translator.Core.Models;
using Translator.Platform.Capture;

namespace Translator.App.Views;

/// <summary>
/// 框选工作区：全屏半透明遮罩，支持多显示器和 DPI。
/// 像截图工具一样拖出一块区域。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private Point _startPoint;
    private bool _isDragging;
    private MonitorInfo? _monitor;
    private double _dpiScale = 1.0;

    /// <summary>用户选定的区域（屏幕物理像素坐标）。取消时为 null。</summary>
    public RectInt? SelectedRegion { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();

        // 遮罩本身要能接收鼠标，但不能进 Alt+Tab
        ShowInTaskbar = false;
        Topmost = true;

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        Loaded += OnLoaded;
    }

    /// <summary>在指定显示器上显示选择器。</summary>
    public void ShowFor(MonitorInfo monitor)
    {
        _monitor = monitor;
        _dpiScale = monitor.DpiScale > 0 ? monitor.DpiScale : 1.0;

        Left = monitor.Bounds.X / _dpiScale;
        Top = monitor.Bounds.Y / _dpiScale;
        Width = monitor.Bounds.Width / _dpiScale;
        Height = monitor.Bounds.Height / _dpiScale;

        Show();
        Activate();
        Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 提示放在屏幕上方居中
        Canvas.SetLeft(HintLabel, (Width - HintLabel.ActualWidth) / 2);
        Canvas.SetTop(HintLabel, Height * 0.12);
        HintLabel.Visibility = Visibility.Visible;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(RootCanvas);
        _isDragging = true;
        HintLabel.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Visible;
        SizeLabel.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var current = e.GetPosition(RootCanvas);
        var rect = MakeRect(_startPoint, current);

        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;

        // 尺寸提示跟在选区右下角
        int physW = (int)Math.Round(rect.Width * _dpiScale);
        int physH = (int)Math.Round(rect.Height * _dpiScale);
        SizeText.Text = $"{physW} × {physH}";

        double labelLeft = Math.Min(rect.Right + 8, Width - 90);
        double labelTop = rect.Bottom + 8;
        if (labelTop > Height - 30) labelTop = rect.Y - 28;
        Canvas.SetLeft(SizeLabel, labelLeft);
        Canvas.SetTop(SizeLabel, labelTop);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();

        var current = e.GetPosition(RootCanvas);
        var rect = MakeRect(_startPoint, current);

        // 太小的选区视为误触
        if (rect.Width < 12 || rect.Height < 12)
        {
            SelectedRegion = null;
            Close();
            return;
        }

        // 换算成屏幕物理像素坐标
        int ox = _monitor?.Bounds.X ?? 0;
        int oy = _monitor?.Bounds.Y ?? 0;

        SelectedRegion = new RectInt(
            ox + (int)Math.Round(rect.X * _dpiScale),
            oy + (int)Math.Round(rect.Y * _dpiScale),
            (int)Math.Round(rect.Width * _dpiScale),
            (int)Math.Round(rect.Height * _dpiScale));

        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = null;
            Close();
        }
    }

    private static Rect MakeRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
               Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
