using System.Windows.Interop;
using Translator.Platform.Windowing;

namespace Translator.App.Interop;

/// <summary>一个全局快捷键的定义。</summary>
public sealed record HotkeyDefinition(string Id, string Gesture, Action Callback);

/// <summary>
/// 全局快捷键管理（RegisterHotKey）。
/// 注册失败说明被别的程序占用了，要把冲突告诉用户。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyDefinition> _registered = new();
    private int _nextId = 0x4000;

    /// <summary>注册冲突的快捷键：Id -> 失败原因。</summary>
    public Dictionary<string, string> Conflicts { get; } = new(StringComparer.Ordinal);

    public HotkeyManager(IntPtr windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle)
                  ?? throw new InvalidOperationException("无法获取窗口的 HwndSource。");
        _source.AddHook(WndProc);
    }

    /// <summary>注册一组快捷键，返回实际成功的 Id 集合。</summary>
    public IReadOnlyList<string> RegisterAll(IEnumerable<HotkeyDefinition> definitions)
    {
        var ok = new List<string>();
        Conflicts.Clear();

        foreach (var def in definitions)
        {
            if (string.IsNullOrWhiteSpace(def.Gesture)) continue;

            if (!TryParse(def.Gesture, out uint modifiers, out uint vk))
            {
                Conflicts[def.Id] = $"无法识别的快捷键格式：{def.Gesture}";
                continue;
            }

            int id = _nextId++;
            if (NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
            {
                _registered[id] = def;
                ok.Add(def.Id);
            }
            else
            {
                // 被别的程序占用（或本程序内重复）
                Conflicts[def.Id] = $"「{def.Gesture}」已被其他程序占用";
            }
        }

        return ok;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var def))
            {
                try { def.Callback(); }
                catch (Exception) { /* 回调异常不能打断消息循环 */ }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>解析 "Ctrl+Alt+T" 这类写法。</summary>
    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        foreach (var raw in parts[..^1])
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win": modifiers |= NativeMethods.MOD_WIN; break;
                default: return false;
            }
        }

        var keyPart = parts[^1];
        if (keyPart.Length == 1 && char.IsLetter(keyPart[0]))
        {
            vk = char.ToUpperInvariant(keyPart[0]);
            return true;
        }
        if (keyPart.Length == 1 && char.IsDigit(keyPart[0]))
        {
            vk = keyPart[0];
            return true;
        }
        if (keyPart.StartsWith('F') && int.TryParse(keyPart[1..], out int fnum) && fnum is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fnum - 1);
            return true;
        }

        vk = keyPart.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            _ => 0
        };
        return vk != 0;
    }

    public void Dispose() => UnregisterAll();
}
