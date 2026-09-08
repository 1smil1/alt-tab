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
    /// it means: leaving a stack sub view (子标签页面 → 主页面) or closing
    /// the overlay — the hook itself has no view state.</summary>
    public event Action? OverlayEscape;
    public event Action? EscapePressed;

    public uint TriggerVirtualKey { get; set; } = 0xC0; // VK_OEM_3 = backtick
    public uint TriggerScanCode { get; set; } = 0x29;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private bool _altDown;
    private bool _overlayOpen;

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

        if (vk == 0x12 || vk == 0xA4 || vk == 0xA5)
        {
            var wasDown = _altDown;
            _altDown = !isUp;
            if (isUp && _overlayOpen)
            {
                Log.Info("KeyboardHook", "alt-up commit");
                Fire(OverlayCommit);
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
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
                // Alt+数字: main row 0-9 and numpad 0-9 address slot badges.
                if (vk is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69)
                {
                    var digit = (int)(vk >= 0x60 ? vk - 0x60 : vk - 0x30);
                    Log.Info("KeyboardHook", $"alt+digit {digit}");
                    Fire(() => OverlayDigit?.Invoke(digit));
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void Fire(Action? action)
    {
        if (action is null) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
