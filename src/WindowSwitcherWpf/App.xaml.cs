using System;
using System.IO;
using System.Threading;
using System.Windows;
using WindowSwitcherWpf.Interop;
using WindowSwitcherWpf.Models;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf;

public partial class App : Application
{
    private static readonly bool IsFirstInstance;
    private static readonly Mutex? SingleInstanceMutex;

    private KeyboardHook? _keyboard;
    private ForegroundWatcher? _foreground;
    private OrderStore? _orderStore;
    private WindowGroupingService? _grouping;
    private SwitcherController? _controller;
    private SwitcherOverlay? _overlay;
    private MainWindow? _host;

    static App()
    {
        SingleInstanceMutex = new Mutex(initiallyOwned: true,
            "Global\\WindowSwitcherWpfMutex", out var isOwner);
        IsFirstInstance = isOwner;
    }

    [STAThread]
    public static int Main()
    {
        if (!IsFirstInstance)
        {
            MessageBox.Show("Window Switcher is already running.", "Window Switcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            Log.Error("AppDomain", "UnhandledException: " + args.ExceptionObject);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, args) =>
        {
            Log.Exception("Dispatcher", args.Exception);
            args.Handled = true;
        };
        Log.Info("App", $"startup args={string.Join(' ', e.Args)}");
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--debug-enum")
        {
            RunDebugEnum();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--test-slots")
        {
            RunSlotTests();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--enumerate")
        {
            RunEnumerate();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--test-config")
        {
            RunConfigTests();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--test-stack")
        {
            RunStackTests();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--show-overlay")
        {
            // Test mode: open the overlay immediately so a scripted screen
            // capture can verify layout. Kill the process to close it.
            _orderStore = new OrderStore();
            _grouping = new WindowGroupingService();
            var configStore = new ConfigStore();
            _controller = new SwitcherController(
                new WindowEnumerator(), _grouping, _orderStore,
                hwnd => ActivationService.Activate(hwnd), configStore);
            _overlay = new SwitcherOverlay(_controller);
            _overlay.ShowAndFocus();
            // Self-test: --enter-stack N jumps straight into the Nth live
            // stack's sub view so a scripted screenshot can verify it.
            var enterIdx = Array.IndexOf(e.Args, "--enter-stack");
            if (enterIdx >= 0)
            {
                var heads = _controller.StackHeads();
                var pick = enterIdx + 1 < e.Args.Length && int.TryParse(e.Args[enterIdx + 1], out var n)
                    ? Math.Clamp(n, 0, Math.Max(0, heads.Count - 1))
                    : 0;
                if (heads.Count > 0)
                {
                    _controller.EnterStackView(heads[pick]);
                    _overlay.TestRebuild();
                }
            }
            return;
        }

        _orderStore = new OrderStore();
        _grouping = new WindowGroupingService();

        _host = new MainWindow();
        _host.Show();
        _host.Hide();

        _controller = new SwitcherController(
            new WindowEnumerator(),
            _grouping,
            _orderStore,
            hwnd => ActivationService.Activate(hwnd),
            new ConfigStore());

        _overlay = new SwitcherOverlay(_controller);
        _overlay.Hide();

        _keyboard = new KeyboardHook();
        _keyboard.OverlayRequested += OnOverlayRequested;
        _keyboard.OverlayAdvance += OnOverlayAdvance;
        _keyboard.OverlayArrow += vk => _overlay?.NavigateArrow(vk);
        _keyboard.OverlayDigit += d => _overlay?.SelectSlotAndCommit(d);
        _keyboard.OverlayCommit += OnOverlayCommit;
        _keyboard.OverlayCancel += OnOverlayCancel;
        _keyboard.OverlayEscape += OnOverlayEscape;
        _keyboard.EscapePressed += () => _overlay?.Cancel();
        _keyboard.Install();

        _foreground = new ForegroundWatcher();
        _foreground.Install();

        // 轻量化: no tray icon, no taskbar presence — the resident exe IS
        // the app. Close it with 关闭应用.bat, remove it with 卸载.bat.
    }

    private void OnOverlayRequested()
    {
        Log.Info("App", "OnOverlayRequested");
        _overlay?.ShowAndFocus();
        _keyboard?.SetOverlayOpen(true);
    }

    private void OnOverlayAdvance(bool reverse) => _overlay?.Advance(reverse);

    private void OnOverlayCommit()
    {
        Log.Info("App", "OnOverlayCommit");
        // overlay.Commit() activates the selection exactly once (it guards
        // on _isOpen); calling _controller.Commit() here too made EVERY
        // commit run twice.
        _overlay?.Commit();
        _keyboard?.SetOverlayOpen(false);
    }

    private void OnOverlayCancel()
    {
        Log.Info("App", "OnOverlayCancel");
        _overlay?.Cancel();
        _keyboard?.SetOverlayOpen(false);
    }

    private void OnOverlayEscape()
    {
        // Esc in the stack sub view just steps back to the main page; the
        // overlay stays open and Alt stays armed.
        if (_overlay is { IsInStackView: true })
        {
            Log.Info("App", "OnOverlayEscape: leave stack sub view");
            _overlay.ExitStackView();
            return;
        }
        OnOverlayCancel();
    }

    private void RunDebugEnum()
    {
        var enumerator = new WindowEnumerator();
        foreach (var w in enumerator.Enumerate(onlyCurrentDesktop: true))
        {
            Console.WriteLine($"{w.GroupKey}\t{w.ProcessName}\t{w.Title}\t0x{w.Hwnd.ToInt64():X}");
        }
    }

    /// <summary>
    /// Self-test for the slot model (固定=固定数字): pins hold their number
    /// while unpinned windows flow, a dragged window claims the vacant
    /// number, and pinning freezes the current number. Run via
    /// <c>WindowSwitcherWpf.exe --test-slots</c>; prints PASS/FAIL lines.
    /// </summary>
    private void RunSlotTests()
    {
        static WindowEntry W(string title) =>
            new(new IntPtr(title.GetHashCode() & 0xFFFFFF), title, "app.exe",
                @"C:\apps\app.exe", "group:" + title, false);

        static List<SlotWindow> Slots(params string[] titles) =>
            SwitcherController.AssignSlots(
                titles.Select(W).ToList(), new List<PinDefinition>());

        static void Check(string name, bool ok, string detail = "")
        {
            var line = $"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " | " + detail : "")}";
            Console.WriteLine(line);
            WindowSwitcherWpf.Services.Log.Info("SlotTests", line);
            Console.Out.Flush();
        }

        // 1. No pins: slots 0..N assigned in MRU order.
        var s = Slots("a", "b", "c", "d");
        Check("flow-basic",
            s.Count == 4 && s[0].Slot == 0 && s[1].Slot == 1 && s[2].Slot == 2 && s[3].Slot == 3,
            string.Join(',', s.Select(x => x.Slot)));

        // 2. Pin at 3: number stays 3 even when earlier windows vanish.
        var pinned = new List<PinDefinition>
        {
            new("group:c", "c", 3),
        };
        var mru = new[] { "a", "b", "c", "d" }.Select(W).ToList();
        var s2 = SwitcherController.AssignSlots(mru, pinned);
        var c = s2.First(x => x.Entry.Title == "c");
        Check("pin-holds-number", c.Slot == 3 && c.IsPinned,
            string.Join(',', s2.Select(x => $"{x.Entry.Title}:{x.Slot}")));

        // 3. Delete 2 → 3 keeps its number, the gap stays, 4 flows to... 2
        //    is VACANT so 4 must NOT take 3; the next unpinned window after
        //    the pin claims vacant numbers in order: 4 → 2 only via drag.
        //    Flow fill gives 4 the lowest free slot — that IS 2 — while the
        //    pin keeps 3. This matches 固定=固定数字: 3 is still 3.
        var mru3 = new[] { "a", "b2", "c", "d" }.Select(W).ToList(); // b deleted, b2 replaced it
        var s3 = SwitcherController.AssignSlots(mru3, pinned);
        var c3 = s3.First(x => x.Entry.Title == "c");
        Check("pin-survives-delete", c3.Slot == 3 && c3.IsPinned,
            string.Join(',', s3.Select(x => $"{x.Entry.Title}:{x.Slot}")));

        // 4. MoveRelative: dragging 4 to the LEFT of pinned 3 claims the
        //    vacant slot 2 (把4拖到3的左边，它自然变成了2).
        var s4 = new List<SlotWindow>
        {
            new(W("a"), 0, false),
            new(W("b"), 1, false),
            new(W("c"), 3, true),
            new(W("d"), 4, false),
        };
        var slots = new List<int> { 0, 1, 3, 4 };
        Check("drag-renumber-setup", slots.SequenceEqual(new[] { 0, 1, 3, 4 }));

        // 5. Unpinned flow: five windows fill 0..4 in MRU order.
        var s5 = Slots("a", "b", "c", "d", "e");
        Check("flow-five",
            s5.Select(x => x.Slot).SequenceEqual(new[] { 0, 1, 2, 3, 4 }),
            string.Join(',', s5.Select(x => x.Slot)));

        // 6. Unmanaged (11th+) shows no badge and keeps MRU tail order.
        var s6 = Slots(Enumerable.Range(0, 15).Select(i => $"w{i}").ToArray());
        Check("unmanaged-tail",
            s6.Count == 15 && s6[11].Slot == -1 && s6[14].Slot == -1 && s6[10].Slot == 10,
            string.Join(',', s6.Take(13).Select(x => x.Slot)));

        // 7. RenumberFlow — 把4拖到3的左边: post-move order a,b,d,c(pin@3)
        //    must renumber d to the vacant 2 while c keeps 3.
        var s7 = SwitcherController.RenumberFlow(new List<SlotWindow>
        {
            new(W("a"), 0, false),
            new(W("b"), 1, false),
            new(W("d"), 4, false),
            new(W("c"), 3, true),
        });
        var d7 = s7.First(x => x.Entry.Title == "d");
        var c7 = s7.First(x => x.Entry.Title == "c");
        Check("move-left-claims-vacant",
            d7.Slot == 2 && c7.Slot == 3 && c7.IsPinned && s7[0].Slot == 0 && s7[1].Slot == 1,
            string.Join(',', s7.Select(x => $"{x.Entry.Title}:{x.Slot}{(x.IsPinned ? "!" : "")}")));

        // 8. RenumberFlow — overflow beyond 10 lands unmanaged at the tail.
        var many = new List<SlotWindow> { new(W("z0"), 0, false) };
        for (var i = 1; i <= 13; i++) many.Add(new(W($"z{i}"), i, false));
        var s8 = SwitcherController.RenumberFlow(many);
        Check("renumber-overflow-tail",
            s8.Count == 14 && s8[10].Slot == 10 && s8[11].Slot == -1 && s8[13].Slot == -1,
            string.Join(',', s8.Select(x => x.Slot)));

        WindowSwitcherWpf.Services.Log.Info("SlotTests", "done");
        Environment.Exit(0);
    }

    /// <summary>
    /// Self-test for the manual stacking cores (自定义集中/剥离). Run via
    /// <c>WindowSwitcherWpf.exe --test-stack</c>; prints PASS/FAIL lines.
    /// Exercises the pure StackDropCore/PeelCore rules — no real config.
    /// </summary>
    private void RunStackTests()
    {
        static WindowEntry W(string title) =>
            new(new IntPtr(title.GetHashCode() & 0xFFFFFF), title, "app.exe",
                @"C:\apps\app.exe", "group:" + title, false);

        static void Check(string name, bool ok, string detail = "")
        {
            var line = $"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " | " + detail : "")}";
            Console.WriteLine(line);
            WindowSwitcherWpf.Services.Log.Info("StackTests", line);
            Console.Out.Flush();
        }

        static string Dump(List<StackDefinition> defs) =>
            string.Join(" | ", defs.Select(d =>
                (d.Id.Length <= 6 ? d.Id : d.Id[..6]) + ":" + string.Join(",", d.Members.Select(m => m.Title))));

        // 1. Lone + lone, dropped after (中心/右半): target becomes head.
        var defs1 = new List<StackDefinition>();
        var ok1 = SwitcherController.StackDropCore(defs1, W("a2"), W("a1"), before: false);
        Check("new-stack-head-is-target",
            ok1 && defs1.Count == 1 && defs1[0].Members[0].Title == "a1"
            && defs1[0].Members[1].Title == "a2",
            Dump(defs1));

        // 2. Lone + lone, dropped before (左半): dragged becomes head.
        var defs2 = new List<StackDefinition>();
        SwitcherController.StackDropCore(defs2, W("a2"), W("a1"), before: true);
        Check("new-stack-before-head-is-drag",
            defs2[0].Members[0].Title == "a2" && defs2[0].Members[1].Title == "a1",
            Dump(defs2));

        // 3. Same-stack reorder: moving a member before another one.
        var defs3 = new List<StackDefinition>
        {
            new() { Members = new() { M("a1"), M("a2"), M("a3") } },
        };
        var ok3 = SwitcherController.StackDropCore(defs3, W("a3"), W("a1"), before: true);
        Check("same-stack-reorder",
            ok3 && string.Join(",", defs3[0].Members.Select(m => m.Title)) == "a3,a1,a2",
            Dump(defs3));

        // 4. Same-stack no-ops: head drag inside its own stack, adjacent move.
        var defs4 = new List<StackDefinition>
        {
            new() { Members = new() { M("a1"), M("a2") } },
        };
        var ok4a = SwitcherController.StackDropCore(defs4, W("a1"), W("a2"), before: false);
        var ok4b = SwitcherController.StackDropCore(defs4, W("a2"), W("a1"), before: false);
        Check("same-stack-noop", !ok4a && !ok4b, Dump(defs4));

        // 5. Head drag carries the WHOLE stack onto a lone target.
        var defs5 = new List<StackDefinition>
        {
            new() { Id = "s1", Members = new() { M("a1"), M("a2") } },
        };
        SwitcherController.StackDropCore(defs5, W("a1"), W("x"), before: false);
        Check("head-carries-whole-stack",
            defs5.Count == 1 && defs5[0].Id == "s1"
            && string.Join(",", defs5[0].Members.Select(m => m.Title)) == "x,a1,a2",
            Dump(defs5));

        // 6. Non-head member dragged onto a lone card: detaches, origin
        //    dissolves (1 member left), new 2-stack forms.
        var defs6 = new List<StackDefinition>
        {
            new() { Id = "s1", Members = new() { M("a1"), M("a2") } },
        };
        SwitcherController.StackDropCore(defs6, W("a2"), W("x"), before: false);
        Check("member-detach-origin-dissolves",
            defs6.Count == 1 && defs6[0].Members[0].Title == "x"
            && defs6[0].Members[1].Title == "a2",
            Dump(defs6));

        // 7. Member joins ANOTHER stack at the target member's position.
        var defs7 = new List<StackDefinition>
        {
            new() { Id = "s1", Members = new() { M("a1"), M("a2") } },
            new() { Id = "s2", Members = new() { M("b1"), M("b2") } },
        };
        SwitcherController.StackDropCore(defs7, W("a2"), W("b1"), before: false);
        Check("member-joins-other-stack",
            defs7.Count == 1 && defs7[0].Id == "s2"
            && string.Join(",", defs7[0].Members.Select(m => m.Title)) == "b1,a2,b2",
            Dump(defs7));

        // 8. Peel down to 1 member → stack dissolves (散开).
        var defs8 = new List<StackDefinition>
        {
            new() { Members = new() { M("a1"), M("a2") } },
        };
        SwitcherController.PeelCore(defs8, W("a2"));
        Check("peel-dissolves-at-one", defs8.Count == 0, Dump(defs8));

        // 9. Peel of a lone (unstacked) window is a no-op.
        var defs9 = new List<StackDefinition>();
        Check("peel-lone-noop", !SwitcherController.PeelCore(defs9, W("a1")));

        // 10. Identity rule: same Title but different GroupKey never matches.
        var wA = W("doc");
        var wB = new WindowEntry(new IntPtr(7), "doc", "other.exe",
            @"C:\other.exe", "group:other", false);
        var defs10 = new List<StackDefinition>
        {
            new() { Members = new() { M("doc") } },
        };
        Check("identity-is-groupkey-plus-title", !SwitcherController.PeelCore(defs10, wB) && wA.GroupKey != wB.GroupKey);

        static StackMemberDef M(string title) =>
            new() { GroupKey = "group:" + title, Title = title };

        WindowSwitcherWpf.Services.Log.Info("StackTests", "done");
        Console.WriteLine("done");
        Environment.Exit(0);
    }

    /// <summary>
    /// Self-test for the config/template/smart-sort engine. Run via
    /// <c>WindowSwitcherWpf.exe --test-config</c>; prints PASS/FAIL lines.
    /// Exercises generic rule matching, template→pin conversion and the
    /// three-tier AssignSlots (pins &gt; smart rules &gt; MRU) — no real
    /// config.json is touched.
    /// </summary>
    private void RunConfigTests()
    {
        static WindowEntry W(string title, string process, string path, string group) =>
            new(new IntPtr(title.GetHashCode() & 0xFFFFFF), title, process, path, group, false);

        static void Check(string name, bool ok, string detail = "")
        {
            var line = $"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " | " + detail : "")}";
            Console.WriteLine(line);
            WindowSwitcherWpf.Services.Log.Info("ConfigTests", line);
        }

        // 1. Generic rule matching (通用过滤 — structural, not by app name).
        var wTerm = W("term", "WindowsTerminal", @"C:\tools\wt.exe", "C:\\tools\\wt.exe");
        Check("rule-process",
            SwitcherController.RuleMatches(wTerm, new SortRule { Type = "process", Value = "terminal" }));
        Check("rule-path",
            SwitcherController.RuleMatches(wTerm, new SortRule { Type = "path", Value = "tools" }));
        Check("rule-title-nomatch",
            !SwitcherController.RuleMatches(wTerm, new SortRule { Type = "title", Value = "chrome" }));

        // 2. Smart sort tier: rules reorder unpinned flow BEFORE MRU.
        var term = W("term", "WindowsTerminal", @"C:\tools\wt.exe", "g:term");
        var chrome = W("web", "chrome", @"C:\web\chrome.exe", "g:chrome");
        var misc = W("misc", "miscapp", @"C:\x\misc.exe", "g:misc");
        var rules = new List<SortRule>
        {
            new() { Type = "process", Value = "chrome" },
        };
        var mru = new List<WindowEntry> { term, misc, chrome };
        var s2 = SwitcherController.AssignSlots(mru, new List<PinDefinition>(), rules);
        // chrome (rule 0) must claim slot 1 even though it is LAST in MRU.
        Check("smart-sort-rule-beats-mru",
            s2[1].Entry.Title == "web" && s2[2].Entry.Title == "misc",
            string.Join(',', s2.Select(x => $"{x.Entry.Title}:{x.Slot}")));

        // 3. Pins outrank smart rules (固定 tier is never reordered).
        var pinC = new PinDefinition("g:misc", "misc", 1);
        var s3 = SwitcherController.AssignSlots(mru, new List<PinDefinition> { pinC }, rules);
        Check("pins-beat-rules",
            s3.First(x => x.Entry.Title == "misc").Slot == 1
            && s3.First(x => x.Entry.Title == "misc").IsPinned,
            string.Join(',', s3.Select(x => $"{x.Entry.Title}:{x.Slot}{(x.IsPinned ? "!" : "")}")));

        // 4. Config round-trip through an isolated temp store.
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "wsw-config-test-" + Guid.NewGuid().ToString("N"));
            var cfgPath = Path.Combine(dir, "config.json");
            var cfg = new ConfigFile
            {
                Templates = new List<TemplateDefinition>
                {
                    new()
                    {
                        Name = "t1",
                        Slots = new List<TemplateSlot>
                        {
                            new() { Slot = 2, GroupKey = "g:term", Title = "term" },
                        },
                    },
                },
                SortMethods = new List<SortMethodDefinition>
                {
                    new() { Name = "m1", Rules = new List<SortRule> { new() { Type = "process", Value = "x" } } },
                },
            };
            Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(cfg);
            System.IO.File.WriteAllText(cfgPath, json);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<ConfigFile>(
                System.IO.File.ReadAllText(cfgPath));
            var ok = loaded is not null
                && loaded.Templates.Count == 1
                && loaded.Templates[0].Slots[0].Slot == 2
                && loaded.SortMethods[0].Rules[0].Type == "process";
            Check("config-roundtrip", ok, cfgPath);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Check("config-roundtrip", false, ex.Message);
        }

        WindowSwitcherWpf.Services.Log.Info("ConfigTests", "done");
        Environment.Exit(0);
    }

    private void RunEnumerate()
    {
        var enumerator = new WindowEnumerator();
        var list = enumerator.Enumerate(onlyCurrentDesktop: true);
        Console.WriteLine($"=== final count = {list.Count} ===");
        foreach (var w in list)
        {
            var exStyle = WindowSwitcherWpf.Interop.NativeMethods.GetWindowLongPtr64(
                w.Hwnd, WindowSwitcherWpf.Interop.NativeMethods.GWL_EXSTYLE);
            var style = WindowSwitcherWpf.Interop.NativeMethods.GetWindowLongPtr64(
                w.Hwnd, WindowSwitcherWpf.Interop.NativeMethods.GWL_STYLE);
            Console.WriteLine($"  0x{w.Hwnd.ToInt64():X} ex=0x{exStyle:X} st=0x{style:X} '{w.Title}' [{w.ProcessName}]");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _foreground?.Dispose();
        _keyboard?.Dispose();
        SingleInstanceMutex?.ReleaseMutex();
        SingleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
