using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WindowSwitcherWpf.Models;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf;

/// <summary>编辑 VM: 一个智能排序方法.</summary>
public sealed class SortMethodVM
{
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<SortRuleVM> Rules { get; set; } = new();
}

/// <summary>编辑 VM: 一条排序规则行.</summary>
public sealed class SortRuleVM
{
    public string Type { get; set; } = "process";
    public string Value { get; set; } = string.Empty;
}

/// <summary>编辑 VM: 模板 (改名/删除; Slots 原样携带 — 槽位由 overlay 导出生成).</summary>
public sealed class TemplateVM
{
    public string Name { get; set; } = string.Empty;
    public List<TemplateSlot> Slots { get; set; } = new();
}

/// <summary>
/// 设置窗口 (按需创建, 关闭即回收 — 空闲零常驻): 三块编辑设置的
/// 快捷键 / 智能排序 / 模板. 保存 = Load 最新 config → 只覆盖本窗口
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
        QuickModBox.SelectedIndex = (cfg.QuickJumpModifier ?? "Alt") switch
        {
            "Ctrl" => 1,
            "Shift" => 2,
            "Win" => 3,
            _ => 0,
        };
        SuppressBox.SelectedIndex = cfg.SuppressAltTab switch
        {
            null => 0,
            true => 1,
            false => 2,
        };
        SortMethodList.ItemsSource = new ObservableCollection<SortMethodVM>(
            cfg.SortMethods.Select(m => new SortMethodVM
            {
                Name = m.Name,
                Rules = new ObservableCollection<SortRuleVM>(
                    m.Rules.Select(r => new SortRuleVM { Type = r.Type, Value = r.Value })),
            }));
        TemplateList.ItemsSource = new ObservableCollection<TemplateVM>(
            cfg.Templates.Select(t => new TemplateVM { Name = t.Name, Slots = t.Slots }));
        ValidateHotkey();
    }

    private void OnHotkeyChanged(object sender, TextChangedEventArgs e)
    {
        ValidateHotkey();
    }

    private void ValidateHotkey()
    {
        if (HotkeyBox is null || SaveButton is null) return;
        var ok = HotkeyParser.TryParseKey(HotkeyBox.Text, out var vk)
                 && HotkeyParser.HasAlt(HotkeyBox.Text);
        HotkeyBox.Background = ok
            ? System.Windows.Media.Brushes.Gray
            : System.Windows.Media.Brushes.IndianRed;
        SaveButton.IsEnabled = ok;
        StatusText.Text = ok ? "" : "主快捷键无效 (须含 Alt)";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!HotkeyParser.TryParseKey(HotkeyBox.Text, out var vk)
            || !HotkeyParser.HasAlt(HotkeyBox.Text)) return;
        // 合并保存: Load 最新 (拿回 controller 维护的 Stacks) → 只覆盖
        // 本窗口编辑的字段 → 写回 → 热生效回调.
        var cfg = _store.Load();
        cfg.Hotkey = HotkeyBox.Text;
        cfg.QuickJumpModifier = QuickModBox.SelectedIndex switch
        {
            1 => "Ctrl",
            2 => "Shift",
            3 => "Win",
            _ => "Alt",
        };
        cfg.SuppressAltTab = SuppressBox.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => null,
        };
        cfg.SortMethods = SortMethodList.Items.OfType<SortMethodVM>()
            .Select(m => new SortMethodDefinition
            {
                Name = m.Name,
                Rules = m.Rules.Select(r => new SortRule
                {
                    Type = r.Type,
                    Value = r.Value,
                }).ToList(),
            }).ToList();
        cfg.Templates = TemplateList.Items.OfType<TemplateVM>()
            .Select(t => new TemplateDefinition { Name = t.Name, Slots = t.Slots })
            .ToList();
        _store.Save(cfg);
        _onSaved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnAddSortMethod(object sender, RoutedEventArgs e)
    {
        var list = (ObservableCollection<SortMethodVM>)SortMethodList.ItemsSource;
        list.Add(new SortMethodVM { Name = "新方法", Rules = new() });
    }

    private void OnDeleteSortMethod(object sender, RoutedEventArgs e)
    {
        if (SortMethodList.SelectedItem is SortMethodVM vm)
            ((ObservableCollection<SortMethodVM>)SortMethodList.ItemsSource).Remove(vm);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (SortMethodList.SelectedItem is SortMethodVM vm)
            vm.Rules.Add(new SortRuleVM());
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SortRuleVM rule
            && SortMethodList.SelectedItem is SortMethodVM vm)
            vm.Rules.Remove(rule);
    }

    private void OnRenameTemplate(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is TemplateVM vm
            && !string.IsNullOrWhiteSpace(TemplateNameBox.Text))
            vm.Name = TemplateNameBox.Text;
    }

    private void OnDeleteTemplate(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is TemplateVM vm)
            ((ObservableCollection<TemplateVM>)TemplateList.ItemsSource).Remove(vm);
    }
}
