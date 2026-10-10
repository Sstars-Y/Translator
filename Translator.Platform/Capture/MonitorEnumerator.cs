using System.Runtime.InteropServices;
using Translator.Core.Models;
using Translator.Platform.Windowing;

namespace Translator.Platform.Capture;

/// <summary>枚举显示器，处理多屏与 DPI 缩放。</summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT _, IntPtr data) =>
            {
                var mi = new NativeMethods.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                    szDevice = string.Empty,
                };

                if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    double scale = GetScaleForMonitor(hMonitor);
                    bool primary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;

                    monitors.Add(new MonitorInfo(
                        mi.szDevice ?? string.Empty,
                        RectInt.FromLtrb(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                        RectInt.FromLtrb(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                        scale,
                        primary));
                }
                return true;
            }, IntPtr.Zero);

        // 主屏排前面
        return monitors.OrderByDescending(m => m.IsPrimary).ToList();
    }

    private static double GetScaleForMonitor(IntPtr hMonitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException)
        {
            // shcore.dll 在极老系统上可能不存在
        }
        return 1.0;
    }


    /// <summary>取指定屏幕坐标点所在的显示器。</summary>
    public static MonitorInfo? MonitorsUnderPoint(int x, int y)
        => GetMonitors().FirstOrDefault(m => m.Bounds.Contains(x, y));

    /// <summary>取包含指定点的显示器。</summary>
    public static MonitorInfo? MonitorFromPoint(int x, int y)
    {
        var pt = new NativeMethods.POINT { X = x, Y = y };
        var h = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (h == IntPtr.Zero) return null;
        return GetMonitors().FirstOrDefault(m => m.Bounds.Contains(x, y));
    }
}
