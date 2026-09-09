using System;

namespace WindowSwitcherWpf.Models;

/// <summary>
/// A single live top-level window discovered by <c>WindowEnumerator</c>.
/// Stable identity is <see cref="GroupKey"/> (the process identity), not the
/// <see cref="Hwnd"/> which is recycled.
/// </summary>
public sealed record WindowEntry(
    IntPtr Hwnd,
    string Title,
    string ProcessName,
    string ModulePath,
    string GroupKey,
    bool IsMinimized)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
}
