using System;
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

    public List<StackDefinition> Stacks { get; set; } = new();

    /// <summary>主快捷键 (唤出 overlay / 循环切换), 格式 "Alt+`" / "Alt+Tab" /
    /// "Alt+OemMinus"。修饰键必须含 Alt (v1 钩子模型: 松开 Alt = 提交)。
    /// null/空 = 默认 "Alt+`"。</summary>
    public string? Hotkey { get; set; }

    /// <summary>一次直达键的修饰键 (quick+N 直达第 N 个窗口), "Alt"/"Ctrl"/
    /// "Shift"/"Win"。null/空 = 默认 "Alt"。注意与两段式区分: 两段式
    /// (先唤 overlay 再按数字) 固定走 Alt+数字, 不可配置。</summary>
    public string? QuickJumpModifier { get; set; }

    /// <summary>是否拦截系统 Alt+Tab。null = 自动 (Hotkey 为 Alt+Tab 时
    /// 拦截, 否则放行); false = 永不拦截; true = 永远拦截。</summary>
    public bool? SuppressAltTab { get; set; }
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

/// <summary>手动堆叠 (自定义集中): an ordered set of windows collapsed into
/// ONE slot on the main page. Members[0] is the head — it lends the stack
/// its slot number and its group color (以子标签的第一个标签属性为准).
/// Identity is GroupKey+Title, the same stable identity pins use; a
/// stack with fewer than 2 alive members just goes dormant (散开).</summary>
public sealed class StackDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public List<StackMemberDef> Members { get; set; } = new();
}

/// <summary>One member of a stack, persisted by the same (GroupKey, Title)
/// identity as pins/templates — generic structural matching, no app names
/// hardcoded anywhere (通用过滤).</summary>
public sealed class StackMemberDef
{
    public string GroupKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}
