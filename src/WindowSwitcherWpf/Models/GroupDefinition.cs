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

    /// <summary>For a top-level pin, the window title. For a stack-member
    /// pin, the HEAD window's title (the parent stack's identity).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The frozen slot number, 1..10. Slot 0 (most recent) is special
    /// and never pinnable. For a member pin: the member's frozen position
    /// inside its stack (1-based).</summary>
    public int Slot { get; set; }

    /// <summary>Non-null ⇒ this pin belongs to the stack member with this
    /// title (its parent stack is identified by GroupKey+Title above).
    /// Null ⇒ a top-level card pin. Old order.json files lack the field and
    /// deserialize as null, which keeps them valid top-level pins.</summary>
    public string? MemberTitle { get; set; }

    /// <summary>Non-null (member pins): the parent stack's stable config Id.
    /// 成员重排会换 head (members[0]) — 锚定 head.Title 在 head 换人后失配,
    /// 所以新 member pin 记 StackId; 旧文件无此字段, 匹配回退 GroupKey+Title.
    /// Null on top-level pins and legacy member pins.</summary>
    public string? StackId { get; set; }
}

/// <summary>One card in the overlay: the live window plus its computed
/// slot. Slot 0 = special most-recent, 1..10 = managed, -1 = unmanaged
/// (no badge). IsPinned mirrors the persisted pin that matched.
/// IsCurrentCopy = 当前窗口卡 (Windows Alt+Tab 左起第一张 = 你刚离开的
/// 窗口): 恒占 index 0, Slot=-2 → 无数字 badge. 纯展示卡 — 不参与数字
/// 寻址/导出/拖拽/cascade. fg 同时是 pin 卡时双卡并存: 真实卡留在编号区
/// (Alt+数字 直达它 = 激活当前窗口, 无害)。
/// IsSlot0Copy = 上一窗口 (Slot=0) 本身是 pin 卡时的显示副本 (用户: 标签3
/// 固定则一直存在, 除非它没有固定): 真身留在编号区由 pin 认领, 0 号只是
/// 展示. 未固定的上一窗口没有真身卡 — 该卡是唯一卡, 标志保持 false.</summary>
public sealed record SlotWindow(WindowEntry Entry, int Slot, bool IsPinned,
    bool IsCurrentCopy = false, bool IsSlot0Copy = false)
{
    /// <summary>纯展示副本 (-2 当前窗口副本 / 0 号位副本): 不吃蓝框、
    /// 不做光标停靠点、不被数字寻址 — 蓝框永远画在同 hwnd 的真卡上。</summary>
    public bool IsCardCopy => IsCurrentCopy || IsSlot0Copy;
}
