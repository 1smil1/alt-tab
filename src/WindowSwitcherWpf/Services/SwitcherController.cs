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
    /// list the overlay renders (slot order, then unmanaged leftovers).
    /// Stack members (非首位) are NOT here: they are collapsed into their
    /// head card, and only surface in the stack sub view.</summary>
    public IReadOnlyList<SlotWindow> Slotted { get; private set; } = new List<SlotWindow>();
    private IntPtr _lastForeground;
    public int ActiveFlatIndex { get; private set; }
    public int ActiveGroupIndex { get; private set; }
    public int ActiveWindowIndex { get; private set; }
    public bool HasWindows => _flat.Count > 0;
    public event Action? StateChanged;

    /// <summary>A drag/move was rejected because it would displace a
    /// pinned slot (固定卡编号不可被挤走). Payload = hwnd of the pin that
    /// blocked the move (top-level card / stack head / pinned member) so
    /// the overlay can ding + flash its frame — the user sees WHY.</summary>
    public event Action<IntPtr>? MoveRejectedByPin;

    // ---------- 手动堆叠 (自定义集中/剥离) ----------
    // A stack is an ordered set of windows collapsed into ONE slot on the
    // main page: the head (Members[0]) carries the slot number and lends
    // the group color; the rest hide behind it as fan layers. Alt+digit on
    // a stack opens the 子标签页面 listing its members in relative order.

    /// <summary>A live stack: members in relative order (head first),
    /// resolved against alive windows during the last rebuild.</summary>
    public sealed class WindowStackInfo
    {
        public required string Id { get; init; }
        public required List<WindowEntry> Members { get; init; }
        /// <summary>Slot digit the stack card shows (the head's slot).</summary>
        public int Slot { get; set; } = -1;
        public WindowEntry Head => Members[0];
    }

    private readonly Dictionary<IntPtr, WindowStackInfo> _stacksByHead = new();
    private bool _inStackView;
    private string? _activeStackId;

    /// <summary>The active keyboard/click navigation list: the full flat
    /// card list normally, or the current stack's members while in the
    /// stack sub view (子标签页面里箭头/Alt+`只在堆叠成员间移动).</summary>
    public IReadOnlyList<WindowEntry> Nav => _inStackView && ActiveStackInfo is { } s ? s.Members : _flat;

    public bool IsInStackView => _inStackView;

    public WindowStackInfo? ActiveStackInfo =>
        _inStackView && _activeStackId is not null
            ? _stacksByHead.Values.FirstOrDefault(s => s.Id == _activeStackId)
            : null;

    /// <summary>Stack behind a card (head hwnd → info), null for lone cards.</summary>
    public WindowStackInfo? StackInfoFor(IntPtr hwnd) =>
        _stacksByHead.TryGetValue(hwnd, out var s) ? s : null;

    /// <summary>Heads of all currently-active stacks (self-test hook:
    /// --show-overlay --enter-stack N uses it to jump into a sub view).</summary>
    public List<IntPtr> StackHeads() => _stacksByHead.Keys.ToList();

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

    /// <summary>设置窗口保存后重载 config.json — 模板/排序下拉与热键字段
    /// 即时生效. Stacks 归 controller 独占维护, Load 拿回的就是最新.</summary>
    public void ReloadConfig()
    {
        if (_configStore is null) return;
        _config = _configStore.Load();
        WindowSwitcherWpf.Services.Log.Info("Controller", "config reloaded (settings saved)");
    }

    public void OpenOverlay()
    {
        // Capture the window being left BEFORE the overlay takes focus —
        // it becomes the last card so card 0 is the most recent other one.
        // A previous open's stack sub view never survives: every open
        // starts on the main page.
        _inStackView = false;
        _activeStackId = null;
        var foreground = NativeMethods.GetForegroundWindow();
        _lastForeground = foreground;
        Refresh();
        RebuildFlat(foreground);
        // 预选 0 号卡 (上个窗口) — Windows Alt+Tab 手感: 快速松开 = 切到
        // 上个窗口. 复制卡占 index 0, 所以预选 index 1.
        ActiveFlatIndex = _flat.Count > 1 ? 1 : 0;
        ActiveGroupIndex = 0;
        ActiveWindowIndex = 0;
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"OpenOverlay groups={Groups.Count} flat={_flat.Count} stacks={_stacksByHead.Count}");
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

        // Stack collapse (集中): resolve every configured stack against the
        // alive windows; the head (first alive member) keeps its place in
        // the MRU list — and therefore its slot — while the tail members
        // are pulled OUT of the list so their numbers free up for others.
        // Fewer than 2 alive members = the stack is dormant (散开).
        _stacksByHead.Clear();
        var consumed = new HashSet<WindowEntry>();
        foreach (var def in _config.Stacks)
        {
            var alive = new List<WindowEntry>();
            foreach (var m in def.Members)
            {
                var hit = mru.FirstOrDefault(w =>
                    !consumed.Contains(w)
                    && w.GroupKey == m.GroupKey && w.Title == m.Title);
                if (hit is null) continue;
                consumed.Add(hit);
                alive.Add(hit);
            }
            if (alive.Count < 2) continue;
            // Heal pins orphaned by a pre-StackId head change (固定规则跟着
            // 堆叠走): a pin anchored to a NON-first member is really the
            // stack's old pin (that member WAS the head before a reorder)
            // — retarget it to the alive head so it stops sitting dormant.
            var headW = alive[0];
            foreach (var p in _order.Pins)
            {
                var anchoredOld = alive.Skip(1).Any(m =>
                    p.GroupKey == m.GroupKey && p.Title == m.Title);
                if (!anchoredOld) continue;
                if (p.MemberTitle is not null
                    && alive.All(m => m.Title != p.MemberTitle)) continue;
                WindowSwitcherWpf.Services.Log.Info("Controller",
                    $"heal stale stack pin → head '{headW.Title}' (was '{p.Title}')");
                p.GroupKey = headW.GroupKey;
                p.Title = headW.Title;
            }
            foreach (var t in alive.Skip(1)) mru.Remove(t);
            _stacksByHead[alive[0].Hwnd] = new WindowStackInfo
            {
                Id = def.Id,
                Members = alive,
            };
        }

        var cards = AssignSlots(mru, _order.Pins, _activeSortRules, foreground);
        // 当前窗口卡 (Slot=-2 复制卡) 与 Slot 0 (上一个窗口) 由 AssignSlots
        // 统一插入 — 复制卡不参与数字寻址/导出/拖拽/cascade (SelectByDigit /
        // ExportCurrentAsTemplate / CanDrag 各自跳过), Slot 0 卡不可拖拽.
        Slotted = cards;
        _flat = Slotted.Select(s => s.Entry).ToList();
        // Diagnostics: duplicate hwnds in the flat list break the cascade
        // build (two head cards share one stack → orphaned sub cards).
        // 当前窗口复制卡 (-2) 与 slot0 显示副本 (0号是 pin 卡时) 与真卡
        // 同 hwnd 均为设计内的双卡 — 不算 dup.
        var dupGroups = Slotted.Where(s => !s.IsCurrentCopy && !s.IsSlot0Copy)
            .GroupBy(w => w.Entry.Hwnd).Where(g => g.Count() > 1).ToList();
        foreach (var g in dupGroups)
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"duplicate hwnd in flat: 0x{g.Key.ToInt64():X} x{g.Count()} '{g.First().Entry.Title}'");
        foreach (var sw in Slotted)
        {
            if (_stacksByHead.TryGetValue(sw.Entry.Hwnd, out var info))
                info.Slot = sw.Slot;
        }
    }

    // ---------- Stack sub view (子标签页面) ----------

    /// <summary>Alt+数字 on a stack card: switch the nav list to the stack's
    /// members; arrows / Alt+` / digits now move within the stack, and Alt
    /// release commits the selected member. Selection starts on the head.</summary>
    public bool EnterStackView(IntPtr headHwnd)
    {
        if (!_stacksByHead.TryGetValue(headHwnd, out var info)) return false;
        _inStackView = true;
        _activeStackId = info.Id;
        ActiveFlatIndex = 0; // head = first member
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"stack view enter '{info.Head.Title}' members={info.Members.Count}");
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Esc from the sub view (or peel leaving one member): back to
    /// the main page, selection re-pointed at the stack head.</summary>
    public void ExitStackView()
    {
        if (!_inStackView) return;
        var head = ActiveStackInfo?.Head.Hwnd;
        _inStackView = false;
        _activeStackId = null;
        if (head is IntPtr h)
        {
            var i = _flat.FindIndex(w => w.Hwnd == h);
            ActiveFlatIndex = i >= 0 ? i : 0;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Drop the sub view when its stack dissolved (e.g. the peel
    /// left one member): the caller rebuilds whatever view it needs.</summary>
    private void StackViewReset()
    {
        if (!_inStackView) return;
        _inStackView = false;
        _activeStackId = null;
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
        IReadOnlyList<SortRule>? sortRules = null, IntPtr foreground = default)
    {
        var result = new List<SlotWindow>(mru.Count);
        if (mru.Count == 0) return result;

        // 当前窗口卡 (Windows Alt+Tab 左起第一张 = 你刚离开的窗口): 恒占
        // index 0, Slot=-2 → badge 不显示数字. 真实卡保留在编号区 — pin 卡
        // 双卡并存 (Alt+数字直达真实卡 = 激活当前窗口, 无害).
        var fi = foreground != default ? mru.FindIndex(w => w.Hwnd == foreground) : -1;
        if (fi >= 0)
            result.Add(new SlotWindow(mru[fi], -2, false) { IsCurrentCopy = true });

        // Slot 0 = 上一个窗口 = MRU 序列里 fg 之外的第一个 (quick tap 松开
        // 的提交目标). fg==mru[0] 时它就是 mru[1] — 0 号恒存在.
        var slot0 = -1;
        for (var i = 0; i < mru.Count; i++)
            if (i != fi) { slot0 = i; break; }
        if (slot0 < 0) return result; // 屏幕上只有 fg 一个窗口

        // 上一窗口本身是 pin 卡时走复制机制 (用户: 标签3 固定则一直存在,
        // 除非它没有固定) — 0 号是显示副本, 真身留在编号区由 pin 认领,
        // 同 -2 的双卡并存. 未固定的上一窗口没有真身卡, 不算复制.
        var slot0Entry = mru[slot0];
        var slot0Pinned = pins.Any(p => p.MemberTitle is null
            && p.GroupKey == slot0Entry.GroupKey && p.Title == slot0Entry.Title);
        result.Add(new SlotWindow(slot0Entry, 0, false) { IsSlot0Copy = slot0Pinned });
        // 编号区候选 = mru 除 slot0 卡外的全部 (含 fg 真实卡 — pin 认领
        // 依赖它, 双卡并存的关键); slot0 窗口已 pin 时同样留在候选里
        // (真身). 不可拖拽: -2 复制卡 CanDrag 拒绝, Slot 0 卡 MoveRelative
        // 拒绝.
        var rest = mru.Where((w, i) => i != slot0 || slot0Pinned).ToList();

        // 1. Pins hard-claim their numbers (模板/手动固定 tier — both are pins;
        // smart sort NEVER moves them).
        var slotMap = new SortedDictionary<int, WindowEntry>();
        var pinnedKeys = new HashSet<(string, string)>();
        foreach (var pin in pins)
        {
            // Stack-member pins (MemberTitle != null) freeze a member's
            // position INSIDE its stack — they must not also claim a
            // top-level slot, or the head window gets matched twice and
            // shows up as two cards (one empty) in the flat list.
            if (pin.MemberTitle is not null) continue;
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
        // 同 hwnd 双卡 (复制机制): 优先取真身卡 — 0 号副本排在列表前部,
        // 否则点真身卡的 📌 会被下面的 Slot==0 挡掉 (unpin 失效).
        var sw = Slotted.FirstOrDefault(s => s.Entry.Hwnd == hwnd && !s.IsSlot0Copy)
              ?? Slotted.FirstOrDefault(s => s.Entry.Hwnd == hwnd);
        if (sw is null || sw.Slot == 0) return;
        var pin = _order.Pins.FirstOrDefault(p =>
            p.MemberTitle is null
            && p.GroupKey == sw.Entry.GroupKey && p.Title == sw.Entry.Title);
        if (pin is not null)
        {
            // Unpinning a stack head also releases its member pins — the
            // members' guarantee (主标签固定) dies with the stack pin.
            var headStackId = FindDef(_config.Stacks, sw.Entry)?.Id;
            _order.Pins.RemoveAll(p => p.MemberTitle is not null
                && (headStackId is not null
                    ? p.StackId == headStackId
                    : p.GroupKey == sw.Entry.GroupKey && p.Title == sw.Entry.Title));
            _order.Pins.Remove(pin);
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"unpin slot={pin.Slot} '{sw.Entry.Title}'");
        }
        else
        {
            if (!PinHeadCore(sw)) return; // no free slot to pin into
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"pin slot={sw.Slot} '{sw.Entry.Title}'");
        }
        _store.SaveDebounced(_order);
        RebuildFlat(_lastForeground);
        KeepSelection();
        StateChanged?.Invoke();
    }

    /// <summary>Pin-side of TogglePin: freeze this card's current number
    /// (lowest free one when unmanaged). Caller persists + reflows.</summary>
    private bool PinHeadCore(SlotWindow sw)
    {
        var slot = sw.Slot is >= 1 and <= 10
            ? sw.Slot
            : Enumerable.Range(1, 10)
                .FirstOrDefault(s => Slotted.All(x => x.Slot != s), -1);
        if (slot < 1) return false;
        _order.Pins.Add(new PinDefinition(sw.Entry.GroupKey, sw.Entry.Title, slot));
        return true;
    }

    public bool IsStackPinned(IntPtr headHwnd) =>
        // 同 hwnd 双卡: 只有真身卡带 IsPinned (0 号副本恒 false) — Any 即真身.
        Slotted.Any(s => s.Entry.Hwnd == headHwnd && s.IsPinned);

    // ---------- Stack-member pins (子标签固定; 固定成员联动主标签) ----------
    // Constraint from the spec: 主标签只有固定了，子标签才能固定; pinning
    // any member pins the whole stack first (联动). Identity = the head's
    // (GroupKey, Title) + the member's title; the frozen in-stack position
    // rides in Slot (1-based).

    private WindowStackInfo? StackOfMember(IntPtr hwnd) =>
        _stacksByHead.Values.FirstOrDefault(s => s.Members.Any(m => m.Hwnd == hwnd));

    /// <summary>Member-pin stack identity: StackId when present (新数据),
    /// GroupKey + head-Title fallback (旧 order.json). 成员重排会换 head —
    /// 旧锚定在 head 换人后失配, so new member pins record the stack Id.</summary>
    private static bool MemberPinOnStack(PinDefinition p, string stackId,
        string headGroupKey, string headTitle) =>
        p.StackId is not null ? p.StackId == stackId
            : p.GroupKey == headGroupKey && p.Title == headTitle;

    /// <summary>True when this window is a pinned member of a stack.</summary>
    public bool IsMemberPinned(IntPtr memberHwnd)
    {
        if (StackOfMember(memberHwnd) is not { } st) return false;
        var member = st.Members.FirstOrDefault(m => m.Hwnd == memberHwnd);
        if (member is null) return false;
        var head = st.Head;
        return _order.Pins.Any(p => p.MemberTitle == member.Title
            && MemberPinOnStack(p, st.Id, head.GroupKey, head.Title));
    }

    /// <summary>Pin/unpin a stack member (子标签固定). Pinning auto-pins the
    /// parent stack first (联动); unpinning leaves the stack pin alone
    /// (解除子固定不动主固定). Frozen position = current member index.</summary>
    public bool ToggleMemberPin(IntPtr memberHwnd)
    {
        if (StackOfMember(memberHwnd) is not { } st)
        {
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"toggle member pin: no stack contains 0x{memberHwnd.ToInt64():X}");
            return false;
        }
        var head = st.Head;
        var member = st.Members.FirstOrDefault(m => m.Hwnd == memberHwnd);
        if (member is null) return false;
        var pin = _order.Pins.FirstOrDefault(p => p.MemberTitle is not null
            && p.MemberTitle == member.Title
            && MemberPinOnStack(p, st.Id, head.GroupKey, head.Title));
        if (pin is not null)
        {
            _order.Pins.Remove(pin);
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"unpin member '{member.Title}' of '{head.Title}'");
        }
        else
        {
            // 联动: a member pin requires the stack pin — create it first.
            var headSw = Slotted.FirstOrDefault(s => s.Entry.Hwnd == head.Hwnd);
            if (headSw is null)
            {
                WindowSwitcherWpf.Services.Log.Info("Controller",
                    $"toggle member pin: head 0x{head.Hwnd.ToInt64():X} '{head.Title}' not in Slotted");
                return false;
            }
            if (!headSw.IsPinned && !PinHeadCore(headSw))
            {
                WindowSwitcherWpf.Services.Log.Info("Controller",
                    $"toggle member pin: no free slot 1-10 to auto-pin the head " +
                    $"(grid full, head slot={headSw.Slot})");
                return false;
            }
            var slot = st.Members.IndexOf(member) + 1; // 1-based frozen position
            _order.Pins.Add(new PinDefinition(head.GroupKey, head.Title, slot)
            {
                MemberTitle = member.Title,
                StackId = st.Id,
            });
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"pin member '{member.Title}' pos={slot} of '{head.Title}'");
        }
        _store.SaveDebounced(_order);
        RebuildFlat(_lastForeground);
        KeepSelection();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Member-pin anchor guard (子标签同理): simulate a same-stack
    /// move (moveDi set) or a pure insert of one flowing member (moveDi
    /// null) and verify every pinned member still anchors its frozen
    /// position — no flowing member numbered ≥ the pin may sit on its left
    /// after the monotonic fill.</summary>
    public bool MemberPinAllowsInsert(StackDefinition def, int? moveDi, int at)
    {
        var members = def.Members.ToList();
        if (moveDi is { } di)
        {
            if (di == at || di == at - 1) return true; // no-op move
            var m = members[di];
            members.RemoveAt(di);
            members.Insert(at > di ? at - 1 : at, m);
        }
        else
        {
            members.Insert(Math.Clamp(at, 0, members.Count),
                new StackMemberDef { GroupKey = "?", Title = "?" }); // synthetic flowing member
        }
        var head = def.Members[0];
        var pins = _order.Pins.Where(p => p.MemberTitle is not null
            && MemberPinOnStack(p, def.Id, head.GroupKey, head.Title)).ToList();
        if (pins.Count == 0) return true;
        var numbers = MemberMonotonicNumbers(members, pins);
        for (var i = 0; i < members.Count; i++)
        {
            var pin = pins.FirstOrDefault(p => p.MemberTitle == members[i].Title);
            if (pin is null) continue; // pinned members keep their own number
            for (var j = 0; j < i; j++)
                if (numbers[j] >= pin.Slot
                    && pins.All(p => p.MemberTitle != members[j].Title))
                    return false; // flowing member squeezed ahead of the pin
        }
        return true;
    }

    /// <summary>Monotonic in-stack numbering: pinned members keep their
    /// frozen slot, flowing members take the next free number, gaps stay
    /// vacant (-1 = overflow). Mirrors <see cref="RenumberFlow"/> scoped to
    /// stack members.</summary>
    public static List<int> MemberMonotonicNumbers(List<StackMemberDef> ordered,
        List<PinDefinition> pins)
    {
        var pinSlots = pins.Select(p => p.Slot).Where(s => s is >= 1 and <= 10).ToHashSet();
        var result = new List<int>(ordered.Count);
        var next = 1;
        foreach (var m in ordered)
        {
            var pin = pins.FirstOrDefault(p => p.MemberTitle == m.Title);
            if (pin is not null && pin.Slot is >= 1 and <= 10)
            {
                result.Add(pin.Slot);
                next = Math.Max(next, pin.Slot + 1);
            }
            else
            {
                while (next <= 10 && pinSlots.Contains(next)) next++;
                result.Add(next <= 10 ? next++ : -1);
            }
        }
        return result;
    }

    /// <summary>解散堆叠: remove the whole stack definition — every member
    /// becomes a lone card again (the head's ✕ on a stacked card). Windows
    /// are NOT closed.</summary>
    public bool DissolveStack(IntPtr headHwnd)
    {
        var entry = EntryByHwnd(headHwnd);
        if (entry is null) return false;
        var def = FindDef(_config.Stacks, entry);
        if (def is null) return false;
        _config.Stacks.Remove(def);
        SaveStacksAndReflow();
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"stack dissolved head='{entry.Title}' members={def.Members.Count}");
        return true;
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
        if (!PinsAnchored(renumbered))
        {
            var violator = AnchorViolatorHwnd(renumbered);
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"move rejected by pin hwnd=0x{violator.ToInt64():X} (move 0x{movingHwnd.ToInt64():X} rel 0x{targetHwnd.ToInt64():X})");
            MoveRejectedByPin?.Invoke(violator);
            return false; // 固定卡编号不可被挤走
        }
        Slotted = renumbered;
        _flat = renumbered.Select(s => s.Entry).ToList();
        KeepSelection();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Pure renumber pass over an ordered card list (单调编号):
    /// slot 0 and pins keep their numbers; every other window takes the
    /// smallest free number ≥ (previous+1), skipping pin-claimed slots —
    /// badges stay strictly increasing along the row, and numbers left
    /// behind (e.g. a card dragged past a pin: 2 → 4, 4 → 5) stay VACANT
    /// instead of backfilling. Deleting a card before a pin shifts the pin
    /// left while its frozen number holds (3号不因前移变2).</summary>
    public static List<SlotWindow> RenumberFlow(List<SlotWindow> ordered)
    {
        var pinSlots = new HashSet<int>();
        foreach (var s in ordered)
            if (s.IsPinned && s.Slot is >= 1 and <= 10)
                pinSlots.Add(s.Slot);
        var result = new List<SlotWindow>(ordered.Count);
        var overflow = new List<SlotWindow>();
        var next = 1;
        foreach (var s in ordered)
        {
            if (s.IsCurrentCopy) { result.Add(s); continue; } // 当前窗口卡: Slot=-2 恒不变
            if (s.Slot == 0 || s.IsPinned)
            {
                result.Add(s);
                if (s.IsPinned && s.Slot is >= 1 and <= 10)
                    next = Math.Max(next, s.Slot + 1); // never re-issue behind a pin
                continue;
            }
            while (next <= 10 && pinSlots.Contains(next)) next++;
            if (next <= 10) result.Add(s with { Slot = next++ });
            else overflow.Add(s);
        }
        foreach (var s in overflow) result.Add(s with { Slot = -1 });
        return result;
    }

    /// <summary>Pin anchor rule (固定=相对位置不变): every number below a
    /// pinned card's frozen slot must sit to its LEFT. A drop that lands a
    /// flowing card numbered ≥ the pin's ahead of it would force the pin to
    /// renumber (3号变4号) — that move is rejected by MoveRelative.</summary>
    public static bool PinsAnchored(List<SlotWindow> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var pin = ordered[i];
            if (!pin.IsPinned || pin.Slot is < 1 or > 10) continue;
            for (var j = 0; j < i; j++)
            {
                var left = ordered[j];
                if (left.Slot == 0 || left.IsPinned) continue; // slot 0 = number 0, always leftmost
                if (left.Slot >= pin.Slot) return false;
            }
        }
        return true;
    }

    /// <summary>First pinned card whose anchor rule <paramref name="ordered"/>
    /// violates — companion to <see cref="PinsAnchored"/>, used to point the
    /// rejection feedback at the exact pin that blocked the move.</summary>
    public static IntPtr AnchorViolatorHwnd(List<SlotWindow> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var pin = ordered[i];
            if (!pin.IsPinned || pin.Slot is < 1 or > 10) continue;
            for (var j = 0; j < i; j++)
            {
                var left = ordered[j];
                if (left.Slot == 0 || left.IsPinned) continue;
                if (left.Slot >= pin.Slot) return pin.Entry.Hwnd;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Hwnd of a stack definition's head entry (for feedback that
    /// targets a whole pinned stack), Zero if not currently alive.</summary>
    private IntPtr StackHeadHwnd(StackDefinition def)
    {
        // def.Members 含已关闭的窗口 — def.Members[0] 不一定是活 head,
        // 必须查 _stacksByHead (RebuildFlat 按活窗口解析出的 head)。
        foreach (var kv in _stacksByHead)
            if (kv.Value.Id == def.Id) return kv.Key;
        return IntPtr.Zero;
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
    /// and by the arrow-key grid navigation in the overlay. Wraps within
    /// the ACTIVE nav list (full cards, or stack members in the sub view).</summary>
    public void SelectFlat(int index)
    {
        var n = Nav.Count;
        if (n == 0) return;
        ActiveFlatIndex = ((index % n) + n) % n;
        StateChanged?.Invoke();
    }

    /// <summary>导出: snapshot the current slot layout (slots 1..10) into
    /// config.json as a new template. Same-name templates are replaced.</summary>
    public int ExportCurrentAsTemplate(string name)
    {
        var slots = new List<TemplateSlot>();
        foreach (var sw in Slotted)
        {
            if (sw.IsCurrentCopy) continue; // 复制卡不进模板
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

    public bool SelectByDigit(int digit)
    {
        // Digits address SLOT numbers (Alt+3 → the card badged 3), not flat
        // positions — a vacant pin slot shifts the display order. Inside the
        // stack sub view they switch to RELATIVE member index instead
        // (相对标签数字不需要显示 但有 — hidden but functional).
        if (_inStackView)
        {
            var members = ActiveStackInfo?.Members;
            if (members is null) return false;
            var i = digit == 0 ? 9 : digit - 1;
            if (i >= members.Count) return false;
            ActiveFlatIndex = i;
            StateChanged?.Invoke();
            return true;
        }
        for (var i = 0; i < Slotted.Count; i++)
        {
            if (Slotted[i].IsCurrentCopy) continue; // 复制卡不被数字寻址
            if (Slotted[i].Slot == digit)
            {
                ActiveFlatIndex = i;
                StateChanged?.Invoke();
                return true;
            }
        }
        return false;
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
        var entry = Nav[Math.Min(ActiveFlatIndex, Nav.Count - 1)];
        // The sub view never survives the commit — the next open starts
        // on the main page.
        StackViewReset();
        WindowSwitcherWpf.Services.Log.Info("Controller", $"commit 0x{entry.Hwnd.ToInt64():X} '{entry.Title}'");
        _activate(entry.Hwnd);
    }

    /// <summary>一次直达前调: Slotted 常驻态是陈旧的 (只在 overlay 打开时
    /// 重建) — 先枚举一次活窗口, 编号→窗口映射才可信.</summary>
    public void PrepareQuickJump()
    {
        var fg = NativeMethods.GetForegroundWindow();
        _lastForeground = fg;
        Refresh();
        RebuildFlat(fg);
    }

    /// <summary>一次直达: quick+数字 的目标 hwnd — 该编号卡的窗口; 卡是
    /// 堆叠头时返回 head hwnd (按住才进子页面, 快速松开=跳 head).
    /// 复制卡不参与寻址. 0x0 = 无此编号.</summary>
    public IntPtr SlotCardHwnd(int digit)
    {
        for (var i = 0; i < Slotted.Count; i++)
        {
            if (Slotted[i].IsCurrentCopy) continue;
            if (Slotted[i].Slot == digit) return Slotted[i].Entry.Hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>Activate a window directly (quick jump path — no overlay,
    /// no selection bookkeeping). 目标已是前台时改为最小化 (用户: 本来就是
    /// 顶层, 再按就是缩小化 — 即目标窗口同 HWND 也出现在标签 -2 复制卡上
    /// 时, 再按一次收起); 已最小化却仍是前台的边缘态走激活 = 恢复.</summary>
    public void QuickActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (hwnd == NativeMethods.GetForegroundWindow() && !NativeMethods.IsIconic(hwnd))
        {
            // ShowWindowAsync: 控制台类宿主窗口同步 ShowWindow 可能阻塞钩子链.
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MINIMIZE);
            WindowSwitcherWpf.Services.Log.Info("Controller",
                $"quick jump toggle → minimize 0x{hwnd.ToInt64():X}");
            return;
        }
        var entry = _flat.Find(w => w.Hwnd == hwnd);
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"quick jump 0x{hwnd.ToInt64():X} '{entry?.Title}'");
        _activate(hwnd);
    }

    /// <summary>诊断: 按假想前台重建 Slotted 并逐卡打印 (复制卡插入 /
    /// badge / 堆叠折叠) — 不动 UI. --dump-slotted [0xHWND].</summary>
    public IEnumerable<string> DumpSlottedLines(IntPtr pretendForeground)
    {
        Refresh();
        RebuildFlat(pretendForeground);
        for (var i = 0; i < Slotted.Count; i++)
        {
            var sw = Slotted[i];
            yield return $"i={i} copy={sw.IsCurrentCopy} s0={sw.IsSlot0Copy} slot={sw.Slot} " +
                $"pin={sw.IsPinned} hwnd=0x{sw.Entry.Hwnd.ToInt64():X} " +
                $"'{sw.Entry.Title}'";
        }
    }

    public void Cancel()
    {
        StackViewReset();
        WindowSwitcherWpf.Services.Log.Info("Controller", "cancel");
    }

    // ---------- 堆叠编辑 (拖拽集中 / 剥离 / 换序) ----------

    /// <summary>Unified drop onto a card (拖到卡上): same stack → reorder
    /// members; lone card → joins/creates a stack; a dropped stack head
    /// carries its whole stack. <paramref name="before"/> from the drop
    /// position. Persists and reflots the view.</summary>
    public bool StackDrop(IntPtr dragHwnd, IntPtr targetHwnd, bool before)
    {
        var drag = EntryByHwnd(dragHwnd);
        var target = EntryByHwnd(targetHwnd);
        if (drag is null || target is null) return false;
        var dragDef = FindDef(_config.Stacks, drag);
        var targetDef = FindDef(_config.Stacks, target);
        // 固定成员不可被拖动 (member pins are immovable).
        if (dragDef is not null && IsMemberPinned(dragHwnd))
        {
            MoveRejectedByPin?.Invoke(dragHwnd);
            return false;
        }
        if (dragDef is not null && ReferenceEquals(dragDef, targetDef))
        {
            // Same-stack reorder: member-pin anchor guard (子标签同理).
            var di = dragDef.Members.FindIndex(m => SameId(m, drag));
            var ti = dragDef.Members.FindIndex(m => SameId(m, target));
            if (!MemberPinAllowsInsert(dragDef, di, ti + (before ? 0 : 1)))
            {
                MoveRejectedByPin?.Invoke(StackHeadHwnd(dragDef));
                return false;
            }
        }
        else if (targetDef is not null)
        {
            // Cross-stack insert: the payload lands at a member boundary —
            // guard the pinned members' frozen positions around it.
            var ti = targetDef.Members.FindIndex(m => SameId(m, target));
            if (!MemberPinAllowsInsert(targetDef, null, ti + (before ? 0 : 1)))
            {
                MoveRejectedByPin?.Invoke(StackHeadHwnd(targetDef));
                return false;
            }
        }
        if (!StackDropCore(_config.Stacks, drag, target, before)) return false;
        // head 变化后的 pin 重锚由 RebuildFlat 的 heal 统一处理 (它基于
        // 活窗口, 每次重建自动纠偏) — 这里不再单独迁移。
        SaveStacksAndReflow();
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"stack drop '{drag.Title}' → '{target.Title}' before={before}");
        return true;
    }

    /// <summary>剥离: pull one member out of its stack. The last two
    /// members dissolving leaves the survivor as a normal lone card.</summary>
    public bool PeelFromStack(IntPtr memberHwnd)
    {
        var entry = EntryByHwnd(memberHwnd);
        if (entry is null) return false;
        if (!PeelCore(_config.Stacks, entry)) return false;
        SaveStacksAndReflow();
        WindowSwitcherWpf.Services.Log.Info("Controller",
            $"peel '{entry.Title}' from stack");
        return true;
    }

    /// <summary>Persist stack defs, rebuild the card/stack map, and keep
    /// the sub view pointed at the same stack id when it still exists
    /// (its member list may have changed under it).</summary>
    private void SaveStacksAndReflow()
    {
        var wasId = _inStackView ? _activeStackId : null;
        StackViewReset(); // KeepSelection works on the flat list
        _configStore?.Save(_config);
        RebuildFlat(_lastForeground);
        KeepSelection();
        // heal (RebuildFlat 内) 可能改写了 pin 锚点 — 重新入队保存,
        // 否则 heal 结果要到下一次变更才落盘 (期间崩溃则丢失).
        _store.SaveDebounced(_order);
        if (wasId is not null && _stacksByHead.Values.Any(s => s.Id == wasId))
        {
            _inStackView = true;
            _activeStackId = wasId;
            ActiveFlatIndex = 0; // selection = head inside the member list
        }
        StateChanged?.Invoke();
    }

    private WindowEntry? EntryByHwnd(IntPtr hwnd) =>
        Groups.SelectMany(g => g.Windows).FirstOrDefault(w => w.Hwnd == hwnd);

    private static bool SameId(StackMemberDef m, WindowEntry w) =>
        m.GroupKey == w.GroupKey && m.Title == w.Title;

    private static StackMemberDef MemberOf(WindowEntry w) =>
        new() { GroupKey = w.GroupKey, Title = w.Title };

    private static StackDefinition? FindDef(List<StackDefinition> defs, WindowEntry w) =>
        defs.FirstOrDefault(d => d.Members.Any(m => SameId(m, w)));

    /// <summary>Pure drop core (unit-tested via --test-slots style CLI).
        /// Drop rules: same stack → member reorder; lone+lone → new stack
        /// (head = the TARGET — it was there first, the dragged card lands
        /// on it); lone → target's stack at the target member's position;
        /// dropped stack head → the whole stack splices in. Returns false
        /// on no-op drops.</summary>
    public static bool StackDropCore(List<StackDefinition> defs, WindowEntry drag, WindowEntry target, bool before)
    {
        if (SameId(MemberOf(drag), target) || drag.Hwnd == target.Hwnd) return false;
        var dragDef = FindDef(defs, drag);
        var targetDef = FindDef(defs, target);
        var sameStack = dragDef is not null && ReferenceEquals(dragDef, targetDef);
        var dragIsHead = dragDef is not null && SameId(dragDef.Members[0], drag);

        if (sameStack)
        {
            // Reorder inside one stack. The head already sits first; only
            // non-head members actually move.
            if (dragIsHead) return false;
            var def = dragDef!;
            var di = def.Members.FindIndex(m => SameId(m, drag));
            var ti = def.Members.FindIndex(m => SameId(m, target));
            var at = ti + (before ? 0 : 1);
            if (at == di || at == di + 1) return false;
            var m = def.Members[di];
            def.Members.RemoveAt(di);
            def.Members.Insert(at > di ? at - 1 : at, m);
            return true;
        }

        // Carry the payload: a dropped head carries its WHOLE stack; a
        // non-head member detaches alone (and may dissolve its origin).
        List<StackMemberDef> payload;
        string movingId;
        if (dragDef is not null && dragIsHead)
        {
            payload = dragDef.Members.ToList();
            movingId = dragDef.Id;
            defs.Remove(dragDef);
        }
        else
        {
            payload = new List<StackMemberDef> { MemberOf(drag) };
            movingId = Guid.NewGuid().ToString("N");
            if (dragDef is not null)
            {
                dragDef.Members.RemoveAll(m => SameId(m, drag));
                if (dragDef.Members.Count <= 1) defs.Remove(dragDef); // 散开
            }
        }

        if (targetDef is null)
        {
            // Lone target: target joins the payload — head = target when
            // dropped after (中心/右半: 叠在上面), head = dragged when before.
            var merged = before
                ? new List<StackMemberDef>(payload) { MemberOf(target) }
                : new List<StackMemberDef> { MemberOf(target) }.Concat(payload).ToList();
            defs.Add(new StackDefinition { Id = movingId, Members = merged });
            return true;
        }

        var targetIdx = targetDef.Members.FindIndex(m => SameId(m, target));
        targetDef.Members.InsertRange(targetIdx + (before ? 0 : 1), payload);
        return true;
    }

    /// <summary>Pure peel core: remove one member; a def left with fewer
    /// than 2 members dissolves (剩下的那个回到普通卡).</summary>
    public static bool PeelCore(List<StackDefinition> defs, WindowEntry w)
    {
        var def = FindDef(defs, w);
        if (def is null) return false;
        def.Members.RemoveAll(m => SameId(m, w));
        if (def.Members.Count <= 1) defs.Remove(def);
        return true;
    }

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
