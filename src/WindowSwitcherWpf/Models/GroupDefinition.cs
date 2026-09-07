using System.Collections.Generic;

namespace WindowSwitcherWpf.Models;

/// <summary>
/// User-pinned ordering of process groups. The group key is the full module
/// path (with optional AUMID suffix). Windows are not persisted — they are
/// re-enumerated at runtime.
/// </summary>
public sealed class GroupDefinition
{
    public GroupDefinition() { }

    public GroupDefinition(string groupKey, string displayName)
    {
        GroupKey = groupKey;
        DisplayName = displayName;
    }

    /// <summary>Stable identity, matches <see cref="WindowEntry.GroupKey"/>.</summary>
    public string GroupKey { get; set; } = string.Empty;

    /// <summary>Cached display label, refreshed from the first live window of the group.</summary>
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class OrderFile
{
    public int Version { get; set; } = 1;
    public List<GroupDefinition> Groups { get; set; } = new();

    /// <summary>Windows pinned to numbered slots (1..10). Pin fixes the
    /// NUMBER, not the position — unpinned windows flow around them.</summary>
    public List<PinDefinition> Pins { get; set; } = new();
}

/// <summary>A pinned window. Windows are ephemeral (hwnd dies with the
/// process), so identity = process group + window title at pin time; a pin
/// whose window is gone stays dormant in the file.</summary>
public sealed class PinDefinition
{
    public PinDefinition() { }

    public PinDefinition(string groupKey, string title, int slot)
    {
        GroupKey = groupKey;
        Title = title;
        Slot = slot;
    }

    /// <summary>Stable identity, matches <see cref="WindowEntry.GroupKey"/>.</summary>
    public string GroupKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The frozen slot number, 1..10. Slot 0 (most recent) is special and never pinnable.</summary>
    public int Slot { get; set; }
}

/// <summary>One card in the overlay: the live window plus its computed
/// slot. Slot 0 = special most-recent, 1..10 = managed, -1 = unmanaged
/// (no badge). IsPinned mirrors the persisted pin that matched.</summary>
public sealed record SlotWindow(WindowEntry Entry, int Slot, bool IsPinned);
