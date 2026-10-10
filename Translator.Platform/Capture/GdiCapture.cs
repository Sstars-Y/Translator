using System.Runtime.InteropServices;
using Translator.Core.Abstractions;
using Translator.Core.Models;
using Translator.Platform.Windowing;

namespace Translator.Platform.Capture;

/// <summary>
/// 基于 GDI BitBlt 的捕获实现。
///
/// 为什么先用它而不是 WGC：WGC 会在被捕获的窗口周围画黄色边框，要去掉需要
/// Win11 22000+ 与 MSIX 能力声明。BitBlt 没有这个问题，且在 Win10/11 上都可用，
/// 配合 <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> 就能排除自身窗口。
/// 代价是性能略低、独占全屏下拿不到内容（本来也不支持）。
/// </summary>
public sealed class GdiCapture : ICapture
{
    public string BackendName => "GDI BitBlt";
    public bool SupportsBorderRemoval => true; // 本来就没有边框

    public Task<CapturedFrame> CaptureAsync(RectInt region, CancellationToken ct = default)
        => Task.Run(() => Capture(region), ct);

    public IReadOnlyList<MonitorInfo> GetMonitors() => MonitorEnumerator.GetMonitors();

    private static CapturedFrame Capture(RectInt region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException($"捕获区域无效: {region}");

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = Gdi32.GetDC(IntPtr.Zero);
            memDc = Gdi32.CreateCompatibleDC(screenDc);
            hBitmap = Gdi32.CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            oldBitmap = Gdi32.SelectObject(memDc, hBitmap);

            // CAPTUREBLT 让分层窗口（部分悬浮层）也能被抓到；
            // 我们自己的窗口已用 WDA_EXCLUDEFROMCAPTURE 排除，不会被抓进来。
            const int SRCCOPY = 0x00CC0020;
            const int CAPTUREBLT = 0x40000000;

            if (!Gdi32.BitBlt(memDc, 0, 0, region.Width, region.Height,
                    screenDc, region.X, region.Y, SRCCOPY | CAPTUREBLT))
            {
                throw new InvalidOperationException("BitBlt 失败，可能捕获区域无效或桌面被锁定。");
            }

            var info = new Gdi32.BITMAPINFO
            {
                bmiHeader = new Gdi32.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height, // 负高度 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                }
            };

            int stride = region.Width * 4;
            var pixels = new byte[stride * region.Height];
            int scanned = Gdi32.GetDIBits(memDc, hBitmap, 0, (uint)region.Height,
                pixels, ref info, 0);

            if (scanned == 0)
                throw new InvalidOperationException("GetDIBits 失败。");

            return new CapturedFrame
            {
                Pixels = pixels,
                Width = region.Width,
                Height = region.Height,
                Stride = stride,
                SourceBounds = region,
                Timestamp = DateTimeOffset.Now,
            };
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero) Gdi32.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) Gdi32.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) Gdi32.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Gdi32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose() { }
}

internal static partial class Gdi32
{
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public uint[] bmiColors;
    }
}
