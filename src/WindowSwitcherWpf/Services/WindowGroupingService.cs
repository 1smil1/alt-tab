using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WindowSwitcherWpf.Models;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Collapses the live <see cref="WindowEntry"/> list into user-pinned groups,
/// preserving the saved order. New groups are appended after the pinned ones.
/// </summary>
public sealed class WindowGroupingService
{
    public ObservableCollection<WindowGroup> Group(IEnumerable<WindowEntry> windows, OrderFile order)
    {
        var live = windows
            .GroupBy(w => w.GroupKey)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<WindowGroup>();
        var used = new HashSet<string>();

        foreach (var pinned in order.Groups)
        {
            if (!live.TryGetValue(pinned.GroupKey, out var list))
            {
                // Stale pinned group — skip; will reappear if app returns.
                continue;
            }
            used.Add(pinned.GroupKey);
            result.Add(new WindowGroup(
                pinned.GroupKey,
                PreferDisplayName(list, pinned.DisplayName),
                list));
        }

        foreach (var (key, list) in live)
        {
            if (used.Contains(key)) continue;
            var name = list[0].ProcessName;
            result.Add(new WindowGroup(key, name, list));
        }
        return new ObservableCollection<WindowGroup>(result);
    }

    private static string PreferDisplayName(IReadOnlyList<WindowEntry> windows, string cached)
    {
        if (windows.Count == 0) return cached;
        var first = windows[0];
        if (first.ProcessName.Equals(System.IO.Path.GetFileName(cached), System.StringComparison.OrdinalIgnoreCase))
            return cached;
        return first.ProcessName;
    }
}

public sealed class WindowGroup
{
    public WindowGroup(string key, string displayName, IReadOnlyList<WindowEntry> windows)
    {
        Key = key;
        DisplayName = displayName;
        Windows = new ObservableCollection<WindowEntry>(windows);
    }

    public string Key { get; }
    public string DisplayName { get; set; }
    public ObservableCollection<WindowEntry> Windows { get; }
    public int WindowCount => Windows.Count;
}
