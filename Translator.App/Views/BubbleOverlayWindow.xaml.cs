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

    /// <summary>应用一次布局结果。位置与折叠状态按文本哈希记忆，内容不变就不重排。</summary>
    public void ApplyLayout(IReadOnlyList<BubblePlacement> placements, IReadOnlyList<CoreTextBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var placement in placements)
        {
            var block = blocks.FirstOrDefault(b => b.Id == placement.BlockId);
            if (block is null) continue;

            seen.Add(placement.BlockId);

            if (_bubbles.TryGetValue(placement.BlockId, out var existing))
            {
                // 内容没变就只更新位置，不重建视觉元素（避免闪烁）
                if (existing.ContentHash == block.ContentHash)
                {
                    existing.MoveTo(placement.Rect);
                    continue;
                }
                existing.Remove(BubbleCanvas);
                _bubbles.Remove(placement.BlockId);
            }

            var visual = CreateBubble(placement, block);
            _bubbles[placement.BlockId] = visual;
        }

        // 移除已消失的气泡
        foreach (var id in _bubbles.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _bubbles[id].Remove(BubbleCanvas);
            _bubbles.Remove(id);
        }
    }

    private BubbleVisual CreateBubble(BubblePlacement placement, CoreTextBlock block)
    {
        // 记忆折叠状态
        var state = _stateMemory.TryGetValue(block.ContentHash, out var remembered)
            ? remembered
            : _settings.DefaultBubbleState;

        if (placement.Kind == PlacementKind.Badge)
            return CreateBadge(placement, block, state);

        var border = new Border
        {
            Style = (Style)FindResource(_isDark ? "BubbleStyleDark" : "BubbleStyle"),
            Opacity = _settings.BubbleOpacity,
            Cursor = Cursors.Hand,
            ToolTip = _settings.LogSourceText ? block.Text : null,
        };

        var panel = new StackPanel();

        var text = new System.Windows.Controls.TextBlock
        {
            Style = (Style)FindResource(_isDark ? "TranslationTextStyleDark" : "TranslationTextStyle"),
            FontSize = _settings.BubbleFontSize,
            Text = block.Text.Length > 0 ? GetTranslationFor(block) : "",
        };
        panel.Children.Add(text);

        border.Child = panel;

        var visual = new BubbleVisual(block.ContentHash, border, state);
        visual.ApplyState();

        // 点击切换折叠；悬停临时展开
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            visual.ToggleCollapse();
            _stateMemory[block.ContentHash] = visual.State;
        };
        border.MouseEnter += (_, _) => visual.SetHover(true);
        border.MouseLeave += (_, _) => visual.SetHover(false);

        Canvas.SetLeft(border, placement.Rect.X - (Monitor?.Bounds.X ?? 0));
        Canvas.SetTop(border, placement.Rect.Y - (Monitor?.Bounds.Y ?? 0));
        BubbleCanvas.Children.Add(border);

        return visual;
    }

    private BubbleVisual CreateBadge(BubblePlacement placement, CoreTextBlock block, BubbleState state)
    {
        // 降级形态：找不到空白时变成编号小图标
        int index = _bubbles.Count + 1;
        var border = new Border { Style = (Style)FindResource("BadgeStyle"), ToolTip = GetTranslationFor(block) };

        border.Child = new System.Windows.Controls.TextBlock
        {
            Text = index.ToString(),
            Foreground = Brushes.White,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var visual = new BubbleVisual(block.ContentHash, border, state);
        visual.ApplyState();

        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            visual.ToggleCollapse();
        };

        Canvas.SetLeft(border, placement.Rect.X - (Monitor?.Bounds.X ?? 0));
        Canvas.SetTop(border, placement.Rect.Y - (Monitor?.Bounds.Y ?? 0));
        BubbleCanvas.Children.Add(border);

        return visual;
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
    }

    /// <summary>隐藏所有气泡（滚动、切换窗口时）。</summary>
    public void HideAll()
    {
        foreach (var bubble in _bubbles.Values) bubble.SetVisible(false);
    }

    public void ShowAll()
    {
        foreach (var bubble in _bubbles.Values) bubble.SetVisible(true);
    }

    /// <summary>清空所有气泡。</summary>
    public void ClearAll()
    {
        foreach (var bubble in _bubbles.Values) bubble.Remove(BubbleCanvas);
        _bubbles.Clear();
        _translations.Clear();
    }

    public int BubbleCount => _bubbles.Count;

    /// <summary>根据设置决定浅色还是深色气泡。</summary>
    public void UpdateTheme()
    {
        if (!_settings.BubbleAutoDarkMode) { _isDark = false; return; }
        _isDark = SystemParameters.WindowGlassBrush is SolidColorBrush b
                  && (b.Color.R + b.Color.G + b.Color.B) < 384;
    }

    /// <summary>当前气泡占用的区域，供布局算法避让。</summary>
    public IReadOnlyList<RectInt> GetOccupiedRegions()
    {
        int ox = Monitor?.Bounds.X ?? 0;
        int oy = Monitor?.Bounds.Y ?? 0;
        return _bubbles.Values
            .Where(b => b.IsVisible)
            .Select(b => b.GetBounds(ox, oy))
            .ToList();
    }
}
