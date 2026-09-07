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
}
