using System.Windows;
using Translator.App.Interop;
using Translator.Core.Models;
using Translator.Platform.Capture;

namespace Translator.App.Views;

/// <summary>
/// 选区指示框：把当前翻译范围画在屏幕上，让人知道正在翻哪一块。
/// 点击穿透、不抢焦点，并从捕获里排除——否则框线自己会被 OCR 当成截图内容。
/// </summary>
public partial class SelectionFrameWindow : Window
{
    /// <summary>范围太小时框会细到看不见，给一个下限。</summary>
    private const double MinSize = 20;

    private RectInt _region;

    public SelectionFrameWindow()
    {
        InitializeComponent();
        OverlayWindow.Configure(this, clickThrough: true, excludeFromCapture: true);
    }

    /// <summary>是否已经记住了一个范围（暂停后恢复要用）。</summary>
    public bool HasRegion => _region.Width > 0 && _region.Height > 0;

    /// <summary>显示（或就地更新）指示框。region 是物理像素坐标。</summary>
    public void ShowFor(RectInt region)
    {
        if (region.Width <= 0 || region.Height <= 0) return;

        _region = region;

        var monitor = MonitorEnumerator.MonitorsUnderPoint(region.X, region.Y)
            ?? MonitorEnumerator.GetMonitors().FirstOrDefault();
        double scale = monitor?.DpiScale > 0 ? monitor.DpiScale : 1.0;

        Left = region.X / scale;
        Top = region.Y / scale;
        Width = Math.Max(MinSize, region.Width / scale);
        Height = Math.Max(MinSize, region.Height / scale);

        LabelText.Text = $"翻译范围 {region.Width}×{region.Height}";

        if (!IsVisible) Show();
    }

    /// <summary>收起指示框（范围还记着，恢复时原样画回来）。</summary>
    public void HideFrame()
    {
        if (IsVisible) Hide();
    }
}
