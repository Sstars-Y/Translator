using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Translator.Core;
using Translator.Core.Layout;
using Translator.Core.Models;
using CoreTextBlock = Translator.Core.Models.TextBlock;
using Translator.Core.Text;

namespace Translator.App.Views;

/// <summary>
/// 调试覆盖层：显示 OCR 框、占用图、候选位置与流水线耗时。
///
/// 路线图说"越早做越省时间"，因为它能把布局算法的行为可视化 ——
/// 气泡为什么放在那里、为什么降级成小图标，一眼就能看出来。
/// </summary>
public partial class DebugOverlayWindow : Window
{
    private MonitorInfo? _monitor;

    public DebugOverlayWindow()
    {
        InitializeComponent();
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
    }

    public void AttachToMonitor(MonitorInfo monitor)
    {
        _monitor = monitor;
        Left = monitor.Bounds.X / (monitor.DpiScale > 0 ? monitor.DpiScale : 1);
        Top = monitor.Bounds.Y / (monitor.DpiScale > 0 ? monitor.DpiScale : 1);
        Width = monitor.Bounds.Width / (monitor.DpiScale > 0 ? monitor.DpiScale : 1);
        Height = monitor.Bounds.Height / (monitor.DpiScale > 0 ? monitor.DpiScale : 1);
    }

    /// <summary>把一次流水线的中间结果画出来。</summary>
    public void Render(
        OcrResult ocr,
        IReadOnlyList<CoreTextBlock> blocks,
        IReadOnlyList<BubblePlacement> placements,
        IReadOnlyList<RectInt> occupied,
        PipelineDiagnostics diag,
        IReadOnlyList<RectInt> candidates)
    {
        // 清掉上一次的图形（保留面板）
        foreach (var shape in OverlayCanvas.Children.OfType<Shape>().ToList())
            OverlayCanvas.Children.Remove(shape);

        int ox = _monitor?.Bounds.X ?? 0;
        int oy = _monitor?.Bounds.Y ?? 0;

        // 1. 占用区（最先画，压在底层）
        foreach (var rect in occupied)
            DrawRect(rect, ox, oy, Colors.Transparent, Color.FromArgb(0x50, 0x44, 0x44, 0xFF), 1);

        // 2. OCR 行框
        foreach (var line in ocr.Lines)
            DrawRect(line.Bounds, ox, oy, Colors.Transparent, Color.FromArgb(0xB0, 0x00, 0xFF, 0x00), 1);

        // 3. 段落框
        foreach (var block in blocks)
            DrawRect(block.Bounds, ox, oy, Colors.Transparent, Color.FromArgb(0xC0, 0xFF, 0x88, 0x00), 2);

        // 4. 候选位置（淡蓝色虚线）
        foreach (var cand in candidates)
            DrawRect(cand, ox, oy, Colors.Transparent, Color.FromArgb(0x60, 0x00, 0xAA, 0xFF), 1, dashed: true);

        // 5. 气泡落点
        foreach (var p in placements)
        {
            var color = p.Kind == PlacementKind.Badge
                ? Color.FromArgb(0xE0, 0xFF, 0x44, 0x44)
                : Color.FromArgb(0xE0, 0x00, 0xAA, 0xFF);
            DrawRect(p.Rect, ox, oy, Colors.Transparent, color, 2);
        }

        // 6. 诊断面板
        StatsText.Text =
            $"捕获 {diag.CaptureMs}ms   OCR {diag.OcrMs}ms\n" +
            $"合并 {diag.MergeMs}ms   翻译 {diag.TranslateMs}ms   布局 {diag.LayoutMs}ms\n" +
            $"行 {diag.LineCount}   段 {diag.BlockCount}   跳过 {diag.SkippedCount}\n" +
            $"翻译 {diag.TranslatedCount}   缓存命中 {diag.CacheHits}\n" +
            $"气泡 {placements.Count}（小图标 {placements.Count(p => p.Kind == PlacementKind.Badge)}）\n" +
            $"候选 {candidates.Count}";

        PositionPanels();
    }

    private void PositionPanels()
    {
        Canvas.SetLeft(StatsPanel, 12);
        Canvas.SetTop(StatsPanel, 12);

        Canvas.SetLeft(LegendPanel, 12);
        Canvas.SetTop(LegendPanel, Math.Max(12, Height - 130));
    }

    private void DrawRect(RectInt rect, int offsetX, int offsetY,
        Color fill, Color stroke, double thickness, bool dashed = false)
    {
        var shape = new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = thickness,
            Fill = fill == Colors.Transparent ? null : new SolidColorBrush(fill),
            IsHitTestVisible = false,
        };

        if (dashed)
        {
            shape.StrokeDashArray = [3, 3];
        }

        Canvas.SetLeft(shape, rect.X - offsetX);
        Canvas.SetTop(shape, rect.Y - offsetY);
        OverlayCanvas.Children.Add(shape);
    }

    public void Clear()
    {
        foreach (var shape in OverlayCanvas.Children.OfType<Shape>().ToList())
            OverlayCanvas.Children.Remove(shape);
        StatsText.Text = "";
    }
}
