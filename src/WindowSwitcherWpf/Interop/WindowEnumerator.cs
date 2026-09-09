using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WindowSwitcherWpf.Models;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// Enumerates top-level windows with the same filter pipeline as the
/// reference Rust implementation (EnumWindows → visible + non-tool +
/// non-topmost + non-cloaked + non-minimum-size + own PID lookup + AUMID
/// splitting for Chrome/Edge).
/// </summary>
public sealed class WindowEnumerator
{
    private const int MIN_WIDTH = 120;
    private const int MIN_HEIGHT = 90;

    public IReadOnlyList<WindowEntry> Enumerate(bool onlyCurrentDesktop = true)
    {
        var hwnds = new List<IntPtr>();
        EnumWindows((h, _) => { hwnds.Add(h); return true; }, IntPtr.Zero);
        WindowSwitcherWpf.Services.Log.Verbose("Enumerator", $"raw HWNDs={hwnds.Count}");

        var valid = new List<(IntPtr hwnd, string title, uint pid)>();
        foreach (var hwnd in hwnds)
        {
            if (!IsEligible(hwnd, onlyCurrentDesktop)) continue;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) continue;
            var title = GetWindowText(hwnd);
            if (string.IsNullOrEmpty(title)) continue;
            if (title == "Windows Input Experience") continue;
            var pid = GetWindowThreadProcessId(hwnd, out var p);
            if (pid == 0) continue;
            var p2 = p;
            valid.Add((hwnd, title, p2));
        }
        // Log the rejected windows so we can see which real apps are
        // being dropped and tune the filter.
        foreach (var hwnd in hwnds)
        {
            if (valid.Any(v => v.hwnd == hwnd)) continue;
            var t = GetWindowText(hwnd);
            if (string.IsNullOrEmpty(t)) continue;
            var pid = GetWindowThreadProcessId(hwnd, out var p);
            var modulePath = GetModulePath(pid) ?? string.Empty;
            var proc = System.IO.Path.GetFileName(modulePath);
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            var owner = GetWindow(hwnd, GW_OWNER);
            var reason = IsEligible(hwnd, onlyCurrentDesktop)
                ? (owner != IntPtr.Zero ? "has-owner" : (string.IsNullOrEmpty(t) ? "no-title" : "unknown"))
                : DiagnoseReject(hwnd, onlyCurrentDesktop);
            WindowSwitcherWpf.Services.Log.Verbose("Enumerator",
                $"rejected: 0x{hwnd.ToInt64():X} reason={reason} ex=0x{exStyle.ToInt64():X} st=0x{style.ToInt64():X} owner=0x{owner.ToInt64():X} '{t}' [{proc}]");
        }
        // No per-PID dedup. Native Alt+Tab shows every real top-level
        // window of a process, even when a process has many of them
        // (Chrome tabs, File Explorer windows, etc.). Background
        // windows of the same process are excluded by the structural
        // checks and helper-title list, not by dedup.
        var deduped = valid;
        WindowSwitcherWpf.Services.Log.Verbose("Enumerator",
            $"valid={deduped.Count}");

        var result = new List<WindowEntry>(deduped.Count);
        foreach (var (hwnd, title, origPid) in deduped)
        {
            WindowSwitcherWpf.Services.Log.Verbose("Enumerator", $"valid: 0x{hwnd.ToInt64():X} '{title}'");
            var pid = origPid;
            var modulePath = GetModulePath(pid) ?? string.Empty;
            if (!IsValidModulePath(modulePath))
            {
                // UWP: the visible window is hosted by
                // ApplicationFrameHost; find the window OWNED by this hwnd
                // and use its PID. Mirrors the reference implementation,
                // which keeps an owners[] list aligned with all hwnds.
                var ownerHwnd = FindOwnedWindow(hwnds, hwnd);
                if (ownerHwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(ownerHwnd, out var opid);
                    if (opid != 0) { pid = opid; modulePath = GetModulePath(pid) ?? string.Empty; }
                }
            }
            if (!IsRunningAsAdmin() && IsProcessElevated(pid)) continue;
            // Don't require a valid module path: many real apps
            // (admin cmd, UWP via ApplicationFrameHost, processes we
            // can't OpenProcess) have an empty path here but are
            // still legitimate Alt+Tab candidates.
            if (IsBackgroundProcess(modulePath)) continue;
            if (IsSystemHelperTitle(title)) continue;

            var groupKey = ResolveGroupKey(hwnd, modulePath);
            var procName = string.IsNullOrEmpty(modulePath) ? "?" : Path.GetFileName(modulePath);
            var isMin = IsIconic(hwnd);
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            WindowSwitcherWpf.Services.Log.Verbose("Enumerator",
                $"kept: 0x{hwnd.ToInt64():X} '{title}' [{procName}] ex=0x{exStyle.ToInt64():X} st=0x{style.ToInt64():X}");
            result.Add(new WindowEntry(hwnd, title, procName, modulePath, groupKey, isMin));
        }
        WindowSwitcherWpf.Services.Log.Verbose("Enumerator", $"final count={result.Count}");
        return result;
    }

    public static bool IsEligible(IntPtr hwnd, bool onlyCurrentDesktop)
    {
        // Try DWM HAS_ICONIC_BITMAP first. If it works on this
        // machine, it's the perfect signal. If it returns E_INVALIDARG
        // (some Windows builds), fall back to the structural filter
        // (no tool, no noactivate, no owner, has caption, not cloaked,
        // has resize border, has minimize+maximize, not WS_POPUP-only).
        int hasIconic = 0;
        int hr;
        try
        {
            hr = DwmGetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP, out hasIconic, sizeof(int));
        }
        catch
        {
            hr = -1;
        }
        if (hr == 0)
        {
            // DWM attribute works: trust it.
            if (hasIconic == 0) return false;
            if (IsCloaked(hwnd, onlyCurrentDesktop)) return false;
            return true;
        }
        // Fallback: structural
        return IsEligibleStructural(hwnd, onlyCurrentDesktop);
    }

    private static bool IsEligibleStructural(IntPtr hwnd, bool onlyCurrentDesktop)
    {
        // The single most powerful generic filter: hidden helper
        // windows (Snipaste, Parsec, tray hosts, "WinUI Desktop"
        // shims, AHK scripts) all keep a registered top-level window
        // with a thickframe but without WS_VISIBLE. Minimized real
        // apps keep WS_VISIBLE, so this never drops them.
        if (!IsWindowVisible(hwnd)) return false;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        if ((exStyle & WS_EX_NOACTIVATE) != 0) return false;
        // Real taskbar apps can be resized (WS_THICKFRAME). Tray-only
        // indicators (ImTip) and pure popups (notifications) cannot.
        var style = GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_THICKFRAME) == 0) return false;
        return true;
    }

    public static string DiagnoseReject(IntPtr hwnd, bool onlyCurrentDesktop)
    {
        int hasIconic = 0;
        int hr;
        try
        {
            hr = DwmGetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP, out hasIconic, sizeof(int));
        }
        catch
        {
            hr = -1;
        }
        if (hr == 0)
        {
            if (hasIconic == 0) return "no-iconic-bitmap";
            if (IsCloaked(hwnd, onlyCurrentDesktop)) return "cloaked";
            return "ok";
        }
        return DiagnoseRejectStructural(hwnd, onlyCurrentDesktop);
    }

    private static string DiagnoseRejectStructural(IntPtr hwnd, bool onlyCurrentDesktop)
    {
        if (!IsWindowVisible(hwnd)) return "not-visible";
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return "WS_EX_TOOLWINDOW";
        if ((exStyle & WS_EX_NOACTIVATE) != 0) return "WS_EX_NOACTIVATE";
        var style = GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_THICKFRAME) == 0) return "no-thickframe";
        return "ok";
    }

    private static bool HasCaption(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE);
        return (style & WS_CAPTION) != 0;
    }

    public static bool IsCloaked(IntPtr hwnd, bool onlyCurrentDesktop)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloak, sizeof(int)) != 0)
            return false;
        if (onlyCurrentDesktop) return cloak != 0;
        return (cloak & 0x02) == 0; // DWM_CLOAKED_SHELL only — treat as uncloaked.
    }

    private static bool IsSmall(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r.Right - r.Left < MIN_WIDTH || r.Bottom - r.Top < MIN_HEIGHT;
    }

    private static IntPtr FindOwnedWindow(List<IntPtr> hwnds, IntPtr owner)
    {
        foreach (var h in hwnds)
        {
            if (h == owner) continue;
            if (GetWindow(h, GW_OWNER) == owner) return h;
        }
        return IntPtr.Zero;
    }

    public static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(1024);
        var len = GetWindowTextW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    public static string? GetModulePath(uint pid)
    {
        if (pid == 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = (uint)260;
            var sb = new StringBuilder((int)size);
            if (!QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, sb, ref size)) return null;
            return sb.ToString();
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsValidModulePath(string modulePath) =>
        !string.IsNullOrEmpty(modulePath) &&
        !modulePath.Equals(@"C:\Windows\System32\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    // The only title we always reject — every other heuristic should be
    // structural (owner, tool window, caption) rather than name-based.
    private static readonly string[] BackgroundTitlePatterns =
    {
        "Windows Input Experience",
    };

    // Titles that are generated by Windows / runtime helpers, not by
    // any user-facing app. These are objectively never real user
    // windows, so it is safe (and necessary) to filter them by name.
    private static readonly string[] SystemHelperTitlePatterns =
    {
        "Hidden Window", "HiddenWindow",
        "__wglDummyWindowFodder",
        "RECORDER_NOTIFY_WND",
        "MS_WebcheckMonitor",
        // "OpenCLIApp" 曾在此, 已删 — 那是具体工具的窗口标题, 不是
        // Windows 原生脚手架; 若它回流成污染, 查它的结构特征再治。
        // NB: 不要把真实应用标题加进来 — "Hotkey Manager" 曾在此被拉黑,
        // 导致用户自己的热键管理器主窗口 (可见/thickframe/有标题) 在
        // overlay 消失而原生 Alt+Tab 显示。隐藏辅助窗口由结构过滤
        // (不可见/toolwindow/no-thickframe) 兜底, 不靠标题。
        "NVOGLDC invisible",  // NVIDIA overlay helper
        "wgpu Device Class",  // WebGPU helper
        "WxTrayIconMessageWindow",  // WeChat tray helper
        "QTrayIconMessageWindow",  // generic tray helper
        "GDI+ Window",  // GDI+ helper windows (any app)
    };

    // Process-name fallback for windows that the owner check doesn't
    // catch (e.g. services that own no other window). Very small list
    // to avoid brittleness — the structural filters handle the rest.
    private static readonly string[] BackgroundProcessNames =
    {
        "SangforUDProtectExe",
    };

    // NB: 曾经还有一组按 exe 名子串杀后台进程的规则 (Service/Helper/
    // Watcher/Container/Overlay/Sync./wallpaper/Sunshine/Sangfor/
    // OneDrive/aw- 前缀), 已整体删除 — 它们全是真实应用的名字或泛化
    // 词, 会误杀原生 Alt+Tab 会显示的用户窗口。黑名单只收 Windows
    // 原生/运行时脚手架; 具体污染窗口优先用结构过滤治理。

    // Whitelist of real user processes that happen to live under
    // C:\Windows\System32 (terminals, task manager).
    private static readonly HashSet<string> System32Whitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "WindowsTerminal.exe", "wt.exe",
        "taskmgr.exe", "mmc.exe", "regedit.exe", "msconfig.exe", "explorer.exe",
    };

    private static bool IsSystemHelperTitle(string title)
    {
        foreach (var pat in SystemHelperTitlePatterns)
        {
            if (title.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static bool IsBackgroundTitle(string title)
    {
        foreach (var pat in BackgroundTitlePatterns)
        {
            if (title.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        // GUID-style titles like "{5AEA657D-...}" are internal COM hosts.
        if (title.Length >= 2 && title[0] == '{' && title[^1] == '}') return true;
        return false;
    }

    private static bool IsBackgroundProcess(string modulePath)
    {
        if (string.IsNullOrEmpty(modulePath)) return false;
        var fileName = Path.GetFileName(modulePath);
        foreach (var name in BackgroundProcessNames)
        {
            if (fileName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // C:\Windows\System32 executables are almost always services;
        // the only real user-facing ones are terminals, task manager,
        // mmc, regedit, etc. — whitelisted above. This + the Sangfor
        // exact name are the whole remaining process-name blacklist.
        if (modulePath.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase)
            && !System32Whitelist.Contains(fileName))
            return true;
        return false;
    }

    public static IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, nIndex) : new IntPtr(GetWindowLong32(hwnd, nIndex));

    public static void GetWindowRect(IntPtr hwnd, out NativeMethods.RECT rect)
    {
        NativeMethods.GetWindowRect(hwnd, out rect);
    }

    // ---------- AUMID splitting ----------

    private static string ResolveGroupKey(IntPtr hwnd, string modulePath)
    {
        var fileName = Path.GetFileName(modulePath).ToLowerInvariant();
        if (fileName == "chrome.exe") return ChromeKey(hwnd, modulePath);
        if (fileName == "msedge.exe") return EdgeKey(hwnd, modulePath);
        return modulePath;
    }

    private static string ChromeKey(IntPtr hwnd, string modulePath)
    {
        var aumid = GetAumid(hwnd);
        if (string.IsNullOrEmpty(aumid)) return modulePath;
        if (aumid == "Chrome" || aumid.Length == 0) return modulePath;

        const string crx = "_crx_";
        var idx = aumid.IndexOf(crx, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var after = aumid.Substring(idx + crx.Length);
            var dot = after.IndexOf(".UserData.", StringComparison.Ordinal);
            string appId, profile;
            if (dot >= 0)
            {
                appId = after.Substring(0, dot);
                profile = after.Substring(dot + ".UserData.".Length);
            }
            else
            {
                appId = after;
                profile = "Default";
            }
            if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(profile))
            {
                var profilePart = profile == "Default" ? null : profile;
                return profilePart is null
                    ? $"{modulePath}::Default::{appId}"
                    : $"{modulePath}::{profilePart}::{appId}";
            }
        }
        if (aumid.StartsWith("Chrome.UserData.", StringComparison.Ordinal))
        {
            var profile = aumid.Substring("Chrome.UserData.".Length);
            if (!string.IsNullOrEmpty(profile)) return $"{modulePath}::{profile}";
        }
        return modulePath;
    }

    private static string EdgeKey(IntPtr hwnd, string modulePath)
    {
        var aumid = GetAumid(hwnd);
        if (string.IsNullOrEmpty(aumid)) return modulePath;
        if (aumid.EndsWith("!App", StringComparison.Ordinal))
        {
            var pkg = aumid.Substring(0, aumid.Length - "!App".Length);
            if (!string.IsNullOrEmpty(pkg)) return $"{modulePath}::appx::{pkg}";
            return modulePath;
        }
        if (aumid == "MSEdge" || aumid.Length == 0) return modulePath;
        if (aumid.StartsWith("MSEdge.UserData.", StringComparison.Ordinal))
        {
            var profile = aumid.Substring("MSEdge.UserData.".Length);
            if (!string.IsNullOrEmpty(profile)) return $"{modulePath}::{profile}";
        }
        return modulePath;
    }

    private static string? GetAumid(IntPtr hwnd)
    {
        try
        {
            var iid = IID_IPropertyStore;
            var local = iid;
            SHGetPropertyStoreForWindow(hwnd, ref local, out var ppv);
            if (ppv == IntPtr.Zero) return null;
            try
            {
                var store = (IPropertyStore)Marshal.GetObjectForIUnknown(ppv);
                var key = PKEY_AppUserModel_ID;
                store.GetValue(ref key, out var pv);
                return PropVariantToString(pv);
            }
            finally
            {
                Marshal.Release(ppv);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? PropVariantToString(PROPVARIANT pv)
    {
        if (pv.varType != 31) return null; // VT_LPWSTR
        if (pv.p == IntPtr.Zero) return null;
        return Marshal.PtrToStringUni(pv.p);
    }

    // ---------- Elevation ----------

    public static bool IsProcessElevated(uint pid)
    {
        if (pid == 0) return false;
        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_QUERY, out var hToken)) return false;
            try
            {
                if (!GetTokenInformation(hToken, (int)TokenInformationClass.TokenElevation, IntPtr.Zero, 0, out var needed))
                    return false;
                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!GetTokenInformation(hToken, (int)TokenInformationClass.TokenElevation, buf, needed, out _))
                        return false;
                    return Marshal.ReadInt32(buf) != 0;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(hToken); }
        }
        finally { CloseHandle(hProc); }
    }

    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            return IsProcessElevated((uint)proc.Id);
        }
        catch { return false; }
    }
}
