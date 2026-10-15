using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Translator.App.Interop;
using Translator.Core.Configuration;
using Translator.Core.Layout;
using Translator.Core.Models;
using CoreTextBlock = Translator.Core.Models.TextBlock;

namespace Translator.App.Views;

/// <summary>
/// 气泡层窗口：全屏透明、点击穿透、排除屏幕捕获。
///
/// 交互规则（产品文档）：
///   - 气泡可展开、折叠成小点、隐藏
///   - 悬停临时展开，点击固定
///   - 可以全部折叠
/// </summary>
public partial class BubbleOverlayWindow : Window
{
    private readonly Dictionary<string, BubbleVisual> _bubbles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BubbleState> _stateMemory = new(StringComparer.Ordinal);
    private AppSettings _settings = new();
    private bool _isDark;

    public BubbleOverlayWindow()
    {
        InitializeComponent();
        OverlayWindow.Configure(this, clickThrough: true, excludeFromCapture: true);
    }

    /// <summary>当前显示的显示器。</summary>
    public MonitorInfo? Monitor { get; private set; }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        UpdateTheme();
    }

    /// <summary>把窗口铺满指定显示器。</summary>
    public void AttachToMonitor(MonitorInfo monitor)
    {
        Monitor = monitor;
        OverlayWindow.CoverMonitor(this, monitor);
    }

    /// <summary>物理像素 → 画布 DIP 的缩放比。窗口未上屏时退回显示器 DPI。</summary>
    private double DeviceScale
    {
        get
        {
            var source = PresentationSource.FromVisual(this);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 0;
            if (scale <= 0) scale = Monitor?.DpiScale > 0 ? Monitor.DpiScale : 1.0;
            return scale;
        }
    }

    /// <summary>
    /// 屏幕绝对物理像素 → 画布坐标（DIP，原点为该显示器左上角）。
    ///
    /// 流水线给出的落点是物理像素（因为捕获、OCR 都在物理像素里），
    /// 而 Canvas 用的是 DIP：不除缩放比的话，150% 缩放下气泡会跑到 1.5 倍远的地方。
    /// </summary>
    private RectInt ToCanvas(RectInt physical)
    {
        double scale = DeviceScale;
        int ox = Monitor?.Bounds.X ?? 0;
        int oy = Monitor?.Bounds.Y ?? 0;

        return new RectInt(
            (int)Math.Round((physical.X - ox) / scale),
            (int)Math.Round((physical.Y - oy) / scale),
            Math.Max(1, (int)Math.Round(physical.Width / scale)),
            Math.Max(1, (int)Math.Round(physical.Height / scale)));
    }

    /// <summary>应用一次布局结果。位置与折叠状态按文本哈希记忆，内容不变就不重排。</summary>
    public void ApplyLayout(IReadOnlyList<BubblePlacement> placements, IReadOnlyList<CoreTextBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _sources.Clear();

        foreach (var placement in placements)
        {
            var block = blocks.FirstOrDefault(b => b.Id == placement.BlockId);
            if (block is null) continue;

            seen.Add(placement.BlockId);

            // 落点是屏幕物理像素，画布用 DIP，这里换算一次
            var canvasRect = ToCanvas(placement.Rect);

            // 记下原文位置（画布坐标）：气泡按真实尺寸放不下时要挪到原文下方/上方
            _sources[block.ContentHash] = ToCanvas(block.Bounds);

            if (_bubbles.TryGetValue(placement.BlockId, out var existing))
            {
                // 内容没变就只更新位置，不重建视觉元素（避免闪烁）
                if (existing.ContentHash == block.ContentHash)
                {
                    existing.MoveTo(canvasRect);
                    continue;
                }
                existing.Remove(BubbleCanvas);
                _bubbles.Remove(placement.BlockId);
            }

            var visual = CreateBubble(placement, block, canvasRect);
            _bubbles[placement.BlockId] = visual;
        }

        // 移除已消失的气泡
        foreach (var id in _bubbles.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _bubbles[id].Remove(BubbleCanvas);
            _bubbles.Remove(id);
        }

        RefreshInputRegion();
    }

    private bool _inputRegionScheduled;

    /// <summary>
    /// 只让气泡本体接收鼠标：把窗口区域收缩到可见气泡的并集，其余部分保持穿透。
    /// 没有气泡时恢复整窗穿透，桌面照常可点。
    /// 布局刚变过时尺寸还没算出来，所以推迟到布局之后再取。
    /// </summary>
    private void RefreshInputRegion()
    {
        if (_inputRegionScheduled) return;
        _inputRegionScheduled = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _inputRegionScheduled = false;

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null) return;

            var matrix = source.CompositionTarget.TransformToDevice;
            var rects = new List<RectInt>();

            foreach (var bubble in _bubbles.Values)
            {
                if (!bubble.IsVisible || !bubble.IsElementVisible) continue;

                var b = bubble.GetBounds(0, 0);
                var topLeft = matrix.Transform(new Point(b.X, b.Y));
                var bottomRight = matrix.Transform(new Point(b.X + b.Width, b.Y + b.Height));

                rects.Add(new RectInt(
                    (int)Math.Floor(topLeft.X),
                    (int)Math.Floor(topLeft.Y),
                    (int)Math.Ceiling(bottomRight.X - topLeft.X),
                    (int)Math.Ceiling(bottomRight.Y - topLeft.Y)));
            }

            OverlayWindow.SetInputRegion(this, rects);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 取窗口级样式。找不到时返回 null，让气泡退化成无样式而不是抛异常——
    /// 一个样式缺失不该让整条翻译流水线失败。
    /// </summary>
    private Style? FindStyle(string key) => TryFindResource(key) as Style;

    private BubbleVisual CreateBubble(BubblePlacement placement, CoreTextBlock block, RectInt canvasRect)
    {
        // 记忆折叠状态
        var state = _stateMemory.TryGetValue(block.ContentHash, out var remembered)
            ? remembered
            : _settings.DefaultBubbleState;

        if (placement.Kind == PlacementKind.Badge)
            return CreateBadge(block, state, canvasRect);

        var border = new Border
        {
            Style = FindStyle(_isDark ? "BubbleStyleDark" : "BubbleStyle"),
            Opacity = _settings.BubbleOpacity,
            Cursor = Cursors.Hand,
            ToolTip = _settings.LogSourceText ? block.Text : null,
        };

        var visual = NewVisual(border, block, state);
        visual.ApplyState();
        WireBubble(border, visual, block);
        Place(border, canvasRect);
        ScheduleClamp(border, visual);

        return visual;
    }

    private BubbleVisual CreateBadge(CoreTextBlock block, BubbleState state, RectInt canvasRect)
    {
        // 降级形态：找不到空白时收成编号小图标（悬停或点击仍能展开看译文）
        var border = new Border
        {
            Style = FindStyle("BadgeStyle"),
            ToolTip = GetTranslationFor(block),
        };

        var visual = NewVisual(border, block, state);
        visual.ApplyState();
        WireBubble(border, visual, block);
        Place(border, canvasRect);
        ScheduleClamp(border, visual);

        return visual;
    }

    /// <summary>
    /// 尺寸出来之后（以及折叠/展开改变尺寸之后）按真实渲染尺寸校正位置，
    /// 保证译文再长也不会显示到屏幕外面去。
    /// </summary>
    private void ScheduleClamp(Border border, BubbleVisual visual)
    {
        border.SizeChanged += (_, _) => ClampBubble(visual);
        Dispatcher.BeginInvoke(new Action(() => ClampBubble(visual)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 按真实尺寸把气泡收进显示器范围。
    ///
    /// 布局用的估算尺寸不一定准：中文译文常常比原文窄，也可能比估算宽。
    /// 放不下时优先挪到原文下方/上方（不遮原文），最后才贴屏幕边。
    /// 位置每次都从布局落点重算，所以折叠/展开来回切换不会越飘越远。
    /// </summary>
    private void ClampBubble(BubbleVisual visual)
    {
        if (Monitor is null) return;

        double scale = DeviceScale;
        if (scale <= 0) return;

        var actual = visual.GetCanvasRect();
        if (actual.Width <= 0 || actual.Height <= 0) return;

        // 窗口铺满显示器，所以画布范围就是显示器尺寸（DIP）
        double maxRight = Monitor.Bounds.Width / scale;
        double maxBottom = Monitor.Bounds.Height / scale;

        double x = actual.X;
        double y = actual.Y;
        bool overflow = x + actual.Width > maxRight || y + actual.Height > maxBottom || x < 0 || y < 0;
        if (!overflow) return;

        if (_sources.TryGetValue(visual.ContentHash, out var source))
        {
            double gap = 8;
            double below = source.Y + source.Height + gap;
            double above = source.Y - gap - actual.Height;

            if (source.X + actual.Width <= maxRight && below + actual.Height <= maxBottom)
            {
                x = source.X;
                y = below;
            }
            else if (source.X + actual.Width <= maxRight && above >= 0)
            {
                x = source.X;
                y = above;
            }
            else
            {
                x = Math.Max(0, Math.Min(x, maxRight - actual.Width));
                y = Math.Max(0, Math.Min(y, maxBottom - actual.Height));
            }
        }
        else
        {
            x = Math.Max(0, Math.Min(x, maxRight - actual.Width));
            y = Math.Max(0, Math.Min(y, maxBottom - actual.Height));
        }

        visual.MoveTo(new RectInt((int)Math.Round(x), (int)Math.Round(y), actual.Width, actual.Height));
        RefreshInputRegion();
    }

    /// <summary>内容哈希 → 原文的画布矩形，供气泡溢出时重新选位置。</summary>
    private readonly Dictionary<string, RectInt> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// 组装一个气泡的两种形态：展开态是译文（BubbleStyle），折叠态是编号小圆点（BadgeStyle）。
    /// 样式和内容由 BubbleVisual 在状态切换时挂上去，折叠不再是把整块缩小。
    /// </summary>
    private BubbleVisual NewVisual(Border border, CoreTextBlock block, BubbleState state)
    {
        var panel = new StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Style = FindStyle(_isDark ? "TranslationTextStyleDark" : "TranslationTextStyle"),
            FontSize = _settings.BubbleFontSize,
            Text = block.Text.Length > 0 ? GetTranslationFor(block) : "",
        });

        int index = _bubbles.Count + 1;

        return new BubbleVisual(
            block.ContentHash,
            border,
            panel,
            CreateDot(index),
            FindStyle(_isDark ? "BubbleStyleDark" : "BubbleStyle"),
            FindStyle("BadgeStyle"),
            _settings.BubbleOpacity,
            state);
    }

    /// <summary>折叠后显示的编号小圆点内容。</summary>
    private static System.Windows.Controls.TextBlock CreateDot(int index) => new()
    {
        Text = index.ToString(),
        Foreground = Brushes.White,
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>点击切换折叠、悬停临时展开，并记住用户的选择。</summary>
    private void WireBubble(Border border, BubbleVisual visual, CoreTextBlock block)
    {
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            visual.ToggleCollapse();
            _stateMemory[block.ContentHash] = visual.State;
            RefreshInputRegion();
        };
        border.MouseEnter += (_, _) =>
        {
            visual.SetHover(true);
            RefreshInputRegion();
        };
        border.MouseLeave += (_, _) =>
        {
            visual.SetHover(false);
            RefreshInputRegion();
        };
    }

    private void Place(Border border, RectInt canvasRect)
    {
        // canvasRect 已经是画布 DIP 坐标，这里只负责放上去，位置由 MoveTo 做动画
        Canvas.SetLeft(border, canvasRect.X);
        Canvas.SetTop(border, canvasRect.Y);
        BubbleCanvas.Children.Add(border);
    }

    /// <summary>译文由外部注入（布局时已翻译好）。这里用附加属性保存。</summary>
    private readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);

    private string GetTranslationFor(CoreTextBlock block)
        => _translations.TryGetValue(block.Id, out var t) ? t : block.Text;

    /// <summary>设置某段文本的译文。</summary>
    public void SetTranslation(string blockId, string translated)
        => _translations[blockId] = translated;

    /// <summary>全部折叠 / 展开。</summary>
    public void ToggleCollapseAll()
    {
        bool anyExpanded = _bubbles.Values.Any(b => b.State == BubbleState.Expanded);
        foreach (var bubble in _bubbles.Values)
        {
            bubble.SetState(anyExpanded ? BubbleState.Collapsed : BubbleState.Expanded);
            _stateMemory[bubble.ContentHash] = bubble.State;
        }
        RefreshInputRegion();
    }

    /// <summary>隐藏所有气泡（滚动、切换窗口时）。</summary>
    public void HideAll()
    {
        foreach (var bubble in _bubbles.Values) bubble.SetVisible(false);
        RefreshInputRegion();
    }

    public void ShowAll()
    {
        foreach (var bubble in _bubbles.Values) bubble.SetVisible(true);
        RefreshInputRegion();
    }

    /// <summary>清空所有气泡。</summary>
    public void ClearAll()
    {
        foreach (var bubble in _bubbles.Values) bubble.Remove(BubbleCanvas);
        _bubbles.Clear();
        _translations.Clear();
        RefreshInputRegion();
    }

    public int BubbleCount => _bubbles.Count;

    /// <summary>根据设置决定浅色还是深色气泡。</summary>
    public void UpdateTheme()
    {
        if (!_settings.BubbleAutoDarkMode) { _isDark = false; return; }
        _isDark = SystemParameters.WindowGlassBrush is SolidColorBrush b
                  && (b.Color.R + b.Color.G + b.Color.B) < 384;
    }

    /// <summary>
    /// 当前气泡占用的区域，供布局算法避让。
    /// 布局在屏幕物理像素里算，所以这里要把画布 DIP 换算回绝对物理像素。
    /// </summary>
    public IReadOnlyList<RectInt> GetOccupiedRegions()
    {
        double scale = DeviceScale;
        int ox = Monitor?.Bounds.X ?? 0;
        int oy = Monitor?.Bounds.Y ?? 0;

        return _bubbles.Values
            .Where(b => b.IsVisible && b.IsElementVisible)
            .Select(b => b.GetBounds(0, 0))
            .Select(b => new RectInt(
                ox + (int)Math.Round(b.X * scale),
                oy + (int)Math.Round(b.Y * scale),
                Math.Max(1, (int)Math.Round(b.Width * scale)),
                Math.Max(1, (int)Math.Round(b.Height * scale))))
            .ToList();
    }
}
