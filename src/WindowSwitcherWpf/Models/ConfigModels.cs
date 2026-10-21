using System;
using System.Collections.Generic;

namespace WindowSwitcherWpf.Models;

/// <summary>
/// Exe-adjacent <c>config.json</c>: 堆叠 + 快捷键 + 自启动. 模板/智能排序
/// 已删 (用户: 只保留固定标签的记忆模型).
/// </summary>
public sealed class ConfigFile
{
    public int Version { get; set; } = 1;

    public List<StackDefinition> Stacks { get; set; } = new();

    /// <summary>主快捷键 (唤出 overlay / 循环切换), 格式 "Alt+`" / "Alt+Tab" /
    /// "Alt+OemMinus"。修饰键必须含 Alt (v1 钩子模型: 松开 Alt = 提交)。
    /// null/空 = 默认 "Alt+Tab" (替换系统 Alt+Tab).</summary>
    public string? Hotkey { get; set; }

    /// <summary>第二个热键 (一按就开 overlay, 直到 Esc 才关 — 不受 Alt-up
    /// 提交影响, 可选关闭). 格式同 Hotkey. null/空 = 默认 "Alt+`"。
    /// 空字符串 = 禁用第二热键.</summary>
    public string? SecondHotkey { get; set; }

    /// <summary>一次直达键的修饰键 (quick+N 直达第 N 个窗口), "Alt"/"Ctrl"/
    /// "Shift"/"Win"。null/空 = 默认 "Alt"。注意与两段式区分: 两段式
    /// (先唤 overlay 再按数字) 固定走 Alt+数字, 不可配置。</summary>
    public string? QuickJumpModifier { get; set; }

    /// <summary>是否拦截系统 Alt+Tab。null = 自动 (Hotkey 为 Alt+Tab 时
    /// 拦截, 否则放行); false = 永不拦截; true = 永远拦截。</summary>
    public bool? SuppressAltTab { get; set; }

    /// <summary>开机自启动 (写 HKCU\...\Run). null = 默认 true (首次启动
    /// 写一次注册表); true/false = 用户在设置里手动改过.</summary>
    public bool? AutoStart { get; set; }

    /// <summary>面板内选中并提交 (方向键/Alt+Tab 循环后松开 Alt) 时, 选中的
    /// 窗口就是当前前台 → 缩小化它. null = 默认 false (照旧只激活).</summary>
    public bool? CommitMinimizeOnSame { get; set; }

    /// <summary>同上, 但只对固定标签生效. null = 跟随 CommitMinimizeOnSame
    /// (未固定与固定分开开关 — 用户可只让流动标签缩小化).</summary>
    public bool? CommitMinimizePinnedOnSame { get; set; }

    /// <summary>一次直达 (Alt+数字, overlay 关着) 目标就是前台 → 缩小化.
    /// null = 默认 true (现行为: 再按一次收起).</summary>
    public bool? QuickMinimizeOnSame { get; set; }

    /// <summary>同上, 但只对固定标签生效. null = 跟随 QuickMinimizeOnSame.</summary>
    public bool? QuickMinimizePinnedOnSame { get; set; }

    /// <summary>切到窗口后, 在那个窗口外框闪一圈蓝光然后马上消失 (最小化
    /// 的窗口恢复上来那一刻也能看见). null = 默认 true.</summary>
    public bool? SelectionGlow { get; set; }

    /// <summary>虚拟桌面热键的**修饰键前缀** ("Alt+Ctrl" / "Ctrl+Alt" /
    /// "Win+Ctrl"), 后面跟数字 = 切到第 N 个虚拟桌面 (0 = 第 10 个).
    /// null/空 = 关闭该功能. 注意与 <see cref="QuickJumpModifier"/> 的区别:
    /// 那个是"一次直达窗口"的修饰键, 这个是"切虚拟桌面"的.</summary>
    public string? VirtualDesktopHotkey { get; set; }

    /// <summary>面板开着时"把选中的卡迁到第 N 个桌面"的前缀 (后面跟数字,
    /// 0 = 第 10 个). 默认与 <see cref="VirtualDesktopHotkey"/> 相同:
    /// 面板关着 = 切桌面, 面板开着 = 迁卡片. null/空 = 关闭.</summary>
    public string? MigrateCardHotkey { get; set; }

    /// <summary>迁移窗口热键的**修饰键前缀** ("Alt+Ctrl+Shift"), 后面跟
    /// ←/→ = 把窗口/选中的卡片搬到左/右相邻虚拟桌面. null/空 = 关闭.</summary>
    public string? MoveWindowHotkey { get; set; }

    /// <summary>面板标签是否**跨虚拟桌面共用一份**. false (默认, 与 Windows
    /// 原生 Alt+Tab 和主流工具一致) = 每个虚拟桌面只显示自己的窗口;
    /// true = 全局一份, 别的桌面的窗口也列出来 (卡片带 D号 角标).</summary>
    public bool GlobalTagsAcrossDesktops { get; set; }
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
