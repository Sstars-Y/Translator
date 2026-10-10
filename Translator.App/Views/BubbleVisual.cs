using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Translator.Core.Configuration;
using Translator.Core.Models;

namespace Translator.App.Views;

/// <summary>
/// 一个气泡的视觉状态机。三态：展开、折叠成小点、隐藏。
/// 悬停时临时展开，移开后回到之前的状态。
/// </summary>
public sealed class BubbleVisual
{
    private readonly FrameworkElement _element;
    private readonly double _expandedWidth;
    private BubbleState _state;
    private BubbleState _stateBeforeHover;
    private bool _isHovering;

    public string ContentHash { get; }
    public BubbleState State => _state;
    public bool IsVisible => _state != BubbleState.Hidden;

    public BubbleVisual(string contentHash, FrameworkElement element, BubbleState initialState)
    {
        ContentHash = contentHash;
        _element = element;
        _state = initialState;
        _stateBeforeHover = initialState;
        _expandedWidth = double.IsNaN(element.Width) ? double.NaN : element.Width;
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

    /// <summary>按当前状态调整外观：折叠成小点时缩小并只留一个圆点。</summary>
    public void ApplyState()
    {
        switch (_state)
        {
            case BubbleState.Expanded:
                _element.Opacity = 1;
                _element.Visibility = Visibility.Visible;
                SetScale(1);
                if (_element is Border b) b.Padding = new Thickness(10, 7, 10, 7);
                break;

            case BubbleState.Collapsed:
                _element.Opacity = 0.75;
                _element.Visibility = Visibility.Visible;
                SetScale(0.42);
                break;

            case BubbleState.Hidden:
                _element.Visibility = Visibility.Hidden;
                break;
        }
    }

    private void SetScale(double scale)
    {
        if (_element.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, 1);
            _element.RenderTransform = st;
            _element.RenderTransformOrigin = new Point(0, 0);
        }

        var anim = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>取气泡在屏幕坐标下的边界，供布局避让。</summary>
    public RectInt GetBounds(int offsetX, int offsetY)
    {
        double left = Canvas.GetLeft(_element);
        double top = Canvas.GetTop(_element);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double w = _element.ActualWidth > 0 ? _element.ActualWidth : _element.Width;
        double h = _element.ActualHeight > 0 ? _element.ActualHeight : _element.Height;
        if (double.IsNaN(w) || w <= 0) w = 120;
        if (double.IsNaN(h) || h <= 0) h = 30;

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
