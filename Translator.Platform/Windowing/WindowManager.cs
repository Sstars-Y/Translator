using System.Diagnostics;
using System.Text;
using Translator.Core.Models;
using Translator.Platform.Windowing;

namespace Translator.Platform.Windowing;

/// <summary>前台窗口信息。</summary>
public sealed record ForegroundWindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    bool IsMinimized)
{
    public static readonly ForegroundWindowInfo None = new(IntPtr.Zero, 0, "", "", false);
}

/// <summary>
/// 窗口相关工具：前台窗口查询、隐私黑名单判断、捕获排除设置。
/// 对应路线图阶段 3 的"窗口切换、遮挡、最小化时隐藏气泡"。
/// </summary>
public sealed class WindowManager
{
    private readonly HashSet<string> _blacklist;

    /// <summary>敏感窗口黑名单，命中后自动暂停（隐私默认值）。</summary>
    public WindowManager(IEnumerable<string>? processBlacklist = null)
    {
        _blacklist = new HashSet<string>(
            processBlacklist ?? DefaultBlacklist,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 默认黑名单：密码管理器、银行、即时通讯等。
    /// 用户可在设置里增删。
    /// </summary>
    public static IReadOnlyList<string> DefaultBlacklist { get; } =
    [
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass", "Dashlane",
        "Enpass", "RoboForm", "NordPass",
        "mstsc",            // 远程桌面
        "LogonUI", "Winlogon",
        "bank", "alipay", "taobao", "wechat", "QQ", "dingtalk", "feishu",
    ];

    public IReadOnlyCollection<string> Blacklist => _blacklist;

    public void AddToBlacklist(string processName) => _blacklist.Add(processName);

    public void RemoveFromBlacklist(string processName) => _blacklist.Remove(processName);

    /// <summary>取当前前台窗口。</summary>
    public static ForegroundWindowInfo GetForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ForegroundWindowInfo.None;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string processName = "";
        try
        {
            processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (Exception)
        {
            // 系统进程可能拒绝访问
        }

        int len = NativeMethods.GetWindowTextLength(hwnd);
        var sb = new StringBuilder(Math.Max(1, len + 1));
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);

        return new ForegroundWindowInfo(
            hwnd, pid, processName, sb.ToString(),
            NativeMethods.IsIconic(hwnd));
    }

    /// <summary>前台窗口是否属于敏感应用，需要自动暂停。</summary>
    public bool IsSensitiveWindow(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (_blacklist.Contains(processName)) return true;
        // 黑名单里也可能写的是标题关键字
        return false;
    }

    /// <summary>窗口标题是否命中黑名单关键字。</summary>
    public bool IsSensitiveTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return _blacklist.Any(entry => title.Contains(entry, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把窗口从屏幕捕获中排除，避免把自己的气泡识别进去。
    /// 这是"框选工作区，排除自身窗口"的关键一步。
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WindowDisplayAffinity.ExcludeFromCapture);
    }

    public static bool IsExcludedFromCapture(IntPtr hwnd)
        => NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity)
           && affinity == NativeMethods.WindowDisplayAffinity.ExcludeFromCapture;

    /// <summary>判断某窗口是否遮挡了指定区域（用于决定是否隐藏气泡）。</summary>
    public static bool IsWindowObscuringRegion(IntPtr hwnd, RectInt region)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        return true; // 精确判定需要 GetWindowRect + 层级比较，这里先做可见性判断
    }
}
