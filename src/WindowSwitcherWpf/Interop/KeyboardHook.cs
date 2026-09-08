using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WindowSwitcherWpf.Services;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// WH_KEYBOARD_LL global hook. v1 uses a separate hotkey (default Alt+`)
/// so the system Alt+Tab is NOT intercepted. Trigger key is configurable
/// via <see cref="TriggerVirtualKey"/> / <see cref="TriggerScanCode"/>.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action? OverlayRequested;
    public event Action<bool>? OverlayAdvance;
    public event Action<int>? OverlayArrow;
    /// <summary>Alt+0..9 (main row + numpad) while the overlay is open:
    /// quick-jump to the slot with that badge (Alt+数字 直达标签).</summary>
    public event Action<int>? OverlayDigit;
    public event Action? OverlayCommit;
    public event Action? OverlayCancel;
    /// <summary>Esc while the overlay is open. The subscriber decides what
    /// it means: leaving a stack view (子标签页面 → 主页面) or closing
    /// the overlay — the hook swallows it either way.</summary>
    public event Action? OverlayEscape;
    public event Action? EscapePressed;
    /// <summary>一次直达 (overlay closed): quick-modifier + digit pressed.
    /// The app decides tap (=jump) vs hold (≥300ms on a堆叠 = 子标签页面)
    /// — the hook only reports press/release.</summary>
    public event Action<int>? QuickJumpDown;
    /// <summary>The quick-jump digit released (快速松开 = 跳转).</summary>
    public event Action<int>? QuickJumpUp;

    /// <summary>Alt+Tab 拦截开关 (config SuppressAltTab 解析后的最终值):
    /// 开 = Alt+Tab 永远到不了系统 (被我们的 overlay 取代).</summary>
    public bool SuppressAltTabActive { get; set; }

    /// <summary>一次直达修饰键的 virtual keys (默认 Alt 三键). Set from
    /// config QuickJumpModifier via HotkeyParser.ModifierKeys.</summary>
    public uint[] QuickModifierVirtualKeys { get; set; } = { 0x12, 0xA4, 0xA5 };

    public uint TriggerVirtualKey { get; set; } = 0xC0; // VK_OEM_3 = backtick
    public uint TriggerScanCode { get; set; } = 0x29;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private bool _altDown;
    private bool _overlayOpen;
    // 一次直达状态: quick 修饰键是否按住 + 当前按住的数字 vk (0=无)
    private bool _quickModDown;
    private uint _quickDigitVk;

    public KeyboardHook()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        var hMod = GetModuleHandleW(null);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Log.Error("KeyboardHook", $"SetWindowsHookEx failed err={err}");
        }
        else
        {
            Log.Info("KeyboardHook", "installed");
        }
    }

    public void SetOverlayOpen(bool open)
    {
        _overlayOpen = open;
        Log.Info("KeyboardHook", $"overlay open={open}");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Info("KeyboardHook", "disposed");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var isUp = (data.flags & LLKHF_UP) != 0;
        var vk = data.vkCode;
        var sc = data.scanCode;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // 修饰键跟踪: Alt (overlay 触发/提交) + quick 修饰键 (一次直达).
        // Alt 三键同时属于两个角色 — 都要更新, 缺一不可.
        if (vk == 0x12 || vk == 0xA4 || vk == 0xA5
            || Array.IndexOf(QuickModifierVirtualKeys, vk) >= 0)
        {
            if (vk == 0x12 || vk == 0xA4 || vk == 0xA5)
            {
                _altDown = !isUp;
                if (isUp && _overlayOpen)
                {
                    Log.Info("KeyboardHook", "alt-up commit");
                    Fire(OverlayCommit);
                }
            }
            if (Array.IndexOf(QuickModifierVirtualKeys, vk) >= 0)
                _quickModDown = !isUp;
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // quick 数字键松开 → 跳转 (仅当 down 是被我们吞掉的那次).
        if (isUp && _quickDigitVk != 0 && vk == _quickDigitVk)
        {
            var digit = DigitFromVk(vk);
            _quickDigitVk = 0;
            Log.Info("KeyboardHook", $"quick jump up {digit}");
            Fire(() => QuickJumpUp?.Invoke(digit));
            return new IntPtr(1);
        }

        // Alt+Tab 拦截 (SuppressAltTab): 热键本身是 Tab 时触发分支已经
        // 吞掉了; 这里补拦截的是 "热键≠Tab 但配置了拦截" 的组合.
        if (SuppressAltTabActive && _altDown && !isUp && vk == 0x09
            && TriggerVirtualKey != 0x09)
        {
            Log.Info("KeyboardHook", "suppressed system alt+tab");
            return new IntPtr(1);
        }

        if (_altDown && !isUp)
        {
            Log.Info("KeyboardHook", $"alt+key vk=0x{vk:X2} sc=0x{sc:X2}");
            if (vk == TriggerVirtualKey || data.scanCode == TriggerScanCode)
            {
                if (!_overlayOpen) { _overlayOpen = true; Log.Info("KeyboardHook", "fire OverlayRequested"); Fire(OverlayRequested); }
                else { Log.Info("KeyboardHook", $"fire OverlayAdvance reverse={shift}"); Fire(() => OverlayAdvance?.Invoke(shift)); }
                return new IntPtr(1);
            }
            if (vk == 0x1B)
            {
                if (_overlayOpen)
                {
                    // Esc first leaves a stack sub view; the app decides
                    // (overlay still open afterwards).
                    Log.Info("KeyboardHook", "fire OverlayEscape");
                    Fire(OverlayEscape);
                }
                else
                {
                    Fire(EscapePressed);
                }
                return new IntPtr(1);
            }
            if (_overlayOpen)
            {
                if (vk == 0x26 || vk == 0x28 || vk == 0x25 || vk == 0x27)
                {
                    // 2D grid navigation: the overlay maps the vk to a flat
                    // card index (left/right = ±1, up/down = ±columns).
                    var vkArrow = (int)vk;
                    Fire(() => OverlayArrow?.Invoke(vkArrow));
                    return new IntPtr(1);
                }
                // Alt+数字 (overlay open): main row 0-9 + numpad 0-9.
                if (vk is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69)
                {
                    var digit = DigitFromVk(vk);
                    Log.Info("KeyboardHook", $"alt+digit {digit}");
                    Fire(() => OverlayDigit?.Invoke(digit));
                    return new IntPtr(1);
                }
            }
        }

        // 一次直达 (overlay closed): quick+数字. 整个组合被吞掉 — 不让
        // 焦点应用收到 Alt+1 (浏览器切 tab 之类). 按住等 QuickJumpUp,
        // App 层区分 快速松开=跳转 / 按住堆叠≥300ms=子标签页面.
        if (!_overlayOpen && _quickModDown && !isUp && _quickDigitVk == 0
            && vk is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69)
        {
            var digit = DigitFromVk(vk);
            _quickDigitVk = vk;
            Log.Info("KeyboardHook", $"quick jump down {digit}");
            Fire(() => QuickJumpDown?.Invoke(digit));
            return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static int DigitFromVk(uint vk) =>
        vk >= 0x60 ? (int)(vk - 0x60) : (int)(vk - 0x30);

    private void Fire(Action? action)
    {
        if (action is null) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
