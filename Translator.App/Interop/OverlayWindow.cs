using System.Windows;
using System.Windows.Interop;
using Translator.Core.Models;
using Translator.Platform.Windowing;

namespace Translator.App.Interop;

/// <summary>
/// 覆盖层窗口的公共行为：
///   - 点击穿透（只有气泡本身可点）
///   - 不出现在 Alt+Tab
///   - 不抢焦点
///   - 排除屏幕捕获（关键：避免识别到自己的气泡）
/// </summary>
public static class OverlayWindow
{
    /// <summary>
    /// 把窗口配置成"不抢焦点、点击穿透、排除捕获"的覆盖层。
    /// </summary>
    /// <param name="clickThrough">
    /// 是否让整个窗口点击穿透。气泡层要设 true，然后对气泡本身
    /// 单独打开命中测试（用 WPF 的 IsHitTestVisible + 自定义命中区域）。
    /// </param>
    public static void Configure(Window window, bool clickThrough = true, bool excludeFromCapture = true)
    {
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.AllowsTransparency = true;
        window.Background = System.Windows.Media.Brushes.Transparent;
        window.Topmost = true;
        window.ShowActivated = false; // 不抢焦点

        var helper = new WindowInteropHelper(window);
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            // 扩展样式：分层 + 工具窗口（不进 Alt+Tab）+ 不激活
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_LAYERED;
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            if (clickThrough) exStyle |= NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);

            if (excludeFromCapture)
            {
                // 让捕获 API 看不到这个窗口
                bool ok = NativeMethods.SetWindowDisplayAffinity(
                    hwnd, NativeMethods.WindowDisplayAffinity.ExcludeFromCapture);

                // 老系统不支持时退回 Monitor 模式（只在录制时可见）
                if (!ok)
                {
                    NativeMethods.SetWindowDisplayAffinity(
                        hwnd, NativeMethods.WindowDisplayAffinity.Monitor);
                }
            }
        };
    }

    /// <summary>动态开关整个窗口的点击穿透。</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if (enabled) exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        else exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    /// <summary>把窗口铺满指定显示器的工作区。</summary>
    public static void CoverMonitor(Window window, MonitorInfo monitor)
    {
        // 用物理像素定位，避免 DPI 缩放导致偏移
        var source = PresentationSource.FromVisual(window);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        window.Left = monitor.Bounds.X / scaleX;
        window.Top = monitor.Bounds.Y / scaleY;
        window.Width = monitor.Bounds.Width / scaleX;
        window.Height = monitor.Bounds.Height / scaleY;
    }

    /// <summary>判断窗口当前是否已排除捕获。</summary>
    public static bool IsExcludedFromCapture(Window window)
        => WindowManager.IsExcludedFromCapture(new WindowInteropHelper(window).Handle);
}
