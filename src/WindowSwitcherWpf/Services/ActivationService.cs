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
        try
        {
            WindowSwitcherWpf.Services.Log.Info("Activate",
                $"Activate 0x{hwnd.ToInt64():X}");
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            var noop = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT() } };
            SendInput(1, new[] { noop }, Marshal.SizeOf<INPUT>());
            if (SetForegroundWindow(hwnd))
            {
                WindowSwitcherWpf.Services.Log.Info("Activate", "SetForegroundWindow ok");
                return true;
            }
            WindowSwitcherWpf.Services.Log.Warn("Activate",
                "first SetForegroundWindow returned false, retrying");
            System.Threading.Thread.Sleep(50);
            var ok = SetForegroundWindow(hwnd);
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
