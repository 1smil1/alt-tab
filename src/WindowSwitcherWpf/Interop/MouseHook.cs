using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WindowSwitcherWpf.Services;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// WH_MOUSE_LL global hook on a DEDICATED pump thread — same shape as
/// <see cref="KeyboardHook"/> so the LL callback microseconds-return and
/// UI-thread GC pressure never starves it. **Only** LButton/RButton/MButton
/// DOWN are inspected; every other message is forwarded untouched — no
/// mousemove traffic, so the overhead is negligible.
///
/// 为什么需要它：SwitcherOverlay 用 ShowActivated="False" + Focusable="False"
/// 永远不会 Activated, 因此 WPF Window.Deactivated 永远不触发. 用户在第二
/// 热键 (Alt+`) 弹出的 persistent overlay 上, 点 overlay 矩形外的屏幕 → 我们
/// 的应用收不到任何 WPF 鼠标事件 (目标窗口的进程才收到 click), 必须用全局
/// hook 截获. 主热键 (Alt+Tab) 路径下也能用, Alt+Tab 通常松开 Alt 就 commit,
/// click-outside 的体验自然; 这里两条路径都覆盖.
///
/// SetOwnerHwnd / ClearOwnerHwnd 由 overlay 在 Show/Hide 时调用: 仅在开着时
/// 才检查 owner rect; hook 始终安装避免热路径上 SetWindowsHookEx 的潜在延迟.
/// </summary>
public sealed class MouseHook : IDisposable
{
    /// <summary>Mouse down landed in the unowned screen area while the
    /// overlay is open. App decides whether to cancel (recommended for
    /// 主热键路径会 commit, 第二热键路径会 cancel).</summary>
    public event Action? ClickOutsideOverlay;

    /// <summary>Current overlay window for hit-test. Updated by overlay
    /// Show/Hide. Null = no overlay armed, all clicks are passed through.</summary>
    private volatile IntPtr _ownerHwnd = IntPtr.Zero;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private ManualResetEventSlim? _ready;
    private volatile bool _pumpAlive;
    private volatile bool _stopRequested;

    public MouseHook()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_pumpThread is not null) return;
        _stopRequested = false;
        _ready = new ManualResetEventSlim();
        _pumpThread = new Thread(HookThreadPump)
        {
            IsBackground = true,
            Name = "MouseHookPump",
        };
        _pumpThread.Start();
        _ready.Wait(2000);
    }

    /// <summary>Overlay opened with hwnd — click-outside armed.</summary>
    public void SetOwnerHwnd(IntPtr hwnd)
    {
        _ownerHwnd = hwnd;
        Log.Info("MouseHook", $"owner=0x{hwnd.ToInt64():X}");
    }

    /// <summary>Overlay closed — click-outside disarmed.</summary>
    public void ClearOwnerHwnd()
    {
        _ownerHwnd = IntPtr.Zero;
        Log.Info("MouseHook", "owner cleared");
    }

    private void HookThreadPump()
    {
        // 代际隔离 — 同 KeyboardHook: 只卸自己的局部 hook, 且只有仍是当前代
        // 才写共享状态. 否则 Dispose→Install 交接时旧线程把新 hook 卸掉.
        var me = Thread.CurrentThread;
        IntPtr myHook = IntPtr.Zero;
        try
        {
            _pumpThreadId = GetCurrentThreadId();
            var hMod = GetModuleHandleW(null);
            myHook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            if (myHook == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Log.Error("MouseHook", $"SetWindowsHookEx failed err={err}");
                _ready!.Set();
                return;
            }
            _hook = myHook;
            Log.Info("MouseHook", "installed (dedicated pump thread)");
            // 先标 alive 再放行 Install() 的 _ready.Wait — 否则看守者第一拍
            // 可能读到 false → 误判死.
            _pumpAlive = true;
            _ready!.Set();
            while (!_stopRequested)
            {
                if (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    _pumpAlive = true; // 每次返回 GetMessageW 都 mark alive — App 的 watchdog 周期性读它确认 pump 还活着.
                    DispatchMessageW(ref msg);
                }
                else
                {
                    Log.Warn("MouseHook",
                        $"GetMessageW<=0 stop={_stopRequested} — exiting pump");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception("MouseHook.pump", ex);
        }
        finally
        {
            if (ReferenceEquals(_pumpThread, me))
            {
                _pumpAlive = false; // 让 watchdog 立刻看见死亡
                _hook = IntPtr.Zero;
            }
            try { if (myHook != IntPtr.Zero) UnhookWindowsHookEx(myHook); } catch { }
            Log.Info("MouseHook", "pump exited, hook removed");
        }
    }

    /// <summary>App watchdog 周期性读 — 如果 pump 线程死了 (GetMessageW
    /// 抛异常 / SetWindowsHookEx 被 OS 强制 Unhook), _pumpAlive 会停在
    /// 最后一次循环体, 时间戳就停在那里. 比只看 tasklist 死活更细粒度.</summary>
    public bool IsPumpAlive => _pumpAlive;

    public void Dispose()
    {
        if (_pumpThread is null) return;
        _stopRequested = true;
        PostThreadMessageW(_pumpThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _pumpThread.Join(1000);
        _pumpThread = null;
        Log.Info("MouseHook", "disposed");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        var msg = (int)wParam.ToInt64();
        // 只关心鼠标按下三键; 移动/双击/滚轮/抬起全部直接 pass-through.
        if (msg is not (WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN))
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var owner = _ownerHwnd;
        if (owner == IntPtr.Zero)
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        // 命中 overlay 矩形内 → pass-through (卡内点击按现有 drag / pin / close 处理).
        if (GetWindowRect(owner, out var r))
        {
            if (data.pt.x >= r.Left && data.pt.x <= r.Right
                && data.pt.y >= r.Top && data.pt.y <= r.Bottom)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
        // 矩形外 → 通知 UI 线程. swallow 这次按键, 避免点中的目标应用收到 click
        // 之后又 (若它没焦点) 反而被我们 activate 引发的副作用; 对主热键 Alt 按住
        // 路径, Alt-up commit 会另走 hook 路径激活, 点中的目标其实收不收无所谓.
        Log.Info("MouseHook", $"click-outside at ({data.pt.x},{data.pt.y}) → Cancel");
        _dispatcher.BeginInvoke(new Action(() => ClickOutsideOverlay?.Invoke()));
        return new IntPtr(1);
    }

    // ---------- 专用泵线程的 message loop P/Invoke (self-contained) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}