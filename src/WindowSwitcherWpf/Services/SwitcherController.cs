using System;
using System.Collections.ObjectModel;
using System.Linq;
using WindowSwitcherWpf.Interop;
using WindowSwitcherWpf.Models;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Glue between the keyboard hook, the overlay, the live enumeration, and
/// persistence. Owns the active group list and selection index.
/// </summary>
public sealed class SwitcherController
{
    private readonly WindowEnumerator _enumerator;
    private readonly WindowGroupingService _grouping;
    private readonly OrderStore _store;
    private readonly ConfigStore? _configStore;
    private readonly Action<IntPtr> _activate;
    private OrderFile _order;
    private ConfigFile _config = new();
    private IReadOnlyList<SortRule>? _activeSortRules;

    public ObservableCollection<WindowGroup> Groups { get; private set; } = new();

    /// <summary>
    /// All windows in display order (per-window MRU / z-order). Card 0 is
    /// the special "most recent" slot: the window being left moves to the
    /// end so the first card is the most recent OTHER window — exactly the
    /// native Alt+Tab feel of 第一个界面是最近的标签.
    /// </summary>
    public IReadOnlyList<WindowEntry> Flat => _flat;
    private List<WindowEntry> _flat = new();

    /// <summary>Windows with their computed slots — the authoritative card
    /// list the overlay renders (slot order, then unmanaged leftovers).</summary>
    public IReadOnlyList<SlotWindow> Slotted { get; private set; } = new List<SlotWindow>();
    private IntPtr _lastForeground;
    public int ActiveFlatIndex { get; private set; }
    public int ActiveGroupIndex { get; private set; }
    public int ActiveWindowIndex { get; private set; }
    public bool HasWindows => _flat.Count > 0;
    public event Action? StateChanged;

    public SwitcherController(WindowEnumerator enumerator,
        WindowGroupingService grouping,
        OrderStore store,
        Action<IntPtr> activate,
        ConfigStore? configStore = null)
    {
        _enumerator = enumerator;
        _grouping = grouping;
        _store = store;
        _activate = activate;
        _order = store.Load();
        if (configStore is not null)
        {
            _configStore = configStore;
            _config = configStore.Load();
        }
    }

    /// <summary>Templates and sort methods from config.json (右上角 UI 的数据源).</summary>
    public ConfigFile Config => _config;

    public void OpenOverlay()
    {
        // Capture the window being left BEFORE the overlay takes focus —
        // it becomes the last card so card 0 is the most recent other one.
        var foreground = NativeMethods.GetForegroundWindow();
        _lastForeground = foreground;
        Refresh();
        RebuildFlat(foreground);
        ActiveFlatIndex = 0;
        ActiveGroupIndex = 0;
        ActiveWindowIndex = 0;
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"OpenOverlay groups={Groups.Count} flat={_flat.Count}");
        StateChanged?.Invoke();
    }

    private void RebuildFlat(IntPtr foreground)
    {
        var mru = Groups.SelectMany(g => g.Windows).ToList();
        var idx = mru.FindIndex(w => w.Hwnd == foreground);
        if (idx > 0)
        {
            var entry = mru[idx];
            mru.RemoveAt(idx);
            mru.Add(entry);
        }
        Slotted = AssignSlots(mru, _order.Pins, _activeSortRules);
        _flat = Slotted.Select(s => s.Entry).ToList();
    }

    /// <summary>Apply a template: match live windows to the template's slot
    /// entries 一一对应 and make them the ENTIRE pin set (replaces manual
    /// pins — 模板 is the highest tier). Returns matched count.</summary>
    public int ApplyTemplate(TemplateDefinition template)
    {
        var live = Groups.SelectMany(g => g.Windows).ToList();
        var newPins = new List<PinDefinition>();
        var taken = new HashSet<string>();
        foreach (var ts in template.Slots)
        {
            if (ts.Slot < 1 || ts.Slot > 10) continue;
            var match = live.Find(w =>
                string.Equals(w.GroupKey, ts.GroupKey, StringComparison.OrdinalIgnoreCase)
                && (ts.Title.Length == 0 || w.Title == ts.Title)
                && !taken.Contains(w.GroupKey + "\n" + w.Title));
            if (match is null) continue;
            taken.Add(match.GroupKey + "\n" + match.Title);
            newPins.Add(new PinDefinition(match.GroupKey, match.Title, ts.Slot));
        }
        _order.Pins = newPins;
        _store.SaveDebounced(_order);
        RebuildFlat(_lastForeground);
        KeepSelection();
        StateChanged?.Invoke();
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"template '{template.Name}' applied: {newPins.Count}/{template.Slots.Count} slots matched");
        return newPins.Count;
    }

    /// <summary>Set the active smart-sort method and reflow. Pins are never
    /// moved (第三 tier only orders the rest). Re-invoking re-applies, so
    /// after unpinning, clicking the method again sorts the freed window.</summary>
    public void ApplySmartSort(SortMethodDefinition method)
    {
        _activeSortRules = method.Rules;
        RebuildFlat(_lastForeground);
        KeepSelection();
        StateChanged?.Invoke();
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"smart sort '{method.Name}' applied rules={method.Rules.Count}");
    }

    /// <summary>
    /// The slot model (固定=固定数字): slot 0 is the special most-recent
    /// card. Persisted pins hard-claim their number; every other window
    /// flows into the remaining numbers 1..10 in MRU order. Display order
    /// is by slot number, so a deleted pinned neighbour leaves its number
    /// vacant but the row stays visually packed — and dragging an unpinned
    /// window to the left of a pinned one makes it "naturally become" the
    /// vacant number (MoveRelative).
    /// </summary>
    public static List<SlotWindow> AssignSlots(List<WindowEntry> mru, List<PinDefinition> pins,
        IReadOnlyList<SortRule>? sortRules = null)
    {
        var result = new List<SlotWindow>(mru.Count);
        if (mru.Count == 0) return result;

        result.Add(new SlotWindow(mru[0], 0, false));
        var rest = mru.Skip(1).ToList();

        // 1. Pins hard-claim their numbers (模板/手动固定 tier — both are pins;
        // smart sort NEVER moves them).
        var slotMap = new SortedDictionary<int, WindowEntry>();
        var pinnedKeys = new HashSet<(string, string)>();
        foreach (var pin in pins)
        {
            if (pin.Slot < 1 || pin.Slot > 10 || slotMap.ContainsKey(pin.Slot)) continue;
            var match = rest.Find(w => w.GroupKey == pin.GroupKey && w.Title == pin.Title);
            if (match is null) continue; // dormant pin, window not alive
            slotMap[pin.Slot] = match;
            pinnedKeys.Add((pin.GroupKey, pin.Title));
        }

        // 2. Unpinned windows: smart-sort rules reorder them before flow-fill
        //    (智能排序 tier — first matching rule wins, unmatched trail in MRU).
        IEnumerable<WindowEntry> ordered = rest;
        if (sortRules is { Count: > 0 })
        {
            ordered = rest.Select((w, i) => (w, i))
                .OrderBy(t => RuleIndex(t.w, sortRules))
                .ThenBy(t => t.i)
                .Select(t => t.w);
        }
        var free = new Queue<int>(Enumerable.Range(1, 10).Where(s => !slotMap.ContainsKey(s)));
        var overflow = new List<WindowEntry>();
        foreach (var w in ordered)
        {
            if (slotMap.Values.Contains(w)) continue; // already claimed by a pin
            if (free.Count > 0) slotMap[free.Dequeue()] = w;
            else overflow.Add(w);
        }

        foreach (var kv in slotMap)
            result.Add(new SlotWindow(kv.Value, kv.Key, pinnedKeys.Contains((kv.Value.GroupKey, kv.Value.Title))));
        foreach (var w in overflow)
            result.Add(new SlotWindow(w, -1, false));
        return result;
    }

    /// <summary>Index of the first smart-sort rule matching the window, or
    /// MaxValue when no rule does (they trail in MRU order).</summary>
    private static int RuleIndex(WindowEntry w, IReadOnlyList<SortRule> rules)
    {
        for (var i = 0; i < rules.Count; i++)
            if (RuleMatches(w, rules[i])) return i;
        return int.MaxValue;
    }

    /// <summary>Generic structural matching (通用过滤): process name contains,
    /// module path contains, title contains, or exact group key. No
    /// app-specific logic lives in code — only in the user's config.json.</summary>
    public static bool RuleMatches(WindowEntry w, SortRule r) => r.Type switch
    {
        "process" => w.ProcessName.Contains(r.Value, StringComparison.OrdinalIgnoreCase),
        "path" => w.ModulePath.Contains(r.Value, StringComparison.OrdinalIgnoreCase),
        "title" => w.Title.Contains(r.Value, StringComparison.OrdinalIgnoreCase),
        "group" => string.Equals(w.GroupKey, r.Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Pin/unpin: pinning freezes the window's current number (or
    /// assigns the lowest free one if it was unmanaged); unpinning lets it
    /// flow again. Persisted to order.json.</summary>
    public void TogglePin(IntPtr hwnd)
    {
        var sw = Slotted.FirstOrDefault(s => s.Entry.Hwnd == hwnd);
        if (sw is null || sw.Slot == 0) return;
        var pin = _order.Pins.FirstOrDefault(p =>
            p.GroupKey == sw.Entry.GroupKey && p.Title == sw.Entry.Title);
        if (pin is not null)
        {
            _order.Pins.Remove(pin);
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"unpin slot={pin.Slot} '{sw.Entry.Title}'");
        }
        else
        {
            var slot = sw.Slot is >= 1 and <= 10
                ? sw.Slot
                : Enumerable.Range(1, 10)
                    .FirstOrDefault(s => Slotted.All(x => x.Slot != s), -1);
            if (slot < 1) return; // no free slot to pin into
            _order.Pins.Add(new PinDefinition(sw.Entry.GroupKey, sw.Entry.Title, slot));
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"pin slot={slot} '{sw.Entry.Title}'");
        }
        _store.SaveDebounced(_order);
        RebuildFlat(_lastForeground);
        KeepSelection();
        StateChanged?.Invoke();
    }

    /// <summary>Drag reorder: insert a (draggable) window before or after a
    /// target. Pinned windows and slot 0 are never moved; unpinned slots
    /// renumber so the dragged window claims the vacant number, exactly the
    /// 把4拖到3的左边它自然变成了2 rule.</summary>
    public bool MoveRelative(IntPtr movingHwnd, IntPtr targetHwnd, bool before)
    {
        if (movingHwnd == targetHwnd) return false;
        var list = Slotted.ToList();
        var moving = list.FirstOrDefault(s => s.Entry.Hwnd == movingHwnd);
        var target = list.FirstOrDefault(s => s.Entry.Hwnd == targetHwnd);
        if (moving is null || target is null) return false;
        if (moving.Slot == 0 || moving.IsPinned) return false;

        list.Remove(moving);
        var ti = list.IndexOf(target);
        list.Insert(before ? ti : ti + 1, moving);

        var renumbered = RenumberFlow(list);
        Slotted = renumbered;
        _flat = renumbered.Select(s => s.Entry).ToList();
        KeepSelection();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Pure renumber pass over an ordered card list: slot 0 and
    /// pins keep their numbers, every other window flows into the lowest
    /// free number 1..10, extras go to the tail as unmanaged (-1).</summary>
    public static List<SlotWindow> RenumberFlow(List<SlotWindow> ordered)
    {
        var claimed = new Dictionary<int, WindowEntry>();
        foreach (var s in ordered)
            if (s.IsPinned && s.Slot is >= 1 and <= 10 && !claimed.ContainsKey(s.Slot))
                claimed[s.Slot] = s.Entry;
        var free = new Queue<int>(Enumerable.Range(1, 10).Where(s => !claimed.ContainsKey(s)));
        var result = new List<SlotWindow>(ordered.Count);
        var overflow = new List<SlotWindow>();
        foreach (var s in ordered)
        {
            if (s.Slot == 0 || s.IsPinned) { result.Add(s); continue; }
            if (free.Count > 0) result.Add(s with { Slot = free.Dequeue() });
            else overflow.Add(s);
        }
        foreach (var s in overflow) result.Add(s with { Slot = -1 });
        return result;
    }

    /// <summary>Re-points ActiveFlatIndex at the previously selected hwnd
    /// after the list composition changed (pin toggle / drag).</summary>
    private void KeepSelection()
    {
        var sel = _flat.Count == 0
            ? IntPtr.Zero
            : _flat[Math.Min(ActiveFlatIndex, _flat.Count - 1)].Hwnd;
        var i = _flat.FindIndex(w => w.Hwnd == sel);
        ActiveFlatIndex = i >= 0 ? i : 0;
    }

    public void Advance(bool reverse)
    {
        if (_flat.Count == 0) return;
        SelectFlat(reverse ? ActiveFlatIndex - 1 : ActiveFlatIndex + 1);
    }

    /// <summary>Flat card selection with wrap-around; used by Tab advance
    /// and by the arrow-key grid navigation in the overlay.</summary>
    public void SelectFlat(int index)
    {
        if (_flat.Count == 0) return;
        ActiveFlatIndex = ((index % _flat.Count) + _flat.Count) % _flat.Count;
        StateChanged?.Invoke();
    }

    /// <summary>导出: snapshot the current slot layout (slots 1..10) into
    /// config.json as a new template. Same-name templates are replaced.</summary>
    public int ExportCurrentAsTemplate(string name)
    {
        var slots = new List<TemplateSlot>();
        foreach (var sw in Slotted)
        {
            if (sw.Slot < 1 || sw.Slot > 10) continue;
            slots.Add(new TemplateSlot { Slot = sw.Slot, GroupKey = sw.Entry.GroupKey, Title = sw.Entry.Title });
        }
        _config.Templates.RemoveAll(t => t.Name == name);
        _config.Templates.Add(new TemplateDefinition { Name = name, Slots = slots });
        _configStore?.Save(_config);
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"exported template '{name}' slots={slots.Count}");
        return slots.Count;
    }

    public void SelectByDigit(int digit)
    {
        // Digits address SLOT numbers (Alt+3 → the card badged 3), not flat
        // positions — a vacant pin slot shifts the display order.
        for (var i = 0; i < Slotted.Count; i++)
        {
            if (Slotted[i].Slot == digit)
            {
                ActiveFlatIndex = i;
                StateChanged?.Invoke();
                return;
            }
        }
    }

    /// <summary>Makes <paramref name="hwnd"/> the committed-on-Enter target
    /// (mouse click path — the visual selection must match the controller).</summary>
    public bool SelectByHwnd(IntPtr hwnd)
    {
        for (var i = 0; i < _flat.Count; i++)
        {
            if (_flat[i].Hwnd != hwnd) continue;
            ActiveFlatIndex = i;
            StateChanged?.Invoke();
            return true;
        }
        return false;
    }

    public void Commit()
    {
        if (!HasWindows) return;
        var entry = _flat[Math.Min(ActiveFlatIndex, _flat.Count - 1)];
        WindowSwitcherWpf.Services.Log.Info("Controller", $"commit 0x{entry.Hwnd.ToInt64():X} '{entry.Title}'");
        _activate(entry.Hwnd);
    }

    public void Cancel() { WindowSwitcherWpf.Services.Log.Info("Controller", "cancel"); }

    public void ReloadOrder()
    {
        _order = _store.Load();
        Refresh();
        StateChanged?.Invoke();
    }

    public void PersistOrder()
    {
        _order.Groups = Groups.Select(g => new GroupDefinition(g.Key, g.DisplayName)).ToList();
        _store.SaveDebounced(_order);
    }

    public void MoveGroup(int from, int to)
    {
        if (from < 0 || from >= Groups.Count) return;
        to = Math.Clamp(to, 0, Groups.Count - 1);
        if (from == to) return;
        var item = Groups[from];
        Groups.Move(from, to);
        ActiveGroupIndex = Groups.IndexOf(item);
        if (ActiveGroupIndex < 0) ActiveGroupIndex = 0;
        StateChanged?.Invoke();
        PersistOrder();
    }

    private void Refresh()
    {
        try
        {
            var live = _enumerator.Enumerate(onlyCurrentDesktop: true);
            var grouped = _grouping.Group(live, _order);
            Groups = grouped;
            if (ActiveGroupIndex >= Groups.Count) ActiveGroupIndex = 0;
            if (ActiveWindowIndex >= (Groups.Count == 0 ? 0 : Groups[ActiveGroupIndex].Windows.Count))
                ActiveWindowIndex = 0;
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Controller", ex);
        }
    }
}
