using System;
using System.IO;
using Microsoft.Win32;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// 开机自启动: HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册 +
/// 路径后跟 -autostart 参数. exe 启动时识别该参数 → 延迟 10s 再装 LL hook.
///
/// 为什么这样: 之前用 schtasks (Task Scheduler) 创任务失败 — 用户级权限
/// 创任务需要 admin, Inno installer 用户级安装 (PrivilegesRequired=lowest)
/// 配套路径走不通. 回退到 HKCU\Run + 延迟.
///
/// 延迟目的: auto-start 在登录早期 (explorer.exe 没全 ready) 拉起 exe,
/// 此时 SetForegroundWindow 受限, LL hook 注册后行为不稳定 (用户实测
/// 1次我们的/1次系统交替). 延迟 10s 让桌面/explorer/tray 就绪, 装 hook
/// 后 SetForegroundWindow 不再受限.
///
/// Apply(enabled, exePath):
///   true  → 写值 "WindowSwitcherWpf" "exePath" -autostart
///   false → 删值
///
/// IsEnabled():
///   注册表值含当前 exe 路径 → true. Path 不匹配 (重装到别处) = 重建.
///
/// GetCurrentExePath():
///   dev 环境 ProcessPath 可能为空, 兜底走 BaseDirectory + exe 名.
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowSwitcherWpf";

    public static void Apply(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                Log.Error("AutoStart", "failed to open HKCU Run key");
                return;
            }
            if (enabled)
            {
                // 路径加引号 (路径含空格安全), 后跟 -autostart 让 exe 自身延迟.
                key.SetValue(ValueName, $"\"{exePath}\" -autostart");
                Log.Info("AutoStart", $"enabled → \"{exePath}\" -autostart");
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Log.Info("AutoStart", "disabled");
                }
                else
                {
                    Log.Info("AutoStart", "disabled (no-op, no existing value)");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception("AutoStart", ex);
        }
    }

    /// <summary>True 当且仅当注册表里的 exe 路径 == 当前的 exe 路径
    /// (忽略 -autostart 后缀).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var v = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(v)) return false;
            // 值可能是: "C:\path\exe.exe" -autostart 或 "C:\path\exe.exe"
            var first = v.Trim();
            if (first.StartsWith("\"")) first = first[1..];
            var endQuote = first.IndexOf('"');
            if (endQuote >= 0) first = first[..endQuote];
            var exe = GetCurrentExePath();
            return string.Equals(first.Trim(), exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Exception("AutoStart.IsEnabled", ex);
            return false;
        }
    }

    public static string GetCurrentExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p!;
        var fallback = Path.Combine(AppContext.BaseDirectory, "WindowSwitcherWpf.exe");
        return File.Exists(fallback) ? fallback : p ?? fallback;
    }
}