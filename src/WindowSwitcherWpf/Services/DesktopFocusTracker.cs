using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using WindowSwitcherWpf.Interop;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// 记住每个虚拟桌面上"最后聚焦过的窗口" — 切桌面后把它拉回前台,
/// 免得切过去还要再点一次 (上游 dankrusi 的 restorePreviousWindowFocus
/// 同款; choyy 走的是"激活目标桌面的顶层窗口").
///
/// 记录方式: WinEvent 钩子 (EVENT_SYSTEM_FOREGROUND) — 前台窗口一变
/// 就记到**当前桌面**名下. 键 = 桌面 GUID (序号会因删桌面漂移, GUID 不会).
///
/// 记录过滤: 只记"像应用窗口"的 hwnd (可见 + 有标题 + 不是我们自己的
/// 进程) — 否则切桌面瞬间的临时前景 (Progman / 我们的 overlay) 会把
/// 好记录覆盖成垃圾.
/// </summary>
public sealed class DesktopFocusTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint OBJID_WINDOW = 0;

    private readonly Dictionary<Guid, IntPtr> _lastFocus = new();
    private WinEventProc? _proc;
    private IntPtr _hook;
    private readonly uint _ownPid;

    public DesktopFocusTracker()
    {
        _ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }

    /// <summary>装钩子 (必须在 UI 线程调 — 回调也跑在 UI 线程).</summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnForegroundChanged;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Log.Info("DesktopFocus", $"win-event hook installed (hwnd=0x{_hook.ToInt64():X})");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }

    /// <summary>该桌面上次聚焦的窗口 (没有 = 空).</summary>
    public IntPtr LastFocusOf(Guid desktopId) =>
        desktopId != Guid.Empty && _lastFocus.TryGetValue(desktopId, out var h)
            ? h : IntPtr.Zero;

    /// <summary>手动记一笔 (切走前记当前前台窗口).</summary>
    public void Remember(Guid desktopId, IntPtr hwnd)
    {
        if (desktopId == Guid.Empty || hwnd == IntPtr.Zero) return;
        if (!IsRecordable(hwnd)) return;
        _lastFocus[desktopId] = hwnd;
    }

    /// <summary>切到某桌面后恢复它上次聚焦的窗口. 返回是否真的激活了.</summary>
    public bool RestoreFocus(Guid desktopId)
    {
        var hwnd = LastFocusOf(desktopId);
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindow(hwnd)) return false;
        if (!IsRecordable(hwnd)) return false;
        // 窗口可能被搬到别的桌面了 — 只有还在本桌面才激活.
        var current = VirtualDesktop.CurrentGuid;
        if (current != Guid.Empty)
        {
            var id = VirtualDesktop.DesktopIdOfWindow(hwnd);
            if (id != Guid.Empty && id != current) return false;
        }
        Log.Info("DesktopFocus", $"restore focus 0x{hwnd.ToInt64():X}");
        return ActivationService.Activate(hwnd);
    }

    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW) return;
            var guid = VirtualDesktop.CurrentGuid;
            if (guid == Guid.Empty) return;
            Remember(guid, hwnd);
        }
        catch (Exception ex)
        {
            Log.Exception("DesktopFocus", ex);
        }
    }

    /// <summary>只记"像应用窗口"的 hwnd.</summary>
    private bool IsRecordable(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        if (!IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return false;   // 我们自己的 overlay
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.Length > 0;
    }

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess,
        uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
