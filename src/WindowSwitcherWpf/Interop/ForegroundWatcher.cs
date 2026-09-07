using System;
using System.Runtime.InteropServices;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// SetWinEventHook listener for the system foreground window. Optional hook
/// for the black-list feature: a set of exe names whose focus should suppress
/// the switcher. MVP wires the API but always returns an empty set.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private IntPtr _lastForeground = IntPtr.Zero;

    public event Action<IntPtr>? ForegroundChanged;

    public ForegroundWatcher()
    {
        _proc = OnWinEvent;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || hwnd == _lastForeground) return;
        _lastForeground = hwnd;
        ForegroundChanged?.Invoke(hwnd);
    }
}
