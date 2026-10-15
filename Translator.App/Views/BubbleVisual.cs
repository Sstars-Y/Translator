using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Translator.Core.Configuration;
using Translator.Core.Models;

namespace Translator.App.Views;

/// <summary>
/// 一个气泡的视觉状态机。三态：展开（完整译文）、折叠（只留一个带编号的小圆点）、隐藏。
/// 折叠不再整体缩小，而是把尺寸收缩到 <see cref="CollapsedSize"/> 并把内容换成编号小点，
/// 这样"看起来是圆点"和"命中测试/布局避让按圆点算"是一致的。
/// 悬停时临时展开，移开后回到之前的状态。
/// </summary>
public sealed class BubbleVisual
{
    /// <summary>折叠后小圆点的边长（DIP）。</summary>
    public const double CollapsedSize = 18;

    private readonly Border _element;
    private readonly FrameworkElement _expandedContent;
    private readonly FrameworkElement _collapsedContent;
    private readonly Style? _expandedStyle;
    private readonly Style? _collapsedStyle;
    private readonly double _expandedOpacity;

    private BubbleState _state;
    private BubbleState _stateBeforeHover;
    private bool _isHovering;

    public string ContentHash { get; }
    public BubbleState State => _state;
    public bool IsVisible => _state != BubbleState.Hidden;

    /// <summary>
    /// 元素此刻是否真的画在屏幕上。暂停时整体隐藏但状态记忆不变，
    /// 命中区域必须按"实际可见"算，否则透明区域仍会吃掉鼠标消息。
    /// </summary>
    public bool IsElementVisible => _element.Visibility == Visibility.Visible;

    public BubbleVisual(
        string contentHash,
        Border element,
        FrameworkElement expandedContent,
        FrameworkElement collapsedContent,
        Style? expandedStyle,
        Style? collapsedStyle,
        double expandedOpacity,
        BubbleState initialState)
    {
        ContentHash = contentHash;
        _element = element;
        _expandedContent = expandedContent;
        _collapsedContent = collapsedContent;
        _expandedStyle = expandedStyle;
        _collapsedStyle = collapsedStyle;
        _expandedOpacity = expandedOpacity;
        _state = initialState;
        _stateBeforeHover = initialState;
    }

    /// <summary>移动到新位置。内容不变时只挪位置，不重建元素。</summary>
    public void MoveTo(RectInt rect)
    {
        double left = Canvas.GetLeft(_element);
        double top = Canvas.GetTop(_element);

        if (Math.Abs(left - rect.X) < 0.5 && Math.Abs(top - rect.Y) < 0.5) return;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);

        _element.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation(rect.X, duration) { EasingFunction = ease });
        _element.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(rect.Y, duration) { EasingFunction = ease });
    }

    public void ToggleCollapse()
    {
        SetState(_state == BubbleState.Expanded ? BubbleState.Collapsed : BubbleState.Expanded);
    }

    /// <summary>
    /// 当前画布矩形（DIP：左上角 + 实际渲染尺寸）。
    /// 布局是按估算尺寸落点的，实际渲染出来可能更宽，所以需要按真实尺寸再校正一次。
    /// </summary>
    public RectInt GetCanvasRect()
    {
        double x = Canvas.GetLeft(_element);
        double y = Canvas.GetTop(_element);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;

        return new RectInt(
            (int)Math.Round(x),
            (int)Math.Round(y),
            Math.Max(0, (int)Math.Round(_element.ActualWidth)),
            Math.Max(0, (int)Math.Round(_element.ActualHeight)));
    }

    public void SetState(BubbleState state)
    {
        _state = state;
        ApplyState();
    }

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            _element.Visibility = Visibility.Visible;
            ApplyState();
        }
        else
        {
            _element.Visibility = Visibility.Hidden;
        }
    }

    public void SetHover(bool hovering)
    {
        if (_isHovering == hovering) return;
        _isHovering = hovering;

        if (hovering)
        {
            _stateBeforeHover = _state;
            // 悬停时临时展开
            if (_state == BubbleState.Collapsed) SetState(BubbleState.Expanded);
        }
        else
        {
            // 移开后回到悬停前的状态
            if (_state == BubbleState.Expanded && _stateBeforeHover == BubbleState.Collapsed)
                SetState(BubbleState.Collapsed);
        }
    }

    /// <summary>
    /// 按当前状态切换"外壳 + 内容"：展开态用气泡样式装译文，折叠态用徽章样式装编号小点。
    /// 尺寸与内边距都由样式给（气泡样式 10,7；徽章样式 18x18），
    /// 这里只把折叠尺寸显式写死，展开时再用 NaN 交还给自动尺寸。
    /// </summary>
    public void ApplyState()
    {
        switch (_state)
        {
            case BubbleState.Expanded:
                _element.Style = _expandedStyle;
                _element.Child = _expandedContent;
                _element.Width = double.NaN;
                _element.Height = double.NaN;
                _element.Opacity = _expandedOpacity;
                _element.Visibility = Visibility.Visible;
                break;

            case BubbleState.Collapsed:
                _element.Style = _collapsedStyle;
                _element.Child = _collapsedContent;
                _element.Width = CollapsedSize;
                _element.Height = CollapsedSize;
                // 小点本身要看得清，不能被用户调低的气泡透明度弄没
                _element.Opacity = Math.Max(0.8, _expandedOpacity);
                _element.Visibility = Visibility.Visible;
                break;

            case BubbleState.Hidden:
                _element.Visibility = Visibility.Hidden;
                break;
        }
    }

    /// <summary>取气泡在屏幕坐标下的边界，供布局避让。折叠时按小圆点算。</summary>
    public RectInt GetBounds(int offsetX, int offsetY)
    {
        double left = Canvas.GetLeft(_element);
        double top = Canvas.GetTop(_element);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double w;
        double h;

        if (_state == BubbleState.Collapsed)
        {
            w = CollapsedSize;
            h = CollapsedSize;
        }
        else
        {
            w = _element.ActualWidth > 0 ? _element.ActualWidth : _element.Width;
            h = _element.ActualHeight > 0 ? _element.ActualHeight : _element.Height;
            if (double.IsNaN(w) || w <= 0) w = 120;
            if (double.IsNaN(h) || h <= 0) h = 30;
        }

        return new RectInt(
            (int)Math.Round(left) + offsetX,
            (int)Math.Round(top) + offsetY,
            (int)Math.Round(w),
            (int)Math.Round(h));
    }

    public void Remove(Canvas canvas)
    {
        _element.BeginAnimation(Canvas.LeftProperty, null);
        _element.BeginAnimation(Canvas.TopProperty, null);
        canvas.Children.Remove(_element);
    }
}
