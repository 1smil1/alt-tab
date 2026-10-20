using System;
using System.Runtime.InteropServices;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Foreground-window activation. Uses the same trick as PowerToys
/// FancyZones (credited inline in the Rust reference): restore the window,
/// inject a no-op MOUSEINPUT to claim the foreground rights, then
/// <c>SetForegroundWindow</c>.
/// </summary>
public static class ActivationService
{
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        // 标签是全局的, 面板里可能有别的虚拟桌面的窗口. Windows 不会为
        // SetForegroundWindow 自动切桌面 — 跨桌面激活静默失败, 所以先切
        // 到窗口所在桌面, 等切完再激活.
        var id = WindowSwitcherWpf.Interop.VirtualDesktop.DesktopIdOfWindow(hwnd);
        var cur = WindowSwitcherWpf.Interop.VirtualDesktop.CurrentGuid;
        if (id != Guid.Empty && cur != Guid.Empty && id != cur)
        {
            var index = DesktopIndexOf(id);
            if (index >= 0 && WindowSwitcherWpf.Interop.VirtualDesktop.SwitchTo(index))
            {
                WindowSwitcherWpf.Services.Log.Info("Activate",
                    $"cross-desktop → 桌面 {index + 1}, 延迟激活 0x{hwnd.ToInt64():X}");
                ActivateAfterDesktopSwitch(hwnd);
                return true;
            }
        }
        return ActivateNow(hwnd);
    }

    private static int DesktopIndexOf(Guid id)
    {
        foreach (var d in WindowSwitcherWpf.Interop.VirtualDesktop.List())
            if (d.Id == id) return d.Index;
        return -1;
    }

    /// <summary>桌面切换动画 (~0.3s) 期间窗口还没显示, 直接激活必失败.
    /// 定时重试, 激活成功后闪一下边框.</summary>
    private static void ActivateAfterDesktopSwitch(IntPtr hwnd)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        var tries = 0;
        timer.Tick += (_, _) =>
        {
            tries++;
            var ok = ActivateNow(hwnd);
            if (!ok && tries < 5) return;
            timer.Stop();
            WindowSwitcherWpf.Services.Log.Info("Activate",
                $"cross-desktop activate → {ok} after {tries} tries");
        };
        timer.Start();
    }

    private static bool ActivateNow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            WindowSwitcherWpf.Services.Log.Info("Activate",
                $"Activate 0x{hwnd.ToInt64():X}");
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            var noop = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT() } };
            SendInput(1, new[] { noop }, Marshal.SizeOf<INPUT>());
            if (SetForegroundWindow(hwnd))
            {
                // 先闪再记日志: 边框要抢在窗口切换之后立刻出现, 日志写盘
                // 是同步的, 排在前面就白白多几十毫秒.
                WindowSwitcherWpf.Interop.WindowFlash.Flash(hwnd);
                WindowSwitcherWpf.Services.Log.Info("Activate", "SetForegroundWindow ok");
                return true;
            }
            WindowSwitcherWpf.Services.Log.Warn("Activate",
                "first SetForegroundWindow returned false, retrying");
            System.Threading.Thread.Sleep(50);
            var ok = SetForegroundWindow(hwnd);
            if (ok) WindowSwitcherWpf.Interop.WindowFlash.Flash(hwnd);
            WindowSwitcherWpf.Services.Log.Info("Activate", $"retry -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Activate", ex);
            try
            {
                System.Windows.MessageBox.Show(ex.Message, "Activate failed",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch { }
            return false;
        }
    }
}
