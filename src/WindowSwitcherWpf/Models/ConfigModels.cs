using System.Collections.Generic;

namespace WindowSwitcherWpf.Models;

/// <summary>
/// Exe-adjacent <c>config.json</c>: all templates and smart-sort methods in
/// ONE file (用一个json维护所有模板和智能排序). Data lives here — the
/// matching engine stays generic/structural (通用过滤), never app-specific.
/// </summary>
public sealed class ConfigFile
{
    public int Version { get; set; } = 1;

    public List<TemplateDefinition> Templates { get; set; } = new();

    public List<SortMethodDefinition> SortMethods { get; set; } = new();
}

/// <summary>A named slot layout. Applying a template matches live windows
/// to the slot entries 一一对应 and turns them into pins.</summary>
public sealed class TemplateDefinition
{
    public string Name { get; set; } = string.Empty;

    public List<TemplateSlot> Slots { get; set; } = new();
}

/// <summary>One slot of a template. Identity is GroupKey (module path) plus
/// an optional Title — empty Title matches any window of the group.</summary>
public sealed class TemplateSlot
{
    public int Slot { get; set; }

    public string GroupKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

/// <summary>A named smart-sort method: rules in priority order — the first
/// rule wins for a window, unmatched windows trail in MRU order
/// (谁优先级高谁在最前面; array order = priority).</summary>
public sealed class SortMethodDefinition
{
    public string Name { get; set; } = string.Empty;

    public List<SortRule> Rules { get; set; } = new();
}

/// <summary>Generic structural rule: match by process name, module path
/// substring, window title substring, or exact GroupKey.</summary>
public sealed class SortRule
{
    /// <summary>"process" | "path" | "title" | "group".</summary>
    public string Type { get; set; } = "process";

    public string Value { get; set; } = string.Empty;
}
