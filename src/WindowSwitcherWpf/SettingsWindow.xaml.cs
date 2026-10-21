using System;
using System.Windows;
using System.Windows.Controls;
using WindowSwitcherWpf.Models;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf;

/// <summary>
/// 设置窗口 (按需创建, 关闭即回收 — 空闲零常驻): 编辑 config.json 的
/// 快捷键 / 自启动. 保存 = Load 最新 config → 只覆盖本窗口
/// 编辑的字段 → 写回 (Stacks 归 controller 独占维护, 不经这里) →
/// onSaved 回调 (热生效 hotkey + controller.ReloadConfig).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly Action? _onSaved;

    public SettingsWindow(ConfigStore store, Action? onSaved)
    {
        InitializeComponent();
        _store = store;
        _onSaved = onSaved;

        var cfg = store.Load();
        HotkeyBox.Text = cfg.Hotkey ?? "";
        SecondHotkeyBox.Text = cfg.SecondHotkey ?? "";
        QuickModBox.SelectedIndex = (cfg.QuickJumpModifier ?? "Alt") switch
        {
            "Ctrl" => 1,
            "Shift" => 2,
            "Win" => 3,
            _ => 0,
        };
        VdHotkeyBox.Text = cfg.VirtualDesktopHotkey ?? "";
        TagScopeBox.SelectedIndex = cfg.GlobalTagsAcrossDesktops ? 1 : 0;
        MoveHotkeyBox.Text = cfg.MoveWindowHotkey ?? "";
        MigrateCardBox.Text = cfg.MigrateCardHotkey ?? "";
        SuppressBox.SelectedIndex = cfg.SuppressAltTab switch
        {
            null => 0,
            true => 1,
            false => 2,
        };
        AutoStartBox.IsChecked = cfg.AutoStart ?? true;
        CommitMinBox.IsChecked = cfg.CommitMinimizeOnSame ?? false;
        CommitMinPinnedBox.IsChecked = cfg.CommitMinimizePinnedOnSame
                                        ?? cfg.CommitMinimizeOnSame ?? false;
        QuickMinBox.IsChecked = cfg.QuickMinimizeOnSame ?? true;
        QuickMinPinnedBox.IsChecked = cfg.QuickMinimizePinnedOnSame
                                       ?? cfg.QuickMinimizeOnSame ?? true;
        GlowBox.IsChecked = cfg.SelectionGlow ?? true;
        ValidateHotkey();
    }

    private void OnHotkeyChanged(object sender, TextChangedEventArgs e)
    {
        ValidateHotkey();
    }

    private void OnSecondHotkeyChanged(object sender, TextChangedEventArgs e)
    {
        ValidateHotkey();
    }

    private void OnVdHotkeyChanged(object sender, TextChangedEventArgs e)
    {
        ValidateHotkey();
    }

    private void ValidateHotkey()
    {
        if (HotkeyBox is null || SaveButton is null) return;
        var ok = HotkeyParser.TryParseKey(HotkeyBox.Text, out var vk)
                 && HotkeyParser.HasAlt(HotkeyBox.Text);
        // 第二热键: 空 = 禁用 (合法); 非空 = 须含 Alt 才合法.
        var second = SecondHotkeyBox?.Text ?? "";
        var secondOk = string.IsNullOrWhiteSpace(second)
            || (HotkeyParser.TryParseKey(second, out _) && HotkeyParser.HasAlt(second));
        // 虚拟桌面热键: 空 = 禁用 (合法); 非空 = 只能是修饰键.
        var vd = VdHotkeyBox?.Text ?? "";
        var vdOk = string.IsNullOrWhiteSpace(vd)
            || HotkeyParser.TryParseModifiers(vd, out _);
        // 迁移窗口热键: 空 = 禁用 (合法); 非空 = 只能是修饰键.
        var mv = MoveHotkeyBox?.Text ?? "";
        var mvOk = string.IsNullOrWhiteSpace(mv)
            || HotkeyParser.TryParseModifiers(mv, out _);
        // 迁卡片热键: 空 = 禁用 (合法); 非空 = 只能是修饰键.
        var mc = MigrateCardBox?.Text ?? "";
        var mcOk = string.IsNullOrWhiteSpace(mc)
            || HotkeyParser.TryParseModifiers(mc, out _);
        HotkeyBox.Background = ok
            ? System.Windows.Media.Brushes.Gray
            : System.Windows.Media.Brushes.IndianRed;
        if (SecondHotkeyBox is not null)
            SecondHotkeyBox.Background = secondOk
                ? System.Windows.Media.Brushes.Gray
                : System.Windows.Media.Brushes.IndianRed;
        if (VdHotkeyBox is not null)
            VdHotkeyBox.Background = vdOk
                ? System.Windows.Media.Brushes.Gray
                : System.Windows.Media.Brushes.IndianRed;
        if (MoveHotkeyBox is not null)
            MoveHotkeyBox.Background = mvOk
                ? System.Windows.Media.Brushes.Gray
                : System.Windows.Media.Brushes.IndianRed;
        if (MigrateCardBox is not null)
            MigrateCardBox.Background = mcOk
                ? System.Windows.Media.Brushes.Gray
                : System.Windows.Media.Brushes.IndianRed;
        SaveButton.IsEnabled = ok && secondOk && vdOk && mvOk && mcOk;
        StatusText.Text = !ok ? "主快捷键无效 (须含 Alt)"
            : !secondOk ? "第二快捷键无效 (须含 Alt 或留空)"
            : !vdOk ? "虚拟桌面热键无效 (只填修饰键, 如 Alt+Ctrl)"
            : !mvOk ? "迁移窗口热键无效 (只填修饰键, 如 Alt+Ctrl+Shift)"
            : !mcOk ? "迁卡片热键无效 (只填修饰键, 如 Alt+Ctrl)"
            : "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!HotkeyParser.TryParseKey(HotkeyBox.Text, out var vk)
            || !HotkeyParser.HasAlt(HotkeyBox.Text)) return;
        // 合并保存: Load 最新 (拿回 controller 维护的 Stacks) → 只覆盖
        // 本窗口编辑的字段 → 写回 → 热生效回调.
        var cfg = _store.Load();
        cfg.Hotkey = HotkeyBox.Text;
        cfg.SecondHotkey = string.IsNullOrWhiteSpace(SecondHotkeyBox.Text)
            ? "" : SecondHotkeyBox.Text;
        cfg.QuickJumpModifier = QuickModBox.SelectedIndex switch
        {
            1 => "Ctrl",
            2 => "Shift",
            3 => "Win",
            _ => "Alt",
        };
        cfg.VirtualDesktopHotkey = string.IsNullOrWhiteSpace(VdHotkeyBox.Text)
            ? "" : VdHotkeyBox.Text.Trim();
        cfg.GlobalTagsAcrossDesktops = TagScopeBox.SelectedIndex == 1;
        cfg.MoveWindowHotkey = string.IsNullOrWhiteSpace(MoveHotkeyBox.Text)
            ? "" : MoveHotkeyBox.Text.Trim();
        cfg.MigrateCardHotkey = string.IsNullOrWhiteSpace(MigrateCardBox.Text)
            ? "" : MigrateCardBox.Text.Trim();
        cfg.SuppressAltTab = SuppressBox.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => null,
        };
        cfg.AutoStart = AutoStartBox.IsChecked == true;
        cfg.CommitMinimizeOnSame = CommitMinBox.IsChecked == true;
        cfg.CommitMinimizePinnedOnSame = CommitMinPinnedBox.IsChecked == true;
        cfg.QuickMinimizeOnSame = QuickMinBox.IsChecked == true;
        cfg.QuickMinimizePinnedOnSame = QuickMinPinnedBox.IsChecked == true;
        cfg.SelectionGlow = GlowBox.IsChecked == true;
        // 注册表即时落盘 (cfg.Save 失败也不会回滚勾选状态 — 用户至少知道
        // 应用层记住了, 手动重勾可恢复).
        AutoStartService.Apply(cfg.AutoStart == true, AutoStartService.GetCurrentExePath());
        _store.Save(cfg);
        _onSaved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}


