using Translator.Core.Abstractions;
using Translator.Core.Models;

namespace Translator.Platform.Capture;

/// <summary>
/// 基于 Windows.Graphics.Capture 的捕获实现。
///
/// 关于黄色边框：WGC 默认会在被捕获窗口周围画一圈黄框。捕获整个显示器
/// （而不是某个窗口）时不会画。若确实要捕获单个窗口，需要
/// <c>GraphicsCaptureSession.IsBorderRequired = false</c>，该属性要求
/// Windows 11 22000+ 且调用方具备相应能力；否则退回 DXGI 实现。
/// </summary>
public sealed class WgcCapture : ICapture
{
    public string BackendName => "Windows.Graphics.Capture";
    public bool SupportsBorderRemoval => GraphicsCaptureSupport.CanDisableBorder;

    public Task<CapturedFrame> CaptureAsync(RectInt region, CancellationToken ct = default)
        => throw new NotImplementedException(
            "WGC 捕获实现在 Translator.Platform.Capture.WgcCaptureImpl 中，需要 WinRT 投影。");

    public IReadOnlyList<MonitorInfo> GetMonitors() => MonitorEnumerator.GetMonitors();

    public void Dispose() { }
}

/// <summary>WGC 能力探测，供后端选择使用。</summary>
public static class GraphicsCaptureSupport
{
    /// <summary>Windows 11 22000+ 才能把 IsBorderRequired 设为 false。</summary>
    public static bool CanDisableBorder
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>系统是否支持 WGC。</summary>
    public static bool IsSupported
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}
