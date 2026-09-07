using System;
using System.IO;
using System.Text;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Tiny file logger used to diagnose hotkey / enumeration issues. Writes
/// to <c>%APPDATA%\WindowSwitcherWpf\window-switcher.log</c> with
/// append-only semantics. Thread-safe; very low allocation.
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _path;
    private static bool _initialised;

    public static string FilePath
    {
        get
        {
            EnsureInit();
            return _path!;
        }
    }

    public static void Info(string category, string message) => Write("INFO", category, message);

    /// <summary>
    /// Per-window/per-item diagnostics. These dominated Alt+` open latency
    /// (800+ File.AppendAllText calls ≈ 250ms on every keypress), so they
    /// are skipped unless WINDOWSWITCHER_VERBOSE=1 is set.
    /// </summary>
    public static void Verbose(string category, string message)
    {
        if (Environment.GetEnvironmentVariable("WINDOWSWITCHER_VERBOSE") != "1") return;
        Write("INFO", category, message);
    }
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message) => Write("ERROR", category, message);

    public static void Exception(string category, Exception ex) =>
        Error(category, ex.Message + "\n" + ex.StackTrace);

    private static void EnsureInit()
    {
        if (_initialised) return;
        lock (Lock)
        {
            if (_initialised) return;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowSwitcherWpf");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "window-switcher.log");
            try
            {
                File.AppendAllText(_path,
                    $"\n====== start {DateTime.Now:O} ======\n");
            }
            catch { /* ignore */ }
            _initialised = true;
        }
    }

    private static void Write(string level, string category, string message)
    {
        EnsureInit();
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {category} {message}\n";
        try
        {
            lock (Lock) File.AppendAllText(_path!, line, Encoding.UTF8);
        }
        catch { /* swallow */ }
    }
}
