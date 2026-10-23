using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowSwitcherWpf.Interop;
using WindowSwitcherWpf.Models;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf;

/// <summary>
/// Windows 11 Alt+Tab style overlay: light rounded panel with a wrap-grid
/// of live window thumbnails. Backed by <see cref="SwitcherController"/>
/// and <see cref="WindowThumbnail"/> capture.
/// </summary>
public partial class SwitcherOverlay : Window
{
    private readonly SwitcherController _controller;
    private readonly ObservableCollection<OverlayWindow> _windows = new();
    private readonly DispatcherTimer _thumbTimer;
    private bool _isOpen;

    public SwitcherOverlay(SwitcherController controller)
    {
        _controller = controller;
        _controller.MoveRejectedByPin += OnMoveRejectedByPin;
        InitializeComponent();
        DataContext = this;
        WindowsList.ItemsSource = _windows;
        DesktopBar.ItemsSource = _desktops;

        // Match the native Alt+Tab switcher width: measured 2140px on a
        // 2560px work area (factor 0.836). Content beyond these caps
        // scrolls (draggable scrollbar on the right).
        MaxWidth = SystemParameters.WorkArea.Width * 0.836;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;

        // Any movement of the window or its viewport changes the
        // client-coordinate destination rects of the DWM thumbnails AND
        // the cascade sub-card positions (they live in DropLayer, a
        // sibling canvas — re-layout after every viewport change).
        LocationChanged += (_, _) => { UpdateLiveRects(); LayoutCascades(); };
        SizeChanged += (_, _) => { UpdateLiveRects(); LayoutCascades(); };
        Scroller.ScrollChanged += (_, _) => { UpdateLiveRects(); LayoutCascades(); };

        // 点击 overlay 外任意位置退出 (用户: 点外面就退, 等同未选 Esc):
        // WPF Window.Deactivated 在失焦时触发. ShowAndFocus 激活后会
        // 紧跟一个不期望的 Deactivated (如其他进程抢焦点 / DWM 弹通知),
        // 用 _suppressDeactivatedCancel + 短延迟关闭兜底, 直到 Activated
        // 真正落地为止.
        Deactivated += OnDeactivated;
        Activated += (_, _) => _suppressDeactivatedCancel = false;

        _thumbTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _thumbTimer.Tick += (_, _) => RefreshThumbnails();
    }

    /// <summary>Deactivated 取消守卫: Show 之后的瞬时失焦不应误关.</summary>
    private bool _suppressDeactivatedCancel;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isOpen || _suppressDeactivatedCancel) return;
        // 在 ApplicationIdle 之前若 Activated 触发 (Topmost 接管) 则撤销取消.
        var dispatcher = Dispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isOpen) return;
            if (IsActive) { _suppressDeactivatedCancel = false; return; }
            WindowSwitcherWpf.Services.Log.Info("Overlay", "deactivated → cancel");
            Cancel();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public ObservableCollection<OverlayWindow> AllWindows => _windows;

    // ---------- 虚拟桌面槽位行 ----------

    /// <summary>面板里点了某个桌面槽位 → App 层收掉面板并切桌面
    /// (面板属于当前桌面, 留着会被切走).</summary>
    public event Action<int>? DesktopSwitchRequested;

    private readonly ObservableCollection<DesktopChip> _desktops = new();

    /// <summary>刷新桌面槽位行 (序号 + 名字 + 当前高亮). 桌面数 &lt; 2 时
    /// 隐藏 — 只有一个桌面没什么可切的, 不占面板空间.</summary>
    private void RefreshDesktops()
    {
        var list = VirtualDesktop.List();
        var current = VirtualDesktop.CurrentGuid;
        _desktops.Clear();
        foreach (var d in list)
        {
            _desktops.Add(new DesktopChip(d.Index, d.Id,
                string.IsNullOrWhiteSpace(d.Name)
                    ? $"桌面 {d.Index + 1}"
                    : $"{d.Index + 1} {d.Name}",
                d.Id == current, list.Count >= 2));
        }
        DesktopBarRow.Visibility = list.Count >= 1 ? Visibility.Visible : Visibility.Collapsed;
        // 只有一个桌面 = 没处可迁, 提示文字就多余.
        if (DesktopDropHint is not null)
            DesktopDropHint.Visibility = list.Count >= 2
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>删掉这个虚拟桌面. 删当前桌面 = 面板自己所在的桌面没了,
    /// 先把面板收掉再删, 否则它变成看不见的幽灵窗口.</summary>
    private void OnDesktopChipDelete(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 别再冒泡成"点 chip = 切桌面"
        if (sender is not FrameworkElement fe || fe.DataContext is not DesktopChip chip) return;
        if (!chip.CanDelete) return;
        var answer = System.Windows.MessageBox.Show(
            $"删除虚拟桌面「{chip.Label}」? 它里面的窗口会被关闭.",
            "删除虚拟桌面",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        var wasCurrent = chip.IsCurrent;
        if (wasCurrent) Cancel();
        var ok = VirtualDesktop.Remove(chip.Id);
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"desktop delete {chip.Label} current={wasCurrent} ok={ok}");
        if (ok && !wasCurrent) RefreshDesktops();
    }

    private void OnNewDesktopClick(object sender, MouseButtonEventArgs e)
    {
        var created = VirtualDesktop.Create();
        if (created is null) return;
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"new desktop created: {created.Index + 1} ({created.Id})");
        RefreshDesktops();
    }

    private void OnDesktopChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DesktopChip chip)
        {
            WindowSwitcherWpf.Services.Log.Info("Overlay",
                $"desktop chip click → {chip.Label}");
            DesktopSwitchRequested?.Invoke(chip.Index);
        }
    }

    /// <summary>卡片角标: 窗口不在当前虚拟桌面时返回 "D2" 这样的标记,
    /// 在当前桌面返回 "" (不显示).</summary>
    private string DesktopBadgeOf(IntPtr hwnd)
    {
        var id = VirtualDesktop.DesktopIdOfWindow(hwnd);
        if (id == Guid.Empty) return "";
        foreach (var d in _desktops)
        {
            if (d.Id != id) continue;
            return d.IsCurrent ? "" : $"D{d.Index + 1}";
        }
        return "";
    }

    /// <summary>点亮/熄灭"落点 chip" (拖拽迁桌面时的靶子).</summary>
    private void SetDesktopDropTarget(DesktopChip? chip)
    {
        foreach (var d in _desktops)
            if (d.IsDropTarget != ReferenceEquals(d, chip)) d.IsDropTarget = ReferenceEquals(d, chip);
        // 整行也铺一层底色 — 胶囊之间的空隙/行边缘照样算落点, 用户不用瞄.
        DesktopBarRow.Background = chip is null
            ? System.Windows.Media.Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x28, 0xD9, 0x77, 0x06));
    }

    /// <summary>指针下的桌面槽位 (坐标相对 DesktopBar).</summary>
    private DesktopChip? DesktopChipAt(Point p)
    {
        for (var i = 0; i < _desktops.Count; i++)
        {
            if (DesktopBar.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (DesktopBar.ItemTemplate?.FindName("DesktopChip", cp) is not FrameworkElement chip) continue;
            var pos = chip.TransformToVisual(DesktopBar).Transform(new Point(0, 0));
            var r = new Rect(pos, new Point(chip.ActualWidth, chip.ActualHeight));
            if (r.Contains(p)) return _desktops[i];
        }
        return null;
    }

    /// <summary>桌面槽位行的落点判定 (坐标相对面板). 指针只要落在整行里就算
    /// 落点 — 胶囊之间的空隙、行边缘、胶囊留白全部吃掉 — 没命中精确矩形就
    /// 取最近的一颗胶囊. 之前只认胶囊本体, 手一抖就掉回"排序"去了, 是这条
    /// 路一直不灵的最大原因.</summary>
    private DesktopChip? DesktopDropChipAt(Point overlayPoint)
    {
        if (_desktops.Count == 0) return null;
        if (DesktopBarRow.Visibility != Visibility.Visible) return null;
        var row = new Rect(0, 0, DesktopBarRow.ActualWidth, DesktopBarRow.ActualHeight);
        if (!row.Contains(overlayPoint)) return null;
        var inBar = DesktopBar.PointFromScreen(this.PointFromScreen(overlayPoint));
        return DesktopChipAt(inBar) ?? NearestDesktopChip(overlayPoint);
    }

    /// <summary>距指针最近的胶囊 (中心距, 只看 X — 行是横的).</summary>
    private DesktopChip? NearestDesktopChip(Point overlayPoint)
    {
        DesktopChip? best = null;
        var bestD = double.MaxValue;
        for (var i = 0; i < _desktops.Count; i++)
        {
            if (DesktopBar.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (DesktopBar.ItemTemplate?.FindName("DesktopChip", cp) is not FrameworkElement chip) continue;
            var topLeft = this.PointFromScreen(chip.PointToScreen(new Point(0, 0)));
            var d = Math.Abs(topLeft.X + chip.ActualWidth / 2 - overlayPoint.X);
            if (d < bestD) { bestD = d; best = _desktops[i]; }
        }
        return best;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyAcrylic();
    }

    // Native Alt+Tab look: frosted acrylic + rounded window corners.
    // DWMWA_SYSTEMBACKDROP_TYPE reports success but never draws under a
    // WPF surface (the client area covers it — flat gray). The reliable
    // path is SetWindowCompositionAttribute + ACCENT_ACRYLICBLURBEHIND:
    // the desktop behind shows through, heavily blurred, with a light
    // tint (背景颜色透出来, 朦胧度拉满). GradientColor is ABGR.
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"first frame rendered, show→render {_showWatch.ElapsedMilliseconds}ms");
    }

    private void ApplyAcrylic()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            var policy = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentState.ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 0,
                GradientColor = 0, // tint is painted by WPF (RootBorder) so the
                                   // blurred wallpaper keeps its saturation —
                                   // ACRYLIC accent desaturates to flat gray
            };
            var pPolicy = System.Runtime.InteropServices.Marshal.AllocHGlobal(
                System.Runtime.InteropServices.Marshal.SizeOf(policy));
            System.Runtime.InteropServices.Marshal.StructureToPtr(policy, pPolicy, false);
            var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = pPolicy,
                SizeOfData = System.Runtime.InteropServices.Marshal.SizeOf(policy),
            };
            var ok = NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pPolicy);
            WindowSwitcherWpf.Services.Log.Info("Overlay",
                $"acrylic accent ok={ok}");
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Overlay.Acrylic", ex);
        }
    }

    public void ShowAndFocus()
    {
        try
        {
            _showWatch.Restart();
            if (_idleTrimTimer is { } t && t.IsEnabled) t.Stop();
            _controller.OpenOverlay();
            Rebuild();
            RefreshDesktops();
            try
            {
                Show();
            }
            catch (Exception ex)
            {
                WindowSwitcherWpf.Services.Log.Exception("Overlay.Show", ex);
                ShowError("Show 失败", ex);
                return;
            }
            _isOpen = true;
            _thumbTimer.Start();
            CenterOnCursorMonitor();
            // The Rebuild() above ran while _isOpen was still false, so its
            // thumbnail/cascade chain was skipped — schedule it now that the
            // window is shown and open (Loaded < ApplicationIdle, so these
            // run before PrepareMinimizedThenRegister registers).
            Dispatcher.BeginInvoke(new Action(BuildCascades), DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(UpdateLiveRects), DispatcherPriority.Loaded);
            // DWM thumbnails are registered after the window is shown and
            // fully laid out (destination rects are client coordinates).
            // Minimized windows render their tiny minimized frame into DWM
            // thumbnails, so they get a cloaked restore first.
            Dispatcher.BeginInvoke(new Action(PrepareMinimizedThenRegister),
                DispatcherPriority.ApplicationIdle);
            WindowSwitcherWpf.Services.Log.Info("Overlay", $"shown groups={_controller.Groups.Count} hwnd={new System.Windows.Interop.WindowInteropHelper(this).Handle:X} show={_showWatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Overlay.ShowAndFocus", ex);
            ShowError("ShowAndFocus 失败", ex);
        }
    }

    private static void ShowError(string title, Exception ex)
    {
        try
        {
            System.Windows.MessageBox.Show(
                ex.ToString(),
                "Window Switcher - " + title,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch { }
    }

    public void Advance(bool reverse)
    {
        _controller.Advance(reverse);
        RebuildSelection();
    }

    public void Commit()
    {
        if (!_isOpen || EditMode) return; // 编辑模式: 松 Alt 不提交
        _thumbTimer.Stop();
        // 这个窗口马上要被激活. 如果后台 cloak-restore 舞蹈刚把它临时还原,
        // 绝不能在舞蹈收尾时把它再最小化 — 否则用户就盯着一个"已激活又被
        // 按下去 + cloak 未解"的白窗口等 ~1s. 豁免它: 舞蹈留下的还原态正
        // 好就是激活后想要的.
        var picked = SelectedHwnd;
        if (picked != IntPtr.Zero) lock (DanceKeepLock) DanceKeepHwnds.Add(picked.ToInt64());
        _controller.Commit();
        UnregisterLiveThumbnails();
        _isOpen = false;
        Hide();
        EnterIdleMode();
    }

    public void Cancel()
    {
        if (!_isOpen) return;
        _thumbTimer.Stop();
        UnregisterLiveThumbnails();
        _isOpen = false;
        if (EditMode)
        {
            // 点面板外关闭 = 编辑模式结束. 每一步改动都已 SaveDebounced,
            // 这里不需要额外落盘.
            ToggleEditMode();
        }
        Hide();
        EnterIdleMode();
    }

    // ---------- 编辑模式 (拖拽排序 / 设快捷键号) ----------

    /// <summary>编辑模式: 位置规则全解除, 可随意拖拽与改号. 不持久化 —
    /// 关掉面板即退出. App 层在 OnOverlayCommit 里整体空转, 松 Alt 不关
    /// 面板 (等价于第二热键的常驻面板).</summary>
    public bool EditMode { get; private set; }

    /// <summary>正在等用户按数字键给某张卡设快捷号 (点角标后).</summary>
    public bool QuickEditPending => _quickEditHwnd != IntPtr.Zero;

    private void OnEditClick(object sender, RoutedEventArgs e) => ToggleEditMode();

    public void ToggleEditMode()
    {
        EditMode = !EditMode;
        // 编辑布局: 当前窗口卡/上一窗口卡让位成普通卡, 整行右移, 编号从 1
        // 开始 — 它们才可拖可固定 (用户: "上一个标签固定到 1 现在做不到").
        _controller.SetEditLayout(EditMode);
        if (EditMode)
        {
            if (_controller.IsInStackView) ExitStackView(); // 子页面没有位置概念
            EditButton.Content = "✓ 完成";
            ShowStatus("编辑模式：拖拽排序 · 点 📌 固定 · 点左上角数字设快捷号 · 点面板外关闭");
        }
        else
        {
            EditButton.Content = "✎ 编辑";
            ClearQuickEditPending();
            ShowStatus("");
        }
        if (!_isOpen) return; // 关闭流程里退编辑模式: 面板马上藏, 不重建
        RebuildFromSlots(preserveScroll: true);
    }

    private readonly DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(4500),
    };

    private void ShowStatus(string msg, int ms = 4500)
    {
        StatusText.Text = msg;
        StatusText.Visibility = msg.Length == 0
            ? Visibility.Collapsed : Visibility.Visible;
        _statusTimer.Stop();
        if (msg.Length > 0)
        {
            _statusTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _statusTimer.Tick += OnStatusTick;
            _statusTimer.Start();
        }
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTick;
        if (!EditMode) ShowStatus("");
    }

    // ---------- 角标改快捷号 (点角标 → 按数字) ----------
    // 状态存 hwnd 不存 OverlayWindow 实例: RebuildFromSlots 每次全量重建,
    // 存实例下一次就失效了.
    private IntPtr _quickEditHwnd;
    private int? _quickEditOriginal;

    private void OnBadgeClick(object sender, MouseButtonEventArgs e)
    {
        if (!EditMode || _controller.IsInStackView) return;
        if (sender is not FrameworkElement { DataContext: OverlayWindow ow }) return;
        ClearQuickEditPending();
        _quickEditHwnd = ow.Hwnd;
        // 原值 = 手动设过的快捷号 (没设过 = null, Esc 时清掉).
        var sw = _controller.Slotted.FirstOrDefault(s => s.Entry.Hwnd == ow.Hwnd);
        _quickEditOriginal = sw is null ? null : _controller.ExplicitQuickFor(sw);
        ShowStatus("输入数字设快捷号 (0=10) · Esc 取消", 8000);
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"quick edit start '{ow.DisplayTitle}' hwnd=0x{ow.Hwnd.ToInt64():X}");
        e.Handled = true;
        // 重建一次让这颗角标进入闪烁态 (用户: "点一下就该看见在闪")
        RebuildFromSlots(preserveScroll: true);
        StartQuickBlink(ow.Hwnd);
    }

    /// <summary>改号待定: 那颗角标里的 "(N)" 一直闪. 停靠重建自然结束
    /// (号码一落盘就 RebuildFromSlots, 新容器没有动画).</summary>
    private void StartQuickBlink(IntPtr hwnd)
    {
        var ow = _windows.FirstOrDefault(w => w.Hwnd == hwnd);
        if (ow is null
            || WindowsList.ItemContainerGenerator.ContainerFromItem(ow) is not ContentPresenter cp
            || WindowsList.ItemTemplate?.FindName("QuickPart", cp) is not TextBlock tb) return;
        tb.BeginAnimation(System.Windows.UIElement.OpacityProperty,
            new DoubleAnimation(1, 0.15, TimeSpan.FromMilliseconds(400))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }

    private void ClearQuickEditPending()
    {
        _quickEditHwnd = IntPtr.Zero;
        _quickEditOriginal = null;
    }

    /// <summary>角标编辑中按了数字: 这颗数字是**快捷号**而非位置号.
    /// 立即落盘 (无待定态), 冲突则顶掉旧指派并红闪提示.</summary>
    private void ApplyPendingQuickNumber(int digit)
    {
        var hwnd = _quickEditHwnd;
        ClearQuickEditPending();
        var n = SwitcherController.NumberForKey(digit);
        var stolen = _controller.SetQuickNumber(hwnd, n);
        ShowStatus(stolen is { Length: > 0 } t
            ? $"快捷号 {n} —「{t}」的原号码已让出"
            : $"快捷号 = {n} (面板关闭后 Alt+{SwitcherController.KeyForNumber(n)} 直达)", 6000);
        if (stolen is { Length: > 0 })
        {
            // 旧指派那张卡红闪一下 — 复用已有的 FlashPin, 不新增视觉机制.
            var victim = _windows.FirstOrDefault(w => w.Title == stolen)?.Hwnd ?? IntPtr.Zero;
            if (victim != IntPtr.Zero) Dispatcher.BeginInvoke(
                new Action(() => FlashPin(victim)), DispatcherPriority.Loaded);
        }
        RebuildFromSlots(preserveScroll: true);
    }

    /// <summary>Enter 语义: 号码即时生效, Enter 只是关掉闪烁 (不改任何东西).</summary>
    public void ConfirmQuickEdit()
    {
        if (!QuickEditPending) return;
        ClearQuickEditPending();
        ShowStatus("");
        RebuildFromSlots(preserveScroll: true);
    }

    /// <summary>Esc: 撤销这次角标编辑 (还没输数字就只是退出待定态).</summary>
    public void CancelQuickEdit()
    {
        if (!QuickEditPending) return;
        var hwnd = _quickEditHwnd;
        var orig = _quickEditOriginal;
        ClearQuickEditPending();
        if (orig is { } n) _controller.SetQuickNumber(hwnd, n);
        else _controller.ClearQuickNumber(hwnd);
        ShowStatus(EditMode ? "已取消" : "", 2000);
        RebuildFromSlots(preserveScroll: true);
    }

    // Tray-app memory discipline: the overlay just did its bitmap burst;
    // drop visual-cache entries for windows that no longer exist and trim
    // the working set so idle RAM pages out to the pagefile.
    /// <summary>空闲省内存定时器: 面板关掉 90 秒后才踢内存页. 之前是关掉
    /// 立刻踢, 结果每次 Alt+Tab 打开都要把整个进程从 pagefile 软缺页调回
    /// 来, 表现为面板先白屏 ~1s 才出内容. 拖后 + 开面板时取消, 打开路径
    /// 就不再付这笔缺页钱; 真空闲 90s 内存照样能被系统换出去.</summary>
    private System.Windows.Threading.DispatcherTimer? _idleTrimTimer;

    private System.Diagnostics.Stopwatch _showWatch = new();

    private void EnterIdleMode()
    {
        _idleTrimTimer ??= CreateIdleTrimTimer();
        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateIdleTrimTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(90) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_isOpen) return;
            DoIdleTrim();
        };
        return t;
    }

    private void DoIdleTrim()
    {
        var dead = new List<IntPtr>();
        foreach (var kv in s_visualCache)
        {
            NativeMethods.GetWindowThreadProcessId(kv.Key, out var pidU);
            if (pidU == 0) dead.Add(kv.Key);
        }
        foreach (var h in dead) s_visualCache.Remove(h);
        // 压实 LOH: 缩略图/位图字节数组住在大对象堆, 只标记不压实的话
        // 那些页永远 committed (private 只涨不降) — compact once 让下一
        // 次 GC 归还它们.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        NativeMethods.SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        WindowSwitcherWpf.Services.Log.Info("Overlay", "idle(90s): cache trimmed, working set requested");
    }

    public void SelectDigit(int digit)
    {
        _controller.SelectByDigit(digit);
        RebuildSelection();
    }

    private void Rebuild() => RebuildFromSlots(preserveScroll: false);



    public event Action? SettingsRequested;

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        WindowSwitcherWpf.Services.Log.Info("Overlay", "settings requested");
        SettingsRequested?.Invoke();
    }

    /// <summary>Re-renders the card list from the controller's slot model
    /// (pins + flow + drag result). Slot 0 = most recent, 1..10 managed,
    /// -1 unmanaged (no badge). Pin toggles and drags rebuild with the
    /// scroll position kept so the card doesn't jump under the cursor.
    /// In the stack sub view (子标签页面) it renders the stack's members
    /// in relative order instead.</summary>
    private void RebuildFromSlots(bool preserveScroll)
    {
        var offset = preserveScroll ? Scroller.VerticalOffset : 0.0;
        RefreshDesktops(); // 桌面号角标要用当前桌面列表
        _windows.Clear();
        if (_controller.IsInStackView)
        {
            var info = _controller.ActiveStackInfo!;
            for (var idx = 0; idx < info.Members.Count; idx++)
            {
                var m = info.Members[idx];
                var ow = new OverlayWindow(m, m.GroupKey, 0)
                {
                    // Sub view digits are RELATIVE member numbers (1,2,…) —
                    // not the shared slot digit; Alt+digit maps 1..9→0..8,
                    // 0→9th, matching SelectByDigit.
                    SlotBadge = RelativeMemberDigit(idx),
                    DesktopBadge = DesktopBadgeOf(m.Hwnd),
                    // 固定子标签在子页面同样亮黄框 (alt 3 进入看到同样语义).
                    IsPinned = _controller.IsMemberPinned(m.Hwnd),
                };
                var cached = LookupCache(ow.Hwnd);
                if (cached is not null)
                {
                    ow.Thumbnail = cached.Thumb;
                    ow.Icon = cached.Icon;
                }
                _windows.Add(ow);
            }
        }
        else
        {
            foreach (var sw in _controller.Slotted)
            {
                var ow = new OverlayWindow(sw.Entry, sw.Entry.GroupKey, 0)
                {
                    SlotBadge = sw.Slot >= 0 ? sw.Slot.ToString() : "",
                    DesktopBadge = DesktopBadgeOf(sw.Entry.Hwnd),
                    SlotValue = sw.Slot,
                    // 位置号 != 快捷号时才显示成 "位置(快捷)" (未指派 = 回落
                    // 位置号 = null, 与位置号相同时也是 null).
                    QuickNumber = _controller.ExplicitQuickFor(sw),
                    // 待定改号的那张卡闪烁 (state 存 hwnd, 因为每次重建)
                    IsQuickEditing = _quickEditHwnd == sw.Entry.Hwnd,
                    IsPinned = sw.IsPinned,
                    IsCurrentCopy = sw.IsCurrentCopy,
                    IsSlot0Copy = sw.IsSlot0Copy,
                };
                var st = _controller.StackInfoFor(sw.Entry.Hwnd);
                if (st is not null) ow.StackCount = st.Members.Count;
                var cached = LookupCache(ow.Hwnd);
                if (cached is not null)
                {
                    ow.Thumbnail = cached.Thumb;
                    ow.Icon = cached.Icon;
                }
                _windows.Add(ow);
            }
        }
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"rebuild windows={_windows.Count} stackView={_controller.IsInStackView}");
        // 0 张卡 = 当前虚拟桌面是空的 (或窗口都在别的桌面). 面板必须有内容,
        // 否则塌成 0 高度 → 看不见也点不到桌面槽位, 就切不回去了.
        EmptyDesktopHint.Visibility = _windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AssignGroupAccents();
        // Thumbnail capture happens via _thumbTimer, NEVER in Rebuild,
        // otherwise 113 PrintWindow calls block the dispatcher for ~2s
        // and the user releases Alt before the window is shown.
        RebuildSelection();
        RefreshThumbnails();
        // The visible card set changes when a stack sub view opens/closes,
        // so the DWM registrations follow it; same-set rebuilds (pin drag)
        // skip the churn to avoid flicker. Cascade sub-cards must be built
        // before the rects are measured (FIFO at the same priority).
        if (_isOpen)
        {
            var desired = new HashSet<(IntPtr, bool)>(_windows.Select(w => (w.Hwnd, w.IsCardCopy)));
            if (!_controller.IsInStackView)
                foreach (var w in _windows)
                    if (_controller.StackInfoFor(w.Hwnd) is { } st2)
                        foreach (var m in st2.Members)
                            desired.Add((m.Hwnd, false));
            Dispatcher.BeginInvoke(new Action(BuildCascades), DispatcherPriority.Loaded);
            if (!_liveThumbs.Keys.ToHashSet().SetEquals(desired))
                Dispatcher.BeginInvoke(new Action(RegisterLiveThumbnails), DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(UpdateLiveRects), DispatcherPriority.Loaded);
        }
        Dispatcher.BeginInvoke(new Action(LogCardRects), DispatcherPriority.Loaded);
        UpdateHeaderForView();
        // View switches (主↔子页面) change the content size drastically —
        // re-measure and re-center so the sub view doesn't sit in a huge
        // main-page-sized panel. Pin drags (preserveScroll) keep geometry.
        if (_isOpen && !preserveScroll && IsLoaded)
            CenterOnCursorMonitor();
        if (preserveScroll)
            Dispatcher.BeginInvoke(() => Scroller.ScrollToVerticalOffset(offset),
                DispatcherPriority.Loaded);
    }

    /// <summary>Main page header (设置入口) vs sub view header (堆叠标题 +
    /// 操作提示) — only one is visible at a time.</summary>
    private void UpdateHeaderForView()
    {
        var sub = _controller.IsInStackView;
        MainHeader.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        StackHeader.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        // 子页面是"堆叠内部" — 桌面槽位行只对主页面有意义.
        if (sub) DesktopBarRow.Visibility = Visibility.Collapsed;
        else if (_desktops.Count >= 1) DesktopBarRow.Visibility = Visibility.Visible;
        if (!sub) return;
        var info = _controller.ActiveStackInfo!;
        StackTitleText.Text = info.Head.DisplayTitle;
        StackCountText.Text = "×" + info.Members.Count;
    }

    /// <summary>Alt+数字 quick jump: select the slot with that badge and
    /// activate it immediately (快速打开这个标签). A digit pointing at a
    /// STACK opens the 子标签页面 instead — Alt release commits, arrows and
    /// (hidden) relative digits move within the stack.</summary>
    public void SelectSlotAndCommit(int digit)
    {
        if (!_isOpen) return;
        // 1) 点角标后的待定输入: 这颗数字是快捷号, 不是位置号.
        if (QuickEditPending) { ApplyPendingQuickNumber(digit); return; }
        // 2) 编辑模式: 数字仍按**位置号**寻址 (与面板外 Alt+数字 相反),
        //    但只选中不提交.
        if (EditMode)
        {
            _controller.SelectByDigit(digit);
            RebuildSelection();
            return;
        }
        if (_controller.IsInStackView)
        {
            // Relative member index (相对数字不显示但有效): Alt+2 = 第2个成员.
            _controller.SelectByDigit(digit);
            RebuildSelection();
            return; // still navigating — Alt release commits the member
        }
        if (!_controller.SelectByDigit(digit)) return;
        RebuildSelection();
        var entry = _controller.Flat[Math.Min(_controller.ActiveFlatIndex, _controller.Flat.Count - 1)];
        if (_controller.StackInfoFor(entry.Hwnd) is not null)
        {
            _controller.EnterStackView(entry.Hwnd);
            RebuildFromSlots(preserveScroll: false);
            return;
        }
        Commit();
    }

    public bool IsInStackView => _controller.IsInStackView;

    /// <summary>Esc from the sub view: back to the main page (堆叠不解散).</summary>
    public void ExitStackView()
    {
        if (!_controller.IsInStackView) return;
        _controller.ExitStackView();
        RebuildFromSlots(preserveScroll: false);
    }

    /// <summary>Self-test hook: force a re-render from the current
    /// controller state (--show-overlay --enter-stack N screenshot flow).</summary>
    public void TestRebuild() => RebuildFromSlots(preserveScroll: false);

    // Ground truth for scripted self-tests: the physical screen rect of
    // every card, so synthetic clicks can target pins without pixel
    // archaeology on screenshots.
    private void LogCardRects()
    {
        if (!_isOpen) return;
        for (var i = 0; i < _windows.Count; i++)
        {
            if (WindowsList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not FrameworkElement card) continue;
            var tl = card.PointToScreen(new Point(0, 0));
            var br = card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight));
            var w = _windows[i];
            WindowSwitcherWpf.Services.Log.Info("CardRect",
                $"i={i} slot={w.SlotBadge} pinned={w.IsPinned} " +
                $"screen=({(int)tl.X},{(int)tl.Y})-({(int)br.X},{(int)br.Y}) '{w.Title}'");
            if (i > 12) break;
        }
    }

    // 8-hue structural palette; index comes from a stable FNV-1a hash of
    // the GroupKey so a given app keeps its color across sessions (never
    // string.GetHashCode — that is randomized per process).
    private static readonly System.Windows.Media.Color[] GroupPalette =
    {
        System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB), // blue
        System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A), // green
        System.Windows.Media.Color.FromRgb(0xEA, 0x58, 0x0C), // orange
        System.Windows.Media.Color.FromRgb(0x93, 0x33, 0xEA), // purple
        System.Windows.Media.Color.FromRgb(0x08, 0x91, 0xB2), // cyan
        System.Windows.Media.Color.FromRgb(0xDB, 0x27, 0x77), // magenta
        System.Windows.Media.Color.FromRgb(0xCA, 0x8A, 0x04), // dark yellow
        System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26), // red
    };

    private static uint Fnv1a(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    private void AssignGroupAccents()
    {
        _accentMap.Clear();
        foreach (var w in _windows)
        {
            if (!_accentMap.TryGetValue(w.GroupKey, out var color))
            {
                color = GroupPalette[Fnv1a(w.GroupKey) % (uint)GroupPalette.Length];
                _accentMap[w.GroupKey] = color;
            }
            w.SetGroupAccent(color);
        }
    }

    // Last accent map from AssignGroupAccents — fan strips read the head's
    // color from here.
    private readonly System.Collections.Generic.Dictionary<string, System.Windows.Media.Color> _accentMap = new();

    // 悬停联动: light up every card of the hovered card's group so
    // related windows are findable without any layout shift.
    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (_dragActive) return;
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
            SetGroupHighlight(ow.GroupKey, true);
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
            SetGroupHighlight(ow.GroupKey, false);
    }

    private void SetGroupHighlight(string groupKey, bool on)
    {
        foreach (var w in _windows)
            if (w.GroupKey == groupKey) w.IsGroupHighlighted = on;
    }

    /// <summary>面板开着时选中卡的窗口 (Win+Ctrl+Shift+←/→ 迁移用).
    /// 面板没开 → 0, 调用方回退到前台窗口.</summary>
    public IntPtr SelectedHwnd =>
        IsVisible && _windows.Count > 0
            && _controller.ActiveFlatIndex >= 0
            && _controller.ActiveFlatIndex < _windows.Count
            ? _windows[_controller.ActiveFlatIndex].Hwnd
            : IntPtr.Zero;

    private void RebuildSelection()
    {
        for (var i = 0; i < _windows.Count; i++)
            _windows[i].IsSelected = false;
        if (_controller.Nav.Count == 0 || _windows.Count == 0) return;
        var idx = Math.Min(_controller.ActiveFlatIndex, _windows.Count - 1);
        if (idx < 0) return;
        var target = _windows[idx];
        // 每张卡都是普通停靠点 — 蓝框画在停靠的那张卡本身. 之前把 -2
        // 当前窗口副本特殊对待 (选中它却把框挪到同 hwnd 的真身), 结果
        // 左箭头移到左上角那张时框不在这张卡上, 看起来"不可选"
        // (用户: 方向键应该能移到这里).
        target.IsSelected = true;
        ScrollSelectionIntoView();
    }

    // Keep the selected card visible when navigating with keys (native
    // Alt+Tab behaviour for lists longer than one screen).
    private void ScrollSelectionIntoView()
    {
        var idx = _controller.ActiveFlatIndex;
        if (WindowsList.ItemContainerGenerator.ContainerFromIndex(idx) is not FrameworkElement el) return;
        el.UpdateLayout();
        var point = el.TransformToVisual(Scroller).Transform(new Point(0, 0));
        var viewportH = Scroller.ViewportHeight;
        if (point.Y < 0)
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + point.Y);
        else if (point.Y + el.ActualHeight > viewportH)
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + point.Y + el.ActualHeight - viewportH);
    }

    /// <summary>Arrow-key grid navigation forwarded from the keyboard hook:
    /// left/right = ±1 card, up/down = ±(cards per row), wrapping.</summary>
    public void NavigateArrow(int vk)
    {
        var n = _controller.Nav.Count;
        if (n == 0) return;
        var idx = _controller.ActiveFlatIndex;
        switch (vk)
        {
            case 0x25: idx -= 1; break;   // left
            case 0x27: idx += 1; break;   // right
            case 0x26: idx -= Columns(); break; // up
            case 0x28: idx += Columns(); break; // down
        }
        _controller.SelectFlat(idx);
        RebuildSelection();
    }

    private int Columns()
    {
        // Cards per row = number of containers sharing the minimum top
        // offset within the wrapped panel.
        double minTop = double.MaxValue;
        var tops = new List<double>();
        for (var i = 0; i < _windows.Count; i++)
        {
            if (WindowsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
            var y = el.TransformToVisual(WindowsList).Transform(new Point(0, 0)).Y;
            tops.Add(y);
            if (y < minTop) minTop = y;
        }
        var cols = 0;
        foreach (var y in tops)
            if (Math.Abs(y - minTop) <= 1) cols++;
        return Math.Max(1, cols);
    }

    private void RefreshThumbnails()
    {
        var pending = new List<OverlayWindow>();
        for (var i = 0; i < _windows.Count; i++)
        {
            var needThumb = _windows[i].Thumbnail is null && !_windows[i].HasLivePreview;
            var needIcon = _windows[i].Icon is null;
            if (needThumb || needIcon) pending.Add(_windows[i]);
        }
        if (pending.Count == 0) return;

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            int thumbDone = 0, iconDone = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                var w = pending[i];
                try
                {
                    if (w.Thumbnail is null && !w.HasLivePreview)
                    {
                        var bmp = WindowSwitcherWpf.Interop.WindowThumbnail.Capture(w.Hwnd);
                        if (bmp is not null)
                        {
                            bmp.Freeze();
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                w.Thumbnail = bmp;
                                StoreCache(w.Hwnd, thumb: bmp, icon: null);
                            }));
                            thumbDone++;
                        }
                    }
                    if (w.Icon is null)
                    {
                        var icon = WindowSwitcherWpf.Interop.AppIcon.Resolve(w.Hwnd, w.ModulePath, 20);
                        if (icon is not null)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                w.Icon = icon;
                                StoreCache(w.Hwnd, thumb: null, icon: icon);
                            }));
                            iconDone++;
                        }
                    }
                }
                catch { /* swallow per-window */ }
            }
            WindowSwitcherWpf.Services.Log.Info("Overlay", $"thumbnails={thumbDone} icons={iconDone}/{pending.Count}");
        });
    }

    private void CenterOnCursorMonitor()
    {
        try
        {
            if (!Interop.NativeMethods.GetCursorPos(out var pt))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }
            var monitor = Interop.NativeMethods.MonitorFromPoint(
                pt, Interop.NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new Interop.NativeMethods.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Interop.NativeMethods.MONITORINFO>(),
            };
            if (!Interop.NativeMethods.GetMonitorInfoW(monitor, ref mi))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }
            var work = mi.rcWork;
            // MONITORINFO is physical pixels; convert with this window's DPI
            // so multi-monitor setups with mixed scaling stay centered.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var scale = dpi.PixelsPerDip > 0 ? 1.0 / dpi.PixelsPerDip : 1.0;
            var scaleX = scale;
            var scaleY = scale;
            var workW = (work.Right - work.Left) * scaleX;
            var workH = (work.Bottom - work.Top) * scaleY;

            // Cap the panel to the cursor monitor (MaxWidth/MaxHeight from
            // the constructor follow the PRIMARY monitor only), then force
            // layout so the window resizes to the new cap. 0.836 = native
            // Alt+Tab switcher width (2140px @ 2560px work area).
            MaxWidth = workW * 0.836;
            MaxHeight = workH * 0.92;
            InvalidateMeasure();
            Measure(new Size(MaxWidth, MaxHeight));
            Arrange(new Rect(new Point(0, 0), DesiredSize));
            UpdateLayout();

            var w = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
            var h = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
            if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0) w = 800;
            if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0) h = 500;
            Left = work.Left * scaleX + (workW - w) / 2.0;
            Top = work.Top * scaleY + (workH - h) / 2.0;
            WindowSwitcherWpf.Services.Log.Info("Overlay",
                $"centered monitor work=({work.Left},{work.Top},{work.Right},{work.Bottom}) " +
                $"size=({w}x{h}) pos=({Left},{Top})");
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Overlay.Center", ex);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    // ---------- DWM live thumbnails ----------
    // DwmRegisterThumbnail requires a TOP-LEVEL destination hwnd; the
    // overlay window itself is used, with one registered thumbnail per
    // source window and a per-card client-coordinate destination rect.
    // DWM composites the source window's live surface onto the card —
    // same mechanism native Alt+Tab uses — so minimized and occluded
    // windows still show real content with zero capture cost.

    // Key = (hwnd, isCurrentCopy): 当前窗口卡与真实卡同 hwnd 双卡并存,
    // DWM 允许同一 source 注册多个 thumbnail — 各自独立 dest rect.
    private readonly Dictionary<(IntPtr Hwnd, bool Copy), IntPtr> _liveThumbs = new();

    // A minimized window's DWM surface is its tiny minimized frame, so a
    // live thumbnail shows a miniature window instead of content. Fix:
    // restore it while CLOAKED (DWM composites cloaked windows off-screen,
    // so nothing flashes), let it render, minimize it again, then uncloak.
    // After this DWM holds a restored-size surface and the thumbnail shows
    // real content — the same trick the native taskbar/Alt+Tab relies on.
    private void PrepareMinimizedThenRegister()
    {
        // A quick Alt+` flick commits/cancels within milliseconds; the
        // restore dance serves only thumbnail generation and visibly
        // disturbs every minimized window. Never start it for a closed
        // overlay, and never start it twice concurrently.
        if (!_isOpen) return;
        RegisterLiveThumbnails(); // instant live previews — do NOT wait for the dance
        if (System.Threading.Interlocked.CompareExchange(
                ref _danceRunning, 1, 0) == 1) return;
        lock (DanceKeepLock) DanceKeepHwnds.Clear();
        // Windows we have danced before keep their restored-size DWM
        // surface while minimized — only never-danced ones need the dance.
        var minimized = _windows.Where(w => NativeMethods.IsIconic(w.Hwnd)
                && !s_dancedHwnds.Contains(w.Hwnd.ToInt64()))
            .Select(w => w.Hwnd).ToList();
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"minimized windows needing cloaked restore: {minimized.Count}");
        if (minimized.Count == 0)
        {
            RegisterLiveThumbnails();
            return;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Give the user a beat to flick-commit; a closed overlay
                // cancels the dance entirely.
                System.Threading.Thread.Sleep(300);
                if (!_isOpen) return;
                // Batch dance: restore ALL cloaked, wait once so slow
                // renderers (Zotero/GTK need several hundred ms) finish
                // painting, then minimize ALL. Time is O(1) in window count.
                // Cloak is DENIED (E_ACCESSDENIED) for elevated apps'
                // windows — restoring those would flash them on screen, so
                // only windows whose cloak succeeded get restored.
                var restored = new List<IntPtr>();
                foreach (var h in minimized)
                {
                    var cloak = 1;
                    var hrSet = NativeMethods.DwmSetWindowAttribute(
                        h, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));
                    if (hrSet == 0) restored.Add(h);
                    else WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"cloak denied, skipping restore (no flash) hwnd=0x{h.ToInt64():X}");
                }
                foreach (var h in restored)
                    NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWNOACTIVATE);
                System.Threading.Thread.Sleep(600);
                // Re-minimize and uncloak EVERYTHING we touched, even if
                // the overlay closed mid-dance. 只还原**我们真的 cloak 成功**
                // 的那些 — 对 cloak 被拒 (提权窗口) 的窗强制写 0 会把别的
                // 虚拟桌面上的窗口拉回当前桌面.
                var fg = NativeMethods.GetForegroundWindow();
                List<IntPtr> keep;
                lock (DanceKeepLock)
                {
                    keep = restored.Where(h => DanceKeepHwnds.Contains(h.ToInt64()) || h == fg)
                        .ToList();
                }
                foreach (var h in keep)
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"dance: keep restored (picked/foreground) hwnd=0x{h.ToInt64():X}");
                foreach (var h in restored.Except(keep))
                    NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWMINNOACTIVE);
                foreach (var h in restored)
                {
                    var cloak = 0;
                    NativeMethods.DwmSetWindowAttribute(
                        h, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));
                }
                System.Threading.Thread.Sleep(100);
                WindowSwitcherWpf.Services.Log.Info("Overlay",
                    $"cloaked restore done for {minimized.Count} windows");
            }
            catch (Exception ex)
            {
                WindowSwitcherWpf.Services.Log.Exception("Overlay.CloakRestore", ex);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _danceRunning, 0);
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var h in minimized)
                {
                    s_dancedHwnds.Add(h.ToInt64());
                    var ow = _windows.FirstOrDefault(w => w.Hwnd == h);
                    if (ow is not null) ow.HasLivePreview = true;
                }
            }), DispatcherPriority.ApplicationIdle);
        });
    }

    // Hwnds whose DWM surface was upgraded by the cloaked restore at least
    // once; a once-danced window keeps its restored-size surface while
    // minimized, so repeat opens never pay the dance cost again.
    private static readonly HashSet<long> s_dancedHwnds = new();

    // Static-capture caches: OverlayWindow instances are rebuilt on every
    // open; without these, every Alt+` re-captured all PrintWindow bitmaps
    // and previews visibly popped in one by one. UI-thread only. Hwnds are
    // recycled by Windows, so entries carry the owning pid and are dropped
    // when the hwnd now belongs to a different process.
    private sealed record CachedVisual(BitmapSource? Thumb, BitmapSource? Icon, int Pid);
    private static readonly Dictionary<IntPtr, CachedVisual> s_visualCache = new();

    private static CachedVisual? LookupCache(IntPtr hwnd)
    {
        if (!s_visualCache.TryGetValue(hwnd, out var v)) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pidU); var pid = (int)pidU;
        if (pid != v.Pid)
        {
            s_visualCache.Remove(hwnd); // hwnd recycled to another process
            return null;
        }
        return v;
    }

    private static void StoreCache(IntPtr hwnd, BitmapSource? thumb, BitmapSource? icon)
    {
        var existing = s_visualCache.TryGetValue(hwnd, out var v) ? v : null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pidU); var pid = (int)pidU;
        s_visualCache[hwnd] = new CachedVisual(
            thumb ?? existing?.Thumb,
            icon ?? existing?.Icon,
            pid);
    }

    private int _danceRunning;

    /// <summary>本次舞蹈不许再最小化的窗口 (用户已选中激活的).
    /// 开新舞蹈时清空.</summary>
    private static readonly HashSet<long> DanceKeepHwnds = new();
    private static readonly object DanceKeepLock = new();

    private void RegisterLiveThumbnails()
    {
        if (!_isOpen) return; // closed while the cloaked-restore dance ran
        UnregisterLiveThumbnails();
        if (PresentationSource.FromVisual(this) is not HwndSource src) return;

        void Register(IntPtr hwnd, OverlayWindow? vm, bool copy = false)
        {
            if (_liveThumbs.ContainsKey((hwnd, copy))) return;
            try
            {
                var hr = NativeMethods.DwmRegisterThumbnail(src.Handle, hwnd, out var id);
                if (hr != 0)
                {
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"dwm register failed hr=0x{hr:X} hwnd=0x{hwnd.ToInt64():X}");
                    return;
                }
                _liveThumbs[(hwnd, copy)] = id;
                // Never-danced minimized windows would show their tiny
                // minimized frame; keep the static/placeholder card up
                // until the dance upgrades the surface (~1s).
                if (vm is not null)
                    vm.HasLivePreview = !NativeMethods.IsIconic(hwnd)
                        || s_dancedHwnds.Contains(hwnd.ToInt64());
            }
            catch (Exception ex)
            {
                WindowSwitcherWpf.Services.Log.Exception("Overlay.Register", ex);
            }
        }

        foreach (var w in _windows)
            Register(w.Hwnd, w, w.IsCardCopy);
        // Cascade members show half-previews on the head card in the main
        // view — they need registrations even without a card of their own.
        if (!_controller.IsInStackView)
        {
            foreach (var w in _windows)
            {
                if (_controller.StackInfoFor(w.Hwnd) is not { } st) continue;
                foreach (var m in st.Members)
                    if (m.Hwnd != w.Hwnd) Register(m.Hwnd, null);
            }
        }
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"live thumbnails registered={_liveThumbs.Count}");
        UpdateLiveRects();
    }

    private void UnregisterLiveThumbnails()
    {
        foreach (var id in _liveThumbs.Values)
        {
            try { NativeMethods.DwmUnregisterThumbnail(id); } catch { }
        }
        _liveThumbs.Clear();
        foreach (var w in _windows) w.HasLivePreview = false;
    }

    /// <summary>Source window's FULL rect (window-relative, frame included)
    /// for thumbnail source-rect math. A minimized window's rect sits at
    /// -32000 with a degenerate size — fall back to the danced surface's
    /// RESTORED size from rcNormalPosition.</summary>
    private static NativeMethods.RECT WindowRectFor(IntPtr hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out var wr);
        var w = wr.Right - wr.Left;
        var h = wr.Bottom - wr.Top;
        if (w >= 50 && h >= 50 && wr.Left > -30000)
            return new NativeMethods.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        var wp = new NativeMethods.WINDOWPLACEMENT
        {
            length = (uint)System.Runtime.InteropServices.Marshal
                .SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        };
        if (NativeMethods.GetWindowPlacement(hwnd, ref wp))
        {
            var nr = wp.rcNormalPosition;
            return new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = nr.Right - nr.Left,
                Bottom = nr.Bottom - nr.Top,
            };
        }
        return new NativeMethods.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
    }

    /// <summary>Centered window sub-rect whose aspect matches the dest
    /// rect. DWM stretches source→dest, so without this a taller dest than
    /// the window aspect would distort the preview; with it the preview
    /// crops like the native Alt+Tab switcher instead.</summary>
    private static NativeMethods.RECT CropSourceFor(IntPtr hwnd, double destW, double destH)
    {
        var src = new NativeMethods.RECT { Left = 0, Top = 0 };
        var crc = WindowRectFor(hwnd);
        var cw = crc.Right - crc.Left;
        var ch = crc.Bottom - crc.Top;
        src.Right = cw;
        src.Bottom = ch;
        if (cw < 50 || ch < 50 || destW <= 0 || destH <= 0) return src;
        var target = destW / destH;
        if (cw / (double)ch > target)
        {
            var sw = ch * target;
            src.Left = (int)((cw - sw) / 2);
            src.Right = (int)((cw + sw) / 2);
        }
        else
        {
            var sh = cw / target;
            src.Top = (int)((ch - sh) / 2);
            src.Bottom = (int)((ch + sh) / 2);
        }
        return src;
    }

    private void UpdateLiveRects()
    {
        if (_liveThumbs.Count == 0) return;
        try
        {
            var origin = RootBorder.PointToScreen(new Point(0, 0));
            var cascadeDone = new HashSet<IntPtr>();
            foreach (var w in _windows)
            {
                // Stack heads render their members as a left→right cascade
                // (从左往右叠一半); every member gets a clipped rect there.
                // 复制卡 (-2/0号副本) 不是 cascade 头 — 真身卡 (排在后面)
                // 用自己的几何锚定成员缩略图, 否则 rect 全错位.
                if (!_controller.IsInStackView && !w.IsCardCopy
                    && _controller.StackInfoFor(w.Hwnd) is { } st && st.Members.Count >= 2)
                {
                    UpdateCascadeRects(w, st, origin, cascadeDone);
                    continue;
                }
                if (!_liveThumbs.TryGetValue((w.Hwnd, w.IsCardCopy), out var id)) continue;
                var el = FindThumbBorder(w);
                if (el is null || !el.IsLoaded) continue;
                var tl = el.PointToScreen(new Point(0, 0));
                // PointToScreen is PHYSICAL px while ActualWidth/Height are
                // DIPs — convert the size too, or thumbnails render at
                // 1/scale of the card on >100% monitors.
                var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                var destW = (int)(br.X - tl.X);
                var destH = (int)(br.Y - tl.Y);
                // NB: never set DWM_TNP_SOURCECLIENTAREA / fSourceClientAreaOnly —
                // it samples the raw redirection surface, where Mica/Acrylic
                // backdrop windows (DWMWA_SYSTEMBACKDROP_TYPE) have an
                // alpha-0 background, so the card shows a washed-out ghost.
                // Window-relative rcSource (frame included) is what native
                // Alt+Tab does and renders those windows correctly.
                var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                              | NativeMethods.DWM_TNP_VISIBLE
                              | NativeMethods.DWM_TNP_RECTSOURCE,
                    rcDestination = new NativeMethods.RECT
                    {
                        Left = (int)(tl.X - origin.X),
                        Top = (int)(tl.Y - origin.Y),
                        Right = (int)(tl.X - origin.X) + destW,
                        Bottom = (int)(tl.Y - origin.Y) + destH,
                    },
                    rcSource = CropSourceFor(w.Hwnd, destW, destH),
                    fVisible = true,
                    opacity = 255,
                };
                NativeMethods.DwmUpdateThumbnailProperties(id, ref props);
            }
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Overlay.UpdateRects", ex);
        }
    }

    private FrameworkElement? FindThumbBorder(OverlayWindow w)
    {
        if (WindowsList.ItemContainerGenerator.ContainerFromItem(w) is not ContentPresenter cp)
            return null;
        return WindowsList.ItemTemplate?.FindName("ThumbBorder", cp) as FrameworkElement;
    }

    /// <summary>Clip rects for a stack cascade (从左往右叠一半): members
    /// 0..n-2 show the LEFT HALF of their client area in the left half of
    /// their sub-card (the rest is covered by the next card), the last
    /// member is fully visible with the full client area.</summary>
    private void UpdateCascadeRects(OverlayWindow head, SwitcherController.WindowStackInfo st,
        Point origin, HashSet<IntPtr> done)
    {
        var headEl = FindThumbBorder(head);
        if (headEl is null || !headEl.IsLoaded) return;
        var n = st.Members.Count;
        for (var i = 0; i < n; i++)
        {
            var m = st.Members[i];
            done.Add(m.Hwnd);
            if (!_liveThumbs.TryGetValue((m.Hwnd, false), out var id)) continue;
            var el = i == 0 ? headEl
                : _cascadeSubs.TryGetValue(m.Hwnd, out var t) ? t : null;
            if (el is null || !el.IsLoaded) continue;
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            var width = br.X - tl.X;
            var height = br.Y - tl.Y;
            var last = i == n - 1;
            // Visible width: full for the last card, half otherwise.
            var visW = last ? width : width / 2;
            // Subs' el spans the whole card INCLUDING its WPF title strip —
            // shift the DWM rect below it so the preview doesn't paint over
            // the title text. The head's el is the bare thumb area (the
            // title strip lives outside it), so no shift there.
            var srcTop = i == 0 ? 0 : 32;
            double thumbTop = tl.Y - origin.Y + srcTop;
            double thumbHeight = height - srcTop;
            var flags = NativeMethods.DWM_TNP_RECTDESTINATION
                        | NativeMethods.DWM_TNP_VISIBLE;
            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                rcDestination = new NativeMethods.RECT
                {
                    Left = (int)(tl.X - origin.X),
                    Top = (int)thumbTop,
                    Right = (int)(tl.X - origin.X + visW),
                    Bottom = (int)(thumbTop + thumbHeight),
                },
                fVisible = true,
                opacity = 255,
            };
            if (last)
            {
                // No DWM_TNP_SOURCECLIENTAREA (see UpdateLiveRects) — it
                // ghosts Mica backdrop windows; window-relative rcSource
                // is the native-Alt+Tab behavior.
                flags |= NativeMethods.DWM_TNP_RECTSOURCE;
                props.rcSource = CropSourceFor(m.Hwnd, visW, thumbHeight);
            }
            else
            {
                // Source = left half of the WINDOW rect (window-relative,
                // frame included — same no-SOURCECLIENTAREA rule).
                flags |= NativeMethods.DWM_TNP_RECTSOURCE;
                var wrc = WindowRectFor(m.Hwnd);
                props.rcSource = new NativeMethods.RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = (wrc.Right - wrc.Left) / 2,
                    Bottom = wrc.Bottom - wrc.Top,
                };
            }
            props.dwFlags = flags;
            NativeMethods.DwmUpdateThumbnailProperties(id, ref props);
        }
    }

    // ---------- Cascade sub-cards (堆叠级联: 从左往右叠一半) ----------
    // The declarative template renders the HEAD card; members 1..n-1 are
    // injected as overlapping sub-cards offset by half a card each (later
    // children draw on top, so the last member is the fully visible face).
    private readonly Dictionary<IntPtr, Border> _cascadeSubs = new();

    private void BuildCascades()
    {
        try
        {
            // Detach any old cascade subs from their previous parent so the
            // new run can re-parent them into DropLayer cleanly.
            foreach (var old in _cascadeSubs.Values)
                if (old.Parent is Panel p) p.Children.Remove(old);
            _cascadeSubs.Clear();
            _subTools.Clear();
            foreach (var host in _headToolHosts.Values) DropLayer.Children.Remove(host);
            _headToolHosts.Clear();
            _mainTools.Clear();
            foreach (var b in _pinOutlines.Values) DropLayer.Children.Remove(b);
            _pinOutlines.Clear();
            if (!_isOpen) return;
            if (_controller.IsInStackView)
            {
                // 子页面 renders members as plain cards — no cascade builds
                // here, but LayoutCascades must still run so containers
                // carried over from the main page get their overhang margin
                // and narrow head title restored (否则卡1后留白 + "Wo…" 截断).
                LayoutCascades();
                return;
            }
            // Idempotence guard: the same head hwnd must yield exactly ONE
            // cascade. A duplicate flat entry would otherwise register the
            // second set of subs under the same dictionary keys, leaving
            // the first set as unpositioned orphans at (0,0).
            var seenHeads = new HashSet<IntPtr>();
            foreach (var ow in _windows)
            {
                if (ow.IsCardCopy) continue; // 复制卡 (-2 与 0号副本) 不承载 cascade — 真身卡承载
                if (_controller.StackInfoFor(ow.Hwnd) is not { } st
                    || st.Members.Count < 2) continue;
                if (!seenHeads.Add(ow.Hwnd))
                {
                    WindowSwitcherWpf.Services.Log.Info("Cascade.Build",
                        $"duplicate head skipped: 0x{ow.Hwnd.ToInt64():X}");
                    continue;
                }
            if (WindowsList.ItemContainerGenerator.ContainerFromItem(ow)
                is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("CardRoot", cp) is not Grid root) continue;
            // No more injecting subs into the head's CardRoot — the head
            // stays at its natural width (ThumbWidth + 16) and the subs
            // live in DropLayer (positioned by LayoutCascades).
            var cardW = ow.ThumbWidth + 16;
            var peek = cardW / 2;
            var accent = _accentMap.TryGetValue(ow.GroupKey, out var c)
                ? c : System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A);
            for (var i = 1; i < st.Members.Count; i++)
            {
                var m = st.Members[i];
                var sub = BuildCascadeCard(m, ow.ThumbWidth, cardW,
                    i * peek, accent, i == st.Members.Count - 1, ow.Hwnd);
                DropLayer.Children.Add(sub);
            }
            // The head zone (第一个子标签的可视区 [0, peek)) gets the same
            // hover tool pair as every other member — hosted in DropLayer so
            // it can sit at the patch's right end (the head CARD is only
            // thumb-wide; the tools must sit near x=peek, over the card).
            var headHost = new Border
            {
                // Opaque white patch (白色方框呈现固定/✕, same as the sub
                // strips) — it covers the title tail on hover, so the tools
                // stay readable over long titles.
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Visibility = Visibility.Collapsed,
            };
            var hsp = new StackPanel { Orientation = Orientation.Horizontal };
            var headPin = MakeCascadeTool(ow.Hwnd, "📌", "固定此子标签 (首个)",
                OnCascadeMemberPinClick, MemberPinBrush(ow.Hwnd));
            var headClose = MakeCascadeTool(ow.Hwnd, "✕", "关闭此窗口",
                OnCascadeCloseClick, SubCloseBrush);
            hsp.Children.Add(headPin);
            hsp.Children.Add(headClose);
            headHost.Child = hsp;
            DropLayer.Children.Add(headHost);
            _headToolHosts[ow.Hwnd] = headHost;
        }
        // Position the cascade subs in DropLayer coords once the head card
        // has its real screen rect. The first pass may not have all head
        // cards laid out yet (WrapPanel measures lazily), so LayoutCascades
        // re-queues itself at Background priority until every head's
        // ActualWidth > 0 or the pass cap is hit.
        _layoutPassesRemaining = 30;
        WindowSwitcherWpf.Services.Log.Info("Cascade.Build",
            $"built subs={_cascadeSubs.Count} hosts={_headToolHosts.Count} mainTools={_mainTools.Count}");
        Dispatcher.BeginInvoke(new Action(LayoutCascades), DispatcherPriority.Background);
        Dispatcher.BeginInvoke(new Action(LayoutCascades), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Cascade.Build", ex);
        }
    }

    /// <summary>Position every cascade sub in DropLayer coords using the
    /// head card's actual screen position. 从左往右叠半: sub i sits at
    /// i·peek OVER the previous card — later children (BuildCascades add
    /// order) draw on top, so the last member is the fully visible face
    /// and every earlier member shows only its left peek. The head's
    /// container gets (n-1)·peek of extra right margin so the WrapPanel
    /// reserves room for the spread and the cascade never paints over the
    /// neighbouring cards. Re-runs on every SizeChanged/ScrollChanged via
    /// the constructor hooks.</summary>
    private void LayoutCascades()
    {
        var pending = 0;
        var marginDirty = false;
        foreach (var ow in _windows)
        {
            if (ow.IsCardCopy) continue; // 复制卡 (-2 与 0号副本) 是纯展示卡 — 无overhang/工具/子卡
            if (WindowsList.ItemContainerGenerator.ContainerFromItem(ow)
                is not ContentPresenter cp) continue;
            var st = _controller.StackInfoFor(ow.Hwnd);
            var stacked = st is { Members.Count: >= 2 };
            // Reserve the cascade overhang (stacks) or restore the base
            // margin (dissolved stacks / plain windows). Base margin is
            // snapshotted once per container so repeated passes compose.
            if (!_baseContainerMargins.TryGetValue(cp, out var baseMargin))
                _baseContainerMargins[cp] = baseMargin = cp.Margin;
            var cardW = ow.ThumbWidth + 16;
            var peek = cardW / 2;
            // 子页面 (stack sub view) renders members as plain cards — the
            // cascade overhang must NOT be reserved there, or a blank gap
            // of (n-1)·peek follows the first card (用户实测 bug).
            var reserveOverhang = stacked && !_controller.IsInStackView;
            // Head title trims with "…" inside the exposed head patch
            // ([0, peek)) instead of being hard-cut by the first sub card
            // (badge 23 + stack badge 29 + icon 20 + margins 15 ≈ 87 left).
            if (WindowsList.ItemTemplate?.FindName("TitleText", cp) is TextBlock tt)
                tt.MaxWidth = reserveOverhang ? Math.Max(peek - 91, 30) : 230;
            var extra = reserveOverhang ? (st.Members.Count - 1) * peek : 0;
            var target = new Thickness(baseMargin.Left, baseMargin.Top,
                baseMargin.Right + extra, baseMargin.Bottom);
            if (cp.Margin.Right != target.Right)
            {
                cp.Margin = target;
                marginDirty = true;
            }
            if (!stacked || _controller.IsInStackView) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not Border headCard) continue;
            if (headCard.ActualWidth <= 0) { pending++; continue; }
            var headTl = headCard.TransformToVisual(DropLayer)
                .Transform(new Point(0, 0));
            for (var i = 1; i < st.Members.Count; i++)
            {
                if (!_cascadeSubs.TryGetValue(st.Members[i].Hwnd, out var sub))
                {
                    WindowSwitcherWpf.Services.Log.Info("Cascade.Layout",
                        $"sub missing: head=0x{ow.Hwnd.ToInt64():X} member=0x{st.Members[i].Hwnd.ToInt64():X} dict={_cascadeSubs.Count}");
                    continue;
                }
                Canvas.SetLeft(sub, headTl.X + i * peek);
                Canvas.SetTop(sub, headTl.Y);
                // Match the head card's height exactly so the folded row
                // reads as one aligned stack (the 200px default in
                // BuildCascadeCard is just the pre-layout fallback).
                if (headCard.ActualHeight > 0) sub.Height = headCard.ActualHeight;
            }
            // Head-zone tool host: right end of the exposed head patch
            // ([0, peek)), aligned with the title strip (top margin ~7px).
            if (_headToolHosts.TryGetValue(ow.Hwnd, out var host))
            {
                Canvas.SetLeft(host, headTl.X + peek - 52);
                Canvas.SetTop(host, headTl.Y + 5);
            }
        }
        // If any head wasn't laid out yet — or a margin change just
        // re-flowed the WrapPanel — queue another pass at Background
        // priority (cap at 30 passes).
        if ((pending > 0 || marginDirty) && _isOpen && !_controller.IsInStackView
            && _layoutPassesRemaining > 0)
        {
            _layoutPassesRemaining--;
            Dispatcher.BeginInvoke(new Action(LayoutCascades), DispatcherPriority.Background);
        }
        // Sub positions feed the DWM destination rects — refresh them once
        // the layout pass triggered by the Canvas.SetLeft/Top calls above
        // has actually moved the subs (PointToScreen still returns the OLD
        // position until layout runs, so this must be queued after it).
        Dispatcher.BeginInvoke(new Action(UpdateLiveRects), DispatcherPriority.Loaded);
        // Pin frames follow the union geometry — but only once the final
        // positions are known (after UpdateLiveRects' own layout settle).
        Dispatcher.BeginInvoke(new Action(UpdatePinOutlines), DispatcherPriority.Loaded);
    }
    private int _layoutPassesRemaining;
    private readonly Dictionary<ContentPresenter, Thickness> _baseContainerMargins = new();

    /// <summary>One overlapped member card: title strip + half-visible
    /// live preview. Plain click = 直达, drag = that member (peel/re-stack).
    /// Every member carries hover tools at its region's right end; the LAST
    /// member additionally carries the MAIN stack tools (深色, rightmost).</summary>
    private Border BuildCascadeCard(WindowEntry m, double thumbW, double cardW,
        double offsetX, System.Windows.Media.Color accent, bool last, IntPtr headHwnd)
    {
        // Sub card lives in DropLayer, positioned absolutely by
        // LayoutCascades using Canvas.SetLeft/Top. A Canvas does NOT
        // stretch its children, so the sub needs an explicit Height to
        // match the head card's title+thumb area; otherwise it renders as
        // a 0-height sliver and is invisible.
        //   title strip ≈ 32px (icon row) + 150 thumb + ~14 margins ≈ 196.
        var peek = cardW / 2;
        // The strip is a Grid so the tool cluster RIGHT-aligns to the
        // member's visible region edge (用户实测: 主📌/✕ 要贴 last 卡右缘,
        // 不跟在标题后). The last sub renders the full cardW row with FOUR
        // 20px tools (子📌✕ + 主📌✕, 88px + buffer = 125); other members'
        // regions are one peek wide with two tools (44px + buffer = 72).
        var stripWidth = last ? Math.Max(cardW - 16, 60) : Math.Max(peek - 8, 40);
        var titleRoom = last ? Math.Max(stripWidth - 125, 30) : Math.Max(stripWidth - 72, 30);
        var thumbRoom = last ? Math.Max(thumbW, peek + 20) : Math.Max(peek, 60);
        const double subHeight = 250; // title strip (32) + thumb (200) + bottom margin (14) + breathing room
        var strip = new Grid
        {
            Width = stripWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 7, 0, 7),
        };
        var icon = new Image
        {
            Width = 20, Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Source = LookupCache(m.Hwnd)?.Icon,
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon,
            System.Windows.Media.BitmapScalingMode.HighQuality);
        strip.Children.Add(icon);
        strip.Children.Add(new TextBlock
        {
            Text = m.Title, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(27, 0, 0, 0),
            MaxWidth = titleRoom,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Border? pinTool = null, closeTool = null;
        // Every member's visible region carries HOVER-ONLY tools at its
        // region's right top corner (区域右上角). The last member appends
        // the MAIN stack tools (主📌 深黄固定态 / 主✕ 深红解散) rightmost —
        // 指向任意子标签时它们一起亮起.
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pinTool = MakeCascadeTool(m.Hwnd, "📌", "固定此子标签",
            OnCascadeMemberPinClick, MemberPinBrush(m.Hwnd));
        closeTool = MakeCascadeTool(m.Hwnd, "✕", "关闭此窗口",
            OnCascadeCloseClick, SubCloseBrush);
        pinTool.Opacity = 0;
        closeTool.Opacity = 0;
        tools.Children.Add(pinTool);
        tools.Children.Add(closeTool);
        if (last)
        {
            var mainPin = MakeCascadeTool(headHwnd, "📌", "固定/取消固定整个堆叠",
                OnCascadePinClick, StackPinBrush(headHwnd));
            var mainClose = MakeCascadeTool(headHwnd, "✕", "解散堆叠 (取消所有子标签固定)",
                OnCascadeDissolveClick, MainCloseBrush);
            mainPin.Opacity = 0;
            mainClose.Opacity = 0;
            tools.Children.Add(mainPin);
            tools.Children.Add(mainClose);
            _mainTools[headHwnd] = (mainPin, mainClose);
        }
        strip.Children.Add(tools);
        var thumb = new Border
        {
            Height = 200,
            Width = thumbRoom,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(7, 0, 7, 7),
            CornerRadius = new CornerRadius(6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0xEF, 0xEF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };
        var ph = new Image
        {
            Width = 44, Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Source = LookupCache(m.Hwnd)?.Icon,
        };
        thumb.Child = new Grid { Children = { ph } };
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        panel.Children.Add(strip);
        panel.Children.Add(thumb);
        var sub = new Border
        {
            Width = cardW,
            Height = subHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(8),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = DefaultSubBorderBrush,
            BorderThickness = new Thickness(1),
            ClipToBounds = !last,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = m.Hwnd,
            ToolTip = m.Title,
            Child = panel,
        };
        _cascadeSubs[m.Hwnd] = sub;
        sub.MouseLeftButtonDown += OnCascadeDown;
        // Subs live in DropLayer (sibling of RootBorder), so they steal
        // MouseMove from the RootBorder. OnCascadeSubMove forwards to the
        // hover tracker and the drag handler.
        sub.MouseMove += OnCascadeSubMove;
        sub.MouseLeftButtonUp += OnCascadeUp;
        if (pinTool is not null && closeTool is not null)
            _subTools[sub] = (pinTool, closeTool);
        return sub;
    }

    /// <summary>Sub hover tracking — subs are in DropLayer, so they
    /// intercept MouseMove from RootBorder. Re-resolve the active sub
    /// from the cursor position so the previous highlight is cleared
    /// even when the pointer jumps between overlapping subs.</summary>
    private void OnCascadeSubMove(object sender, MouseEventArgs e)
    {
        UpdateCascadeHoverFromPointer(e.GetPosition(WindowsList));
        // Forward to the drag handler so the drag system still sees motion.
        OnCardMouseMove(sender, e);
    }

    private static Border MakeCascadeTool(IntPtr hwnd, string glyph, string tip,
        MouseButtonEventHandler up, System.Windows.Media.Brush? fg = null)
    {
        var b = new Border
        {
            Width = 20, Height = 20, Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = tip,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = hwnd,
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += up;
        b.Child = new TextBlock
        {
            Text = glyph, FontSize = 12,
            Foreground = fg ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x61, 0x61, 0x61)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return b;
    }

    // Tool palette (工具配色): sub tools read light, MAIN tools read dark —
    // 悬停时一眼区分主/子标签的固定与叉叉. Pinned states turn yellow:
    // 子固定=黄, 主固定=更深的黄.
    private static System.Windows.Media.Brush Rgb(byte r, byte g, byte b) =>
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

    private static readonly System.Windows.Media.Brush SubCloseBrush = Rgb(0x61, 0x61, 0x61);
    private static readonly System.Windows.Media.Brush MainCloseBrush = Rgb(0xB3, 0x26, 0x1E); // 解散=深红
    private static readonly System.Windows.Media.Brush MemberPinPinnedBrush = Rgb(0xD9, 0x77, 0x06);   // 子固定=黄
    private static readonly System.Windows.Media.Brush MemberPinFreeBrush = Rgb(0x75, 0x75, 0x75);     // 子未固定=中灰 (纯白在白卡上不可见 — 用户实测反馈)
    private static readonly System.Windows.Media.Brush StackPinPinnedBrush = Rgb(0xB4, 0x53, 0x09);    // 主固定=深黄
    private static readonly System.Windows.Media.Brush StackPinFreeBrush = Rgb(0x33, 0x33, 0x33);      // 主未固定=深灰

    private System.Windows.Media.Brush MemberPinBrush(IntPtr h) =>
        _controller.IsMemberPinned(h) ? MemberPinPinnedBrush : MemberPinFreeBrush;

    private System.Windows.Media.Brush StackPinBrush(IntPtr head) =>
        _controller.IsStackPinned(head) ? StackPinPinnedBrush : StackPinFreeBrush;

    /// <summary>Digit shown for a relative member index: 1..9, 0 = 10th
    /// (same mapping SelectByDigit uses for Alt+digit).</summary>
    internal static string RelativeMemberDigit(int index) =>
        index >= 9 ? "0" : (index + 1).ToString();

    private void OnCascadeDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // must not arm the whole-card (head) drag
        _dragItem = null;
        _dragHwnd = IntPtr.Zero;
        _dragActive = false;
        _dragFromFanStrip = false;
        if (sender is FrameworkElement fe && fe.Tag is IntPtr h && h != IntPtr.Zero)
        {
            // Cascade sub-cards only exist for members 1..n-1: a drag
            // carries that member alone (same drop semantics as the old
            // fan-edge drags), the head region drags the whole stack.
            _dragHwnd = h;
            _dragFromFanStrip = true;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnCascadeUp(object sender, MouseButtonEventArgs e)
    {
        var wasDrag = _dragActive;
        var dragHwnd = _dragHwnd;
        EndDrag(); // also hides drop indicators
        if (!wasDrag)
        {
            if (sender is FrameworkElement fe && fe.Tag is IntPtr h
                && _controller.SelectByHwnd(h)) Commit();
            e.Handled = true;
            return;
        }
        // Mouse capture routes the release here no matter where the cursor
        // ended up, so sender no longer identifies the target — resolve it
        // from the grid-space pointer position instead.
        if (dragHwnd != IntPtr.Zero)
            DropFanCardAt(e.GetPosition(WindowsList), dragHwnd);
        e.Handled = true;
    }

    /// <summary>Release resolver for fan-strip/cascade-member drags. The
    /// folded cascade's span is checked first (member-precise insert or
    /// same-stack reorder; releasing on the dragged member itself is a
    /// no-op), then a lone card (join its stack); off the cards, the
    /// release peels the member out (剥离). Shared by OnCascadeUp and
    /// OnOverlayMouseUp so routing can never double-handle.</summary>
    private void DropFanCardAt(Point p, IntPtr dragHwnd)
    {
        if (CascadeAt(p) is { } c)
        {
            var tl = c.headCard.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var r = CascadeRegion(c.st, c.head.ThumbWidth + 16, p.X - tl.X);
            var movedC = false;
            if (r is { } reg && c.st.Members[reg.index].Hwnd != dragHwnd)
                movedC = _controller.StackDrop(dragHwnd, c.st.Members[reg.index].Hwnd, reg.before);
            // A pin-rejected drop must NOT rebuild — the fresh containers
            // would replace the flashed pin frame (同 OnOverlayMouseUp).
            if (movedC) RebuildFromSlots(preserveScroll: true);
            return;
        }
        var card = CardAt(p);
        if (card is not null && card.DataContext is OverlayWindow t
            && t.Hwnd != dragHwnd)
        {
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var relPt = new Point(p.X - pos.X, p.Y - pos.Y);
            // Same guard: rejected drop keeps the flash alive, no rebuild.
            if (_controller.StackDrop(dragHwnd, t.Hwnd, before: relPt.X < card.ActualWidth / 2))
                RebuildFromSlots(preserveScroll: true);
            return;
        }
        if (_controller.PeelFromStack(dragHwnd))
            RebuildFromSlots(preserveScroll: true);
    }

    /// <summary>Which cascade member region the point sits in, and whether
    /// it is that member's insert-before half. Geometry matches
    /// LayoutCascades exactly: peek = cardW/2, sub i's visible region is
    /// [i·peek, (i+1)·peek) and the last member's region is a full cardW
    /// wide. Left half of a region = insert before that member, right half
    /// = insert after. Shared by the drag preview (the line indicator) and
    /// the drop so they can never disagree.</summary>
    private static (int index, bool before)? CascadeRegion(
        SwitcherController.WindowStackInfo st, double cardW, double x)
    {
        var n = st.Members.Count;
        if (n == 0 || cardW <= 0) return null;
        var peek = cardW / 2;
        var spanX = (n - 1) * peek + cardW;
        if (x < 0 || x >= spanX) return null;
        var i = Math.Min((int)(x / peek), n - 1);
        var regionW = i == n - 1 ? cardW : peek;
        return (i, x - i * peek < regionW / 2);
    }

    /// <summary>The folded cascade under a grid point: head card + the sub
    /// peeks that reach (n-1)·peek past the head card, where CardAt finds
    /// nothing. Member drags resolve here FIRST so drops on later subs
    /// insert into the stack instead of falling through to 剥离.</summary>
    private (Border headCard, OverlayWindow head,
        SwitcherController.WindowStackInfo st)? CascadeAt(Point p)
    {
        foreach (var ow in _windows)
        {
            if (_controller.StackInfoFor(ow.Hwnd) is not { } st
                || st.Members.Count < 2) continue;
            if (WindowsList.ItemContainerGenerator.ContainerFromItem(ow)
                is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not Border headCard) continue;
            if (headCard.ActualWidth <= 0) continue;
            var tl = headCard.TransformToVisual(WindowsList)
                .Transform(new Point(0, 0));
            var cardW = ow.ThumbWidth + 16;
            var peek = cardW / 2;
            var spanX = (st.Members.Count - 1) * peek + cardW;
            if (p.X < tl.X || p.X >= tl.X + spanX
                || p.Y < tl.Y || p.Y > tl.Y + headCard.ActualHeight) continue;
            return (headCard, ow, st);
        }
        return null;
    }

    // ---------- Cascade hover (悬停哪个成员 = 选中哪个) ----------
    // The cascade shows only half of each covered member, so the pointer
    // position decides the target: the hovered member's full card border
    // turns blue, and a plain click activates exactly that member.
    private static readonly System.Windows.Media.Brush CascadeHoverBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly System.Windows.Media.Brush DefaultSubBorderBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0, 0, 0));

    // Border currently highlighted (background-only), or null. Cleared on
    // every new hover so the old one doesn't linger when the pointer
    // jumps to a different member (WPF MouseLeave doesn't always fire on
    // rapid jumps).
    private Border? _hoverSub;
    private IntPtr _hoverHwnd;

    private static readonly System.Windows.Media.Brush HoveredSubBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEA, 0xF3, 0xFF));

    /// <summary>Highlight the entire sub-card for one member, or — when
    /// stackOutline=true — draw a single blue border around the cascade's
    /// outermost edge. Two independent visual states; only one is active
    /// at a time (outline takes precedence).</summary>
    private void SetCascadeHover(IntPtr hwnd, bool on, bool headZone = false)
    {
        if (on)
        {
            // Always restore the previous sub's background first (cheap
            // when re-entering same hwnd). This is the single source of
            // truth — WPF MouseLeave doesn't always fire on rapid jumps.
            if (_hoverSub is not null && _hoverSub != _hoverSubFor(hwnd))
                RestoreCascadeSub(_hoverSub);
            _hoverSub = null;
            _hoverHwnd = IntPtr.Zero;
            HideAllStackTools(except: hwnd);

            if (headZone)
            {
                // Head patch hover: the head zone's own 📌/✕ (DropLayer
                // host at the patch's right end) + the MAIN stack tools on
                // the last sub. No blue union outline — the permanent pin
                // frame and the selection frame own the borders.
                if (_headToolHosts.TryGetValue(hwnd, out var host))
                    host.Visibility = Visibility.Visible;
                if (_mainTools.TryGetValue(hwnd, out var m))
                {
                    m.pin.Opacity = 1;
                    m.close.Opacity = 1;
                }
                return;
            }

            // Sub hover = subtle background highlight + that sub's 📌/✕ +
            // the MAIN stack tools (指向任意子标签 → 主标签标志都出现).
            if (_cascadeSubs.TryGetValue(hwnd, out var sub))
            {
                sub.Background = HoveredSubBackground;
                if (_subTools.TryGetValue(sub, out var t))
                {
                    t.pin.Opacity = 1;
                    t.close.Opacity = 1;
                }
                _hoverSub = sub;
                _hoverHwnd = hwnd;
                var head = StackHeadOf(hwnd);
                if (head != IntPtr.Zero && _mainTools.TryGetValue(head, out var mt))
                {
                    mt.pin.Opacity = 1;
                    mt.close.Opacity = 1;
                }
            }
        }
        else
        {
            // Clear the previous highlight unconditionally on any off call.
            // The global pointer tracker (UpdateCascadeHoverFromPointer)
            // calls off with the previous hwnd OR with Zero to mean "not
            // over any sub" — both must release the highlight.
            if (_hoverHwnd == hwnd || hwnd == IntPtr.Zero)
            {
                if (_hoverSub is not null) RestoreCascadeSub(_hoverSub);
                _hoverSub = null;
                _hoverHwnd = IntPtr.Zero;
                HideAllStackTools(except: IntPtr.Zero);
            }
        }
    }

    /// <summary>Hide every stack's hover tool groups (head-zone hosts via
    /// Visibility so they stop intercepting head-card clicks; main tools on
    /// the last sub via opacity), optionally sparing one stack.</summary>
    private void HideAllStackTools(IntPtr except)
    {
        foreach (var (head, host) in _headToolHosts)
            host.Visibility = head == except ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (head, m) in _mainTools)
        {
            var o = head == except ? 1.0 : 0.0;
            m.pin.Opacity = o;
            m.close.Opacity = o;
        }
    }

    /// <summary>The stack head that owns <paramref name="memberHwnd"/> as a
    /// folded member, Zero when it is not a cascade member.</summary>
    private IntPtr StackHeadOf(IntPtr memberHwnd)
    {
        foreach (var ow in _windows)
            if (_controller.StackInfoFor(ow.Hwnd) is { } st
                && st.Members.Any(m => m.Hwnd == memberHwnd))
                return ow.Hwnd;
        return IntPtr.Zero;
    }

    // ---------- Stack outer outline ----------
    // The hover-time blue union outline was removed (取消悬停蓝框): borders
    // now belong to the selection frame and the permanent pin frame only.
    // The union-rect math below survives as StackOutlineRect, feeding the
    // permanent pin outlines.

    /// <summary>Union rect of the head card + every sub card, in DropLayer
    /// coords with a 2px outset — the cascade's outermost edge. Shared by
    /// the hover outline (蓝) and the permanent pin outlines (橙).</summary>
    private (double x, double y, double w, double h)? StackOutlineRect(
        FrameworkElement root, OverlayWindow headVm, SwitcherController.WindowStackInfo st)
    {
        var headTl = root.TransformToVisual(WindowsList).Transform(new Point(0, 0));
        var headBr = root.TransformToVisual(WindowsList).Transform(
            new Point(root.ActualWidth, root.ActualHeight));
        double minX = headTl.X, minY = headTl.Y, maxX = headBr.X, maxY = headBr.Y;
        for (var i = 1; i < st.Members.Count; i++)
        {
            if (!_cascadeSubs.TryGetValue(st.Members[i].Hwnd, out var sub)) continue;
            var tl = sub.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var br = sub.TransformToVisual(WindowsList).Transform(
                new Point(sub.ActualWidth, sub.ActualHeight));
            minX = Math.Min(minX, tl.X);
            minY = Math.Min(minY, tl.Y);
            maxX = Math.Max(maxX, br.X);
            maxY = Math.Max(maxY, br.Y);
        }
        var origin = DropLayer.TransformToVisual(WindowsList).Inverse.Transform(new Point(0, 0));
        return (minX - origin.X - 2, minY - origin.Y - 2,
            (maxX - minX) + 4, (maxY - minY) + 4);
    }

    // ---------- Permanent pin outlines (固定橙框, 恒显) ----------
    // One orange frame per PINNED stack, drawn in DropLayer over the folded
    // cascade; refreshed after every layout pass. Plain pinned cards get
    // their frame from the template trigger instead.
    private readonly Dictionary<IntPtr, Border> _pinOutlines = new();

    private static readonly System.Windows.Media.Brush PinOutlineBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));

    private Border EnsurePinOutline(IntPtr hwnd)
    {
        if (!_pinOutlines.TryGetValue(hwnd, out var b))
        {
            b = new Border
            {
                BorderBrush = PinOutlineBrush,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(10),
                Background = System.Windows.Media.Brushes.Transparent,
                IsHitTestVisible = false,
            };
            DropLayer.Children.Add(b);
            _pinOutlines[hwnd] = b;
        }
        return b;
    }

    /// <summary>Re-sync the permanent pin frames with the current windows:
    /// pinned stacks get their union-rect outline, stale ones are removed.</summary>
    private void UpdatePinOutlines()
    {
        var keep = new HashSet<IntPtr>();
        foreach (var ow in _windows)
        {
            if (_controller.StackInfoFor(ow.Hwnd) is not { Members.Count: >= 2 } st) continue;
            if (!_controller.IsStackPinned(ow.Hwnd)) continue;
            if (WindowsList.ItemContainerGenerator.ContainerFromItem(ow)
                is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not Border headCard) continue;
            if (headCard.ActualWidth <= 0) continue;
            if (StackOutlineRect(headCard, ow, st) is not { } r) continue;
            var b = EnsurePinOutline(ow.Hwnd);
            Canvas.SetLeft(b, r.x);
            Canvas.SetTop(b, r.y);
            b.Width = r.w;
            b.Height = r.h;
            b.Visibility = Visibility.Visible;
            keep.Add(ow.Hwnd);
        }
        foreach (var k in _pinOutlines.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            DropLayer.Children.Remove(_pinOutlines[k]);
            _pinOutlines.Remove(k);
        }
    }

    // ---------- 拒绝反馈: 叮 + 突显固定外框 ----------
    // A move that would displace a pinned card is rejected by the
    // controller (固定卡编号不可被挤走). Make the reason visible: a ding,
    // then the blocking pin's frame flashes red for a few seconds.

    private static readonly System.Windows.Media.Brush PinFlashBrush =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE8, 0x11, 0x23));

    private void OnMoveRejectedByPin(IntPtr pinHwnd)
    {
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"move rejected feedback: ding + flash pin hwnd=0x{pinHwnd.ToInt64():X}");
        System.Media.SystemSounds.Asterisk.Play(); // 叮
        FlashPin(pinHwnd);
    }

    /// <summary>Resolve the visual that owns a pin's frame — the permanent
    /// union outline (pinned stack), the cascade sub card (pinned member),
    /// or the template card (pinned plain card) — and flash it red.</summary>
    private void FlashPin(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Border? b = null;
        if (_pinOutlines.TryGetValue(hwnd, out var outline)) b = outline;
        else if (_cascadeSubs.TryGetValue(hwnd, out var sub)) b = sub;
        else if (_windows.FirstOrDefault(w => w.Hwnd == hwnd) is { } ow
            && WindowsList.ItemContainerGenerator.ContainerFromItem(ow) is ContentPresenter cp
            && WindowsList.ItemTemplate?.FindName("Card", cp) is Border card) b = card;
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"flash pin 0x{hwnd.ToInt64():X}: target={(b is null ? "NOT FOUND" : b.Name is "Card" ? "template card" : b is { } x ? x.GetType().Name : "?")}");
        if (b is null) return;

        // Remember how each border value was set: template-card borders are
        // style-trigger driven (restore via ClearValue so triggers regain
        // control); code-built borders hold local values (restore by
        // re-assigning the captured originals).
        var localBrush = b.ReadLocalValue(Border.BorderBrushProperty);
        var localThickness = b.ReadLocalValue(Border.BorderThicknessProperty);
        var brushIsLocal = localBrush != DependencyProperty.UnsetValue;
        var thicknessIsLocal = localThickness != DependencyProperty.UnsetValue;
        var oldThickness = b.BorderThickness;
        b.BorderBrush = PinFlashBrush;
        b.BorderThickness = new Thickness(Math.Max(6, oldThickness.Left + 2));

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            if (brushIsLocal) b.BorderBrush = (System.Windows.Media.Brush)localBrush;
            else b.ClearValue(Border.BorderBrushProperty);
            if (thicknessIsLocal) b.BorderThickness = (Thickness)localThickness;
            else b.ClearValue(Border.BorderThicknessProperty);
            t.Stop();
        };
        t.Start();
    }

    /// <summary>The template card's root Grid (where cascade sub-cards are
    /// injected). Equivalent to the existing FindName("CardRoot") path but
    /// we need the panel itself for the union rect.</summary>
    private FrameworkElement? FindCascadeContainer(OverlayWindow headVm)
    {
        if (WindowsList.ItemContainerGenerator.ContainerFromItem(headVm)
            is not ContentPresenter cp) return null;
        return WindowsList.ItemTemplate?.FindName("CardRoot", cp) as Grid;
    }

    private Border? _hoverSubFor(IntPtr hwnd) =>
        _cascadeSubs.TryGetValue(hwnd, out var s) ? s : null;

    private static readonly System.Windows.Media.Brush DefaultSubBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));

    private void RestoreCascadeSub(Border b)
    {
        b.Background = DefaultSubBackground;
        // Hover tools (📌/✕) live at the end of the sub's visible strip;
        // they only show while the sub is hovered (一般不显示).
        if (_subTools.TryGetValue(b, out var t))
        {
            t.pin.Opacity = 0;
            t.close.Opacity = 0;
        }
    }

    // Per-sub hover tools: pin = member pin (子标签固定), ✕ = close that
    // window. Keyed by the sub card border; cleared on every rebuild.
    private readonly Dictionary<Border, (Border pin, Border close)> _subTools = new();

    // MAIN stack tools (主标签📌/✕) living at the end of the LAST sub's
    // strip; shown while ANY member of that stack is hovered. Keyed by the
    // stack head hwnd.
    private readonly Dictionary<IntPtr, (Border pin, Border close)> _mainTools = new();

    // Head-zone hover tools (第一个子标签区域的📌/✕) hosted in DropLayer;
    // positioned by LayoutCascades at the patch's right end. Collapsed
    // unless that stack's head zone is hovered.
    private readonly Dictionary<IntPtr, Border> _headToolHosts = new();

    /// <summary>Resolve which cascade member (if any) the cursor sits over,
    /// then update the single highlighted sub. Geometry matches
    /// LayoutCascades exactly: the cascade spans
    /// [head.x, head.x + (n-1)·peek + cardW), sub i owns [i·peek, (i+1)·peek)
    /// and the last member owns the widened tail. Called on every
    /// RootBorder mouse move so hover state is always in sync — eliminates
    /// the stale-highlight bug where sub-A stays blue after the cursor
    /// jumps to sub-B without WPF firing sub-A's MouseLeave.</summary>
    private void UpdateCascadeHoverFromPointer(Point p)
    {
        if (_controller.IsInStackView) { SetCascadeHover(IntPtr.Zero, false); return; }
        foreach (var ow in _windows)
        {
            if (_controller.StackInfoFor(ow.Hwnd) is not { } st
                || st.Members.Count < 2) continue;
            if (WindowsList.ItemContainerGenerator.ContainerFromItem(ow)
                is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not Border headCard) continue;
            if (headCard.ActualWidth <= 0) continue;
            var tl = headCard.TransformToVisual(WindowsList)
                .Transform(new Point(0, 0));
            var cardW = ow.ThumbWidth + 16;
            var peek = cardW / 2;
            var spanX = (st.Members.Count - 1) * peek + cardW;
            if (p.X < tl.X || p.X >= tl.X + spanX
                || p.Y < tl.Y || p.Y > tl.Y + headCard.ActualHeight) continue;
            var localX = p.X - tl.X;
            var target = st.Members[0].Hwnd; // head: the leftmost exposed patch
            for (var i = 1; i < st.Members.Count; i++)
            {
                var sStart = i * peek;
                var sEnd = i == st.Members.Count - 1 ? spanX : sStart + peek;
                if (localX >= sStart && localX < sEnd)
                {
                    target = st.Members[i].Hwnd;
                    break;
                }
            }
            if (target == st.Members[0].Hwnd)
            {
                // Head patch: the head zone's tools + the main stack tools.
                SetCascadeHover(target, true, headZone: true);
            }
            else
            {
                SetCascadeHover(target, true);
            }
            return;
        }
        SetCascadeHover(IntPtr.Zero, false);
    }

    /// <summary>Main-stack pin on the last sub (固定/取消固定整个堆叠);
    /// Tag carries the head hwnd (DropLayer tools have no DataContext).</summary>
    private void OnCascadePinClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntPtr h } && h != IntPtr.Zero)
        {
            _controller.TogglePin(h);
            RebuildFromSlots(preserveScroll: true);
            e.Handled = true;
        }
    }

    /// <summary>Main-stack ✕ = 解散堆叠 (drop the grouping, keep every
    /// window — 取消主标签的固定则默认取消子标签的所有的固定).</summary>
    private void OnCascadeDissolveClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntPtr h } && h != IntPtr.Zero)
        {
            _controller.DissolveStack(h);
            RebuildFromSlots(preserveScroll: true);
            e.Handled = true;
        }
    }

    /// <summary>Sub-card member pin (子标签固定): the controller auto-pins
    /// the parent stack first (联动 — 主标签固定是子固定的前提).</summary>
    private void OnCascadeMemberPinClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntPtr h } && h != IntPtr.Zero)
        {
            _controller.ToggleMemberPin(h);
            RebuildFromSlots(preserveScroll: true);
            e.Handled = true;
        }
    }

    private void OnCascadeCloseClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntPtr h } && h != IntPtr.Zero)
            CloseWindowGracefully(h);
        e.Handled = true;
    }

    private void CloseWindowGracefully(IntPtr hwnd)
    {
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        WindowSwitcherWpf.Services.Log.Info("Overlay", $"close posted hwnd=0x{hwnd:X}");
        // Apps tear down asynchronously; refresh the grid once they do.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_isOpen) return;
            _controller.OpenOverlay();
            Rebuild();
        };
        t.Start();
    }


    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        // During a drag the mouse capture routes every MouseUp to the drag
        // card itself, so sender can no longer identify the drop target —
        // release handling lives in OnOverlayMouseUp (geometry-based).
        if (_dragActive) return;
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
        {
            if (EditMode)
            {
                // 编辑模式: 点卡只选中 (看边框/状态), 不切窗口.
                _controller.SelectByHwnd(ow.Hwnd);
                RebuildSelection();
                e.Handled = true;
                return;
            }
            // The controller's selection must follow the mouse, otherwise
            // Commit() would activate the keyboard-selected window instead.
            if (_controller.SelectByHwnd(ow.Hwnd)) Commit();
        }
    }

    // ---------- Drag: reorder / 集中 (stack) / 剥离 (peel) ----------
    // Draggable: every card except slot 0 (the special most-recent) and
    // pinned ones — inside a stack sub view everything is draggable.
    // Drops: outer edge bands reorder, the center stacks onto the target,
    // gaps reorder at the gap, and in the sub view (or from a fan edge)
    // dropping outside the grid peels the member out of its stack.
    private OverlayWindow? _dragItem;
    private IntPtr _dragHwnd;
    private Point _dragStart;
    private bool _dragActive;
    private bool _dragFromFanStrip;
    private FrameworkElement? _captureElt;

    private bool CanDrag(OverlayWindow ow)
    {
        // 副本卡 (-2 当前窗口 / 0 号副本) 是同 hwnd 真身的别名, 拖它会按
        // hwnd 解析到另一张卡 — 编辑模式内外都不可拖.
        if (ow.IsCurrentCopy || ow.IsSlot0Copy) return false;
        // 编辑模式: 固定规则全解除 (拖固定卡 = 取消固定), 主页面任意卡可拖.
        if (EditMode) return !_controller.IsInStackView;
        return !_controller.IsMemberPinned(ow.Hwnd)
            && (_controller.IsInStackView || (ow.SlotBadge != "0" && !ow.IsPinned));
    }

    /// <summary>Pin/close presses must not arm the card drag.</summary>
    private void OnToolDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _dragHwnd = IntPtr.Zero;
        _dragActive = false;
        _dragFromFanStrip = false;
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow && CanDrag(ow))
        {
            _dragItem = ow;
            _dragHwnd = ow.Hwnd;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHwnd == IntPtr.Zero || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragActive &&
            Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) > 8)
        {
            _dragActive = true;
            if (DragCard(_dragItem) is { } b) b.Opacity = 0.55;
            // Capture on the element that raised the move (template Card or
            // cascade sub-card): events keep routing here — and bubbling to
            // the overlay release handlers — even when the cursor leaves the
            // card or the window, so an outside release still drops / peels
            // instead of leaving the drag armed forever.
            _captureElt = sender as FrameworkElement;
            _captureElt?.CaptureMouse();
        }
    }

    /// <summary>Single drop resolver for every drag release. Geometry-based
    /// (CardAt / grid bounds) so it stays correct while the mouse capture
    /// routes the events to the drag card — including releases outside the
    /// window. Zones on a card: 排序 bands / 集中 center (main), all-reorder
    /// (sub view); off-card: peel (剥离) for fan-strip and sub-view-outside,
    /// gap insert otherwise.</summary>
    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive) { SetDesktopDropTarget(null); EndDrag(); return; }
        var dragHwnd = _dragHwnd;
        var fromStrip = _dragFromFanStrip;
        EndDrag();
        if (dragHwnd == IntPtr.Zero) return;
        // 拖到桌面槽位行上 = 把窗口迁到那个桌面 (迁走后它离开当前桌面, 面板
        // 里这张卡随之消失). 不限编辑模式 —— 只看有没有 ≥2 个桌面, 不看模式.
        if (!_controller.IsInStackView && _desktops.Count >= 2)
        {
            var chip = DesktopDropChipAt(e.GetPosition(this));
            if (chip is not null)
            {
                if (chip.Id == VirtualDesktop.CurrentGuid)
                {
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"migrate window → {chip.Label} 取消 (就在这个桌面)");
                }
                else if (_controller.MoveToDesktop(dragHwnd, chip.Id))
                {
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"migrate window 0x{dragHwnd.ToInt64():X} → {chip.Label} ok");
                    RefreshDesktops();
                    RebuildFromSlots(preserveScroll: true);
                }
                else
                {
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"migrate window 0x{dragHwnd.ToInt64():X} → {chip.Label} 失败");
                }
                return;
            }
        }
        var p = e.GetPosition(WindowsList);
        if (fromStrip)
        {
            // A cascade member carries just itself: the release resolves
            // member-precise on a cascade span (including its own — a
            // self-drop is a no-op), joins a lone card's stack (集中), or
            // peels off the cards (剥离). Shared with OnCascadeUp so both
            // routings behave identically.
            DropFanCardAt(p, dragHwnd);
            return;
        }
        // Plain-card drags over a cascade span also resolve member-precise
        // (一步到位): the drop inserts at the member boundary under the
        // pointer. The head card's left 25% stays the reorder band (move
        // the dragged card before the whole cascade); dropping a head on
        // its own stack is a no-op via the same-member guard. Sub view has
        // no cascades — skip there. 编辑模式下一律重排 (不识别堆叠区),
        // 否则会把拖拽变成"集中", 用户明明在排序.
        if (!_controller.IsInStackView && !EditMode && CascadeAt(p) is { } c)
        {
            var ctl = c.headCard.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var relX = p.X - ctl.X;
            bool movedC;
            if (!_dragFromFanStrip && relX < c.headCard.ActualWidth * 0.25)
            {
                // Reorder band: place the dragged card before the cascade.
                movedC = _controller.MoveRelative(dragHwnd, c.head.Hwnd, before: true);
            }
            else if (CascadeRegion(c.st, c.head.ThumbWidth + 16, relX) is { } r
                && c.st.Members[r.index].Hwnd != dragHwnd)
            {
                movedC = _controller.StackDrop(dragHwnd, c.st.Members[r.index].Hwnd, r.before);
            }
            else movedC = false;
            // A rejected move (固定锚定) changed nothing — don't rebuild,
            // or the fresh containers would wipe the pin-flash feedback.
            if (movedC) RebuildFromSlots(preserveScroll: true);
            return;
        }
        var inGrid = InCardGrid(p);
        var card = CardAt(p);
        if (card is not null && card.DataContext is OverlayWindow target)
        {
            if (target.Hwnd == dragHwnd) return; // released on itself: no-op
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var rel = (p.X - pos.X) / Math.Max(card.ActualWidth, 1);
            bool movedT;
            if (EditMode)
            {
                // 编辑模式: 整张卡就是排序区 — 左半插前, 右半插后. 拖固定卡
                // = 取消固定, 要固定回新位置再点 📌.
                movedT = _controller.MoveRelativeEdit(dragHwnd, target.Hwnd, before: rel < 0.5);
            }
            else if (_controller.IsInStackView)
            {
                // Sub view: every card is a member of the same stack, so any
                // card drop reorders members (StackDropCore's same-stack
                // branch) — left half lands before, right half after.
                movedT = _controller.StackDrop(dragHwnd, target.Hwnd, before: rel < 0.5);
            }
            else
            {
                // Zones: outer 25% bands = 排序 (before/after), middle
                // = 集中 (drop onto the card → stack).
                if (rel < 0.25)
                    movedT = _controller.MoveRelative(dragHwnd, target.Hwnd, before: true);
                else if (rel > 0.75)
                    movedT = _controller.MoveRelative(dragHwnd, target.Hwnd, before: false);
                else
                    movedT = _controller.StackDrop(dragHwnd, target.Hwnd, before: false);
            }
            if (movedT) RebuildFromSlots(preserveScroll: true);
            return;
        }
        if (fromStrip || (_controller.IsInStackView && !inGrid))
        {
            // Off the cards in peel mode (cascade member dragged out, or
            // sub view outside the grid): the release detaches it.
            if (_controller.PeelFromStack(dragHwnd))
                RebuildFromSlots(preserveScroll: true);
            return;
        }
        var drop = FindGapInsert(p);
        if (drop is not { } d) return;
        var moved = _controller.IsInStackView
            ? _controller.StackDrop(dragHwnd, d.target, d.before) // member reorder
            : EditMode
                ? _controller.MoveRelativeEdit(dragHwnd, d.target, d.before)
                : _controller.MoveRelative(dragHwnd, d.target, d.before);
        if (moved) RebuildFromSlots(preserveScroll: true);
    }

    /// <summary>Nearest card on the cursor's row: dropping left of its
    /// center inserts before it, right inserts after — the 间隙 drop.</summary>
    private (FrameworkElement card, IntPtr target, bool before)? FindGapInsert(Point p)
    {
        FrameworkElement? best = null;
        double bestD = double.MaxValue;
        var before = false;
        for (var i = 0; i < _windows.Count; i++)
        {
            if (WindowsList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not FrameworkElement card) continue;
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var cy = pos.Y + card.ActualHeight / 2;
            if (Math.Abs(p.Y - cy) > card.ActualHeight) continue; // different row
            var d = Math.Abs(p.X - (pos.X + card.ActualWidth / 2));
            if (d >= bestD) continue;
            bestD = d;
            best = card;
            before = p.X < pos.X + card.ActualWidth / 2;
        }
        return best?.DataContext is OverlayWindow ow ? (best, ow.Hwnd, before) : null;
    }

    private void EndDrag()
    {
        if (_dragItem is not null && DragCard(_dragItem) is { } b) b.Opacity = 1;
        _captureElt?.ReleaseMouseCapture(); // no-op when not captured
        _captureElt = null;
        _dragItem = null;
        _dragHwnd = IntPtr.Zero;
        _dragActive = false;
        _dragFromFanStrip = false;
        SetDesktopDropTarget(null);
        HideDropIndicators();
    }

    // ---------- Drag feedback (拖拽动效: 左/右竖线、里面高亮、剥离) ----------
    // The DropLayer Canvas mirrors every drag position: a blue vertical
    // line at the target card's left/right edge (排序), a group-colored
    // glow around the card (里面 = 集中), and a 剥离 chip trailing the
    // cursor when the release would detach the member from its stack.
    private Border? _glowCard;

    private void OnOverlayDragMove(object sender, MouseEventArgs e)
    {
        // Hover tracking on every cursor move: subs live in DropLayer
        // (their own hit-test target steals MouseMove from the RootBorder),
        // so we can't rely on RootBorder-only events. The single source of
        // truth is the cursor's grid position — re-resolve the active sub
        // on every move so the previous one is guaranteed to clear.
        UpdateCascadeHoverFromPointer(e.GetPosition(WindowsList));

        if (!_dragActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        // 指针压在桌面槽位行上 = 准备迁桌面, 不是排序. 先把"插到哪张卡
        // 前面"的预览全部撤掉 (它在同一个 Grid.Row 里, 会盖住槽位), 只留
        // 一颗亮起来的 chip 当靶子 — 不然用户只会看到左上角有个"最近标签"
        // 的插入预览, 以为要丢在那儿.
        if (!_controller.IsInStackView && _desktops.Count >= 2)
        {
            var chip = DesktopDropChipAt(e.GetPosition(this));
            SetDesktopDropTarget(chip);
            if (chip is not null)
            {
                HideDropIndicators();
                return;
            }
        }
        SetDesktopDropTarget(null);
        var p = e.GetPosition(WindowsList);
        if (!_controller.IsInStackView && CascadeAt(p) is { } c)
        {
            // Drag in hand over a cascade's folded span: preview the exact
            // member boundary the drop will insert at. The span reaches
            // (n-1)·peek past the head card where CardAt finds nothing, so
            // this must resolve before the CardAt lookup. Plain-card drags
            // keep the head's left 25% as a reorder band (line at the
            // stack's left edge = move before the whole cascade).
            var tl = c.headCard.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var cardW = c.head.ThumbWidth + 16;
            if (CascadeRegion(c.st, cardW, p.X - tl.X) is { } r)
            {
                var peek = cardW / 2;
                var regionW = r.index == c.st.Members.Count - 1 ? cardW : peek;
                HideStackGlow();
                ShowLine(!_dragFromFanStrip && p.X - tl.X < c.headCard.ActualWidth * 0.25
                    ? tl.X
                    : tl.X + r.index * peek + (r.before ? 0 : regionW),
                    tl.Y, c.headCard.ActualHeight);
                return;
            }
        }
        var card = CardAt(p);
        if (card is not null)
        {
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            double? lineX = null;
            {
                var rel = (p.X - pos.X) / Math.Max(card.ActualWidth, 1);
                if (rel < 0.25) lineX = pos.X;                         // 左竖线
                else if (rel > 0.75) lineX = pos.X + card.ActualWidth; // 右竖线
                else { ShowStackGlow(card); HideLine(); return; }      // 里面
            }
            HideStackGlow();
            ShowLine(lineX.Value, pos.Y, card.ActualHeight);
            return;
        }
        HideLine();
        HideStackGlow();
        if ((_dragFromFanStrip && !_controller.IsInStackView)
            || (_controller.IsInStackView && !InCardGrid(p)))
        {
            // Off the cards in peel mode (cascade member dragged out, or
            // sub view outside the card area): the release will detach it.
            // Clamped to the layer so the chip stays visible even when the
            // cursor itself has left the window (capture keeps the drag).
            var pr = e.GetPosition(DropLayer);
            PeelChip.Visibility = Visibility.Visible;
            var cx = pr.X + 14;
            var cy = pr.Y + 12;
            cx = Math.Max(4, Math.Min(cx, DropLayer.ActualWidth - PeelChip.ActualWidth - 4));
            cy = Math.Max(4, Math.Min(cy, DropLayer.ActualHeight - PeelChip.ActualHeight - 4));
            Canvas.SetLeft(PeelChip, cx);
            Canvas.SetTop(PeelChip, cy);
            return;
        }
        if (FindGapInsert(p) is { } g)
        {
            var gpos = g.card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            ShowLine(g.before ? gpos.X : gpos.X + g.card.ActualWidth,
                     gpos.Y, g.card.ActualHeight);
        }
    }

    /// <summary>The template card under the grid point, if any.</summary>
    private Border? CardAt(Point p)
    {
        for (var i = 0; i < _windows.Count; i++)
        {
            if (WindowsList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not Border card) continue;
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            if (p.X >= pos.X && p.X <= pos.X + card.ActualWidth
                && p.Y >= pos.Y && p.Y <= pos.Y + card.ActualHeight)
                return card;
        }
        return null;
    }

    /// <summary>True while the point stays near the card area (union of the
    /// template cards + 24px slack). Peel-off (剥离) is judged against this,
    /// not the WindowsList rect — the ItemsControl rectangle fills the whole
    /// panel, which would make "outside the grid" unreachable.</summary>
    private bool InCardGrid(Point p)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
        for (var i = 0; i < _windows.Count; i++)
        {
            if (WindowsList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (WindowsList.ItemTemplate?.FindName("Card", cp) is not FrameworkElement card) continue;
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            minX = Math.Min(minX, pos.X);
            minY = Math.Min(minY, pos.Y);
            maxX = Math.Max(maxX, pos.X + card.ActualWidth);
            maxY = Math.Max(maxY, pos.Y + card.ActualHeight);
        }
        if (maxX <= 0) return false;
        return p.X >= minX - 24 && p.X <= maxX + 24
            && p.Y >= minY - 24 && p.Y <= maxY + 24;
    }

    private void ShowLine(double x, double y, double h)
    {
        LineIndicator.Visibility = Visibility.Visible;
        Canvas.SetLeft(LineIndicator, x - 2);
        Canvas.SetTop(LineIndicator, y);
        LineIndicator.Height = h;
    }

    private void HideLine() => LineIndicator.Visibility = Visibility.Collapsed;

    private void ShowStackGlow(Border card)
    {
        if (ReferenceEquals(_glowCard, card)) return;
        HideStackGlow();
        card.BorderBrush = card.DataContext is OverlayWindow ow
            && _accentMap.TryGetValue(ow.GroupKey, out var c)
                ? new System.Windows.Media.SolidColorBrush(c)
                : System.Windows.Media.Brushes.DodgerBlue;
        card.BorderThickness = new Thickness(3);
        _glowCard = card;
    }

    private void HideStackGlow()
    {
        if (_glowCard is null) return;
        _glowCard.ClearValue(Border.BorderBrushProperty);
        _glowCard.ClearValue(Border.BorderThicknessProperty);
        _glowCard = null;
    }

    private void HideDropIndicators()
    {
        HideLine();
        HideStackGlow();
        PeelChip.Visibility = Visibility.Collapsed;
    }

    // ---------- Fan strip drag (主页面堆叠的侧边条 = 非首位成员) ----------
    // (Replaced by the cascade sub-cards — OnCascadeDown/OnCascadeUp.)

    private Border? DragCard(OverlayWindow w) =>
        WindowsList.ItemContainerGenerator.ContainerFromItem(w) is ContentPresenter cp
            ? WindowsList.ItemTemplate?.FindName("Card", cp) as Border
            : null;

    private void OnPinClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
        {
            WindowSwitcherWpf.Services.Log.Info("CardEvent", $"pinclick '{ow.Title}' " +
                $"hwnd=0x{ow.Hwnd.ToInt64():X} stackView={_controller.IsInStackView}");
            // Sub view (alt+数字进入): the card IS a member — pin it as
            // such; main view pins the top-level slot. The controller
            // recomputes and persists; the overlay re-renders from it.
            if (_controller.IsInStackView) _controller.ToggleMemberPin(ow.Hwnd);
            else _controller.TogglePin(ow.Hwnd);
            RebuildFromSlots(preserveScroll: true);
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
        {
            // Main view: ✕ on a stack head = 解散堆叠 (drop the grouping,
            // keep every window — 主标签的❌删掉主标签里的所有子标签);
            // any other card closes that window. Sub view: always close
            // that member's window (never dissolve from inside).
            if (!_controller.IsInStackView
                && _controller.StackInfoFor(ow.Hwnd) is { Members.Count: >= 2 })
                _controller.DissolveStack(ow.Hwnd);
            else
                CloseWindowGracefully(ow.Hwnd);
            e.Handled = true;
        }
    }
}

public sealed class OverlayWindow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public IntPtr Hwnd { get; }
    public string ProcessName { get; }
    public string Title { get; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
    public string GroupKey { get; }
    public int IndexInGroup { get; }
    public string ModulePath { get; }
    /// <summary>当前窗口的复制卡 (Windows Alt+Tab 左起第一张): 纯展示 —
    /// 不可拖/不承载 cascade/不被数字寻址; badge 显示真实编号.</summary>
    public bool IsCurrentCopy { get; init; }
    /// <summary>上一窗口 (Slot=0) 本身是 pin 卡时的显示副本 — 真身保留在
    /// 编号区 (固定=一直存在). 可被选中/点击 (quick tap 提交目标), 但不
    /// 承载 cascade、缩略图注册走副本 key 位.</summary>
    public bool IsSlot0Copy { get; init; }
    /// <summary>缩略图注册/cascade 头判定的"副本"位: 同 hwnd 双卡 (副本 +
    /// 真身) 靠它拿两个独立的 DWM 注册 — (hwnd, true)=副本, (hwnd, false)
    /// =真身.</summary>
    public bool IsCardCopy => IsCurrentCopy || IsSlot0Copy;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    // Slot badge "0".."10": 0 is the special most-recent slot, 1..10 the
    // managed normal slots. Empty = unmanaged (no badge shown).
    private string _slotBadge = "";
    public string SlotBadge
    {
        get => _slotBadge;
        set
        {
            if (_slotBadge == value) return;
            _slotBadge = value;
            PropertyChanged?.Invoke(this, new(nameof(SlotBadge)));
            PropertyChanged?.Invoke(this, new(nameof(BadgeVisibility)));
        }
    }
    public System.Windows.Visibility BadgeVisibility =>
        string.IsNullOrEmpty(SlotBadge) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>位置编号 (Slot). -2 = 当前窗口副本, -1 = 无编号 (溢出).</summary>
    public int SlotValue { get; init; } = -2;

    /// <summary>窗口所在虚拟桌面的角标 ("" = 当前桌面, 不显示). 面板是全局
    /// 标签列表, 别的桌面的窗口必须标出来, 否则点不动也不知道为什么.</summary>
    public string DesktopBadge { get; init; } = "";
    public System.Windows.Visibility DesktopBadgeVisibility =>
        string.IsNullOrEmpty(DesktopBadge) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>单独指派的一次直达号码; null = 没指派或就等于位置号
    /// (= 旧行为, 不显示额外信息).</summary>
    public int? QuickNumber { get; init; }

    /// <summary>角标文本: 手动设过快捷号就一直显示 — 相同也显示 "5(5)"
    /// (用户: "我自己改的要一直显示"); 没设过就只显示位置号.</summary>
    public string BadgeText =>
        QuickNumber is { } q && !string.IsNullOrEmpty(SlotBadge)
            ? $"{SlotBadge}({q})"
            : SlotBadge;

    /// <summary>角标里独立的 "(快捷号)" 部分 (位置号之后). 没手动设过 →
    /// 空; 进了改号态还没输数字 → "(?)" 闪着.</summary>
    public string QuickSuffix =>
        QuickNumber is { } q && !string.IsNullOrEmpty(SlotBadge)
            ? $"({q})"
            : IsQuickEditing ? "(?)" : "";
    public System.Windows.Visibility QuickBadgeVisibility =>
        QuickSuffix.Length == 0
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>正在等数字给这张卡设快捷号 — 模板里括号部分闪烁 + chip
    /// 描琥珀边 (用户: "点一下应该出现闪烁状态, 输完数字停").</summary>
    public bool IsQuickEditing { get; init; }

    // Pinned windows keep their slot number; they are skipped by smart
    // sort and cannot be dragged (enforced in the S4 drag model).
    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new(nameof(PinBrush)));
        }
    }

    // Pin glyph: blue when pinned, gray when not — driven by a brush so
    // the template only needs one TextBlock.
    public System.Windows.Media.Brush PinBrush =>
        IsPinned ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06))
                 : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A));

    // ---- Group accent (色码层) ----
    // Zero-footprint categorization: each GroupKey maps to one palette
    // hue (stable FNV-1a hash — pure structure, no app names). The hue
    // shows as the card's 3px top strip + badge tint; hovering a card
    // lights up its whole group so related windows are findable without
    // moving anything (网格不动, 留空率不变).
    private System.Windows.Media.Color _accent =
        System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A);
    private bool _isGroupHighlighted;

    public bool IsGroupHighlighted
    {
        get => _isGroupHighlighted;
        set
        {
            if (_isGroupHighlighted == value) return;
            _isGroupHighlighted = value;
            PropertyChanged?.Invoke(this, new(nameof(IsGroupHighlighted)));
            PropertyChanged?.Invoke(this, new(nameof(GroupStripBrush)));
            PropertyChanged?.Invoke(this, new(nameof(GroupBadgeBrush)));
        }
    }

    public System.Windows.Media.Brush GroupStripBrush =>
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(
            (byte)(_isGroupHighlighted ? 0xE0 : 0x50),
            _accent.R, _accent.G, _accent.B));

    public System.Windows.Media.Brush GroupBadgeBrush =>
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(
            (byte)(_isGroupHighlighted ? 0x45 : 0x1E),
            _accent.R, _accent.G, _accent.B));

    public void SetGroupAccent(System.Windows.Media.Color c)
    {
        if (_accent == c) return;
        _accent = c;
        PropertyChanged?.Invoke(this, new(nameof(GroupStripBrush)));
        PropertyChanged?.Invoke(this, new(nameof(GroupBadgeBrush)));
    }

    // ---- Stack card (堆叠) ----
    // A stack head renders one card for the WHOLE stack: an ×N badge next
    // to the slot digit. The members themselves render as a left→right
    // cascade of half-overlapped live previews (从左往右叠一半) — built
    // imperatively in BuildCascades, NOT here (the VM stays fan-free).
    private int _stackCount;
    public int StackCount
    {
        get => _stackCount;
        set
        {
            if (_stackCount == value) return;
            _stackCount = value;
            PropertyChanged?.Invoke(this, new(nameof(StackCount)));
            PropertyChanged?.Invoke(this, new(nameof(StackBadge)));
            PropertyChanged?.Invoke(this, new(nameof(StackBadgeVisibility)));
        }
    }

    public string StackBadge => StackCount >= 2 ? "×" + StackCount : "";

    public System.Windows.Visibility StackBadgeVisibility =>
        StackCount >= 2 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new(nameof(ThumbVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(PlaceholderVisibility)));
        }
    }

    // Card thumbnail area width: the thumb box is 200px tall (native
    // Alt+Tab tile height) and live previews center-crop to fill it via
    // DWM RECTSOURCE; the width formula keeps the 150 base so card WIDTHS
    // (and therefore the panel width, tuned to match Alt+Tab) stay put.
    public double ThumbWidth { get; }

    // True while a DWM live thumbnail is composited over the card — both
    // the static capture and the icon placeholder are hidden because DWM
    // draws the real window surface on top of the gray border.
    private bool _hasLive;
    public bool HasLivePreview
    {
        get => _hasLive;
        set
        {
            if (_hasLive == value) return;
            _hasLive = value;
            PropertyChanged?.Invoke(this, new(nameof(HasLivePreview)));
            PropertyChanged?.Invoke(this, new(nameof(ThumbVisibility)));
            PropertyChanged?.Invoke(this, new(nameof(PlaceholderVisibility)));
        }
    }

    public Visibility ThumbVisibility =>
        (Thumbnail is not null && !_hasLive) ? Visibility.Visible : Visibility.Collapsed;

    // The icon placeholder stays visible even when a DWM thumbnail is
    // registered: it acts as an underlay. DWM draws over it for windows
    // that render; for blank-rendering minimized windows (Microsoft
    // Store) the icon shows through instead of a white rectangle.
    public Visibility PlaceholderVisibility =>
        Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    private BitmapSource? _icon;
    public BitmapSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
        }
    }

    public OverlayWindow(WindowEntry entry, string groupKey, int index)
    {
        Hwnd = entry.Hwnd;
        ProcessName = entry.ProcessName;
        Title = entry.Title;
        GroupKey = groupKey;
        IndexInGroup = index;
        ModulePath = entry.ModulePath;

        var aspect = 1.6;
        if (NativeMethods.GetWindowRect(entry.Hwnd, out var r))
        {
            var ww = r.Right - r.Left;
            var wh = r.Bottom - r.Top;
            if (ww > 100 && wh > 100) aspect = (double)ww / wh;
        }
        ThumbWidth = Math.Round(150 * Math.Clamp(aspect, 0.6, 2.6));
    }
}

/// <summary>One fan-strip layer of a stack card: a colored sliver standing
/// for the member behind the face card (click activates it, dragging it
/// away peels it out of the stack).</summary>

/// <summary>虚拟桌面槽位行的一格: 序号 + GUID + 显示名 + 是否当前桌面.
/// GUID 是稳定标识 (桌面序号会因删桌面漂移, GUID 不会).</summary>
public sealed class DesktopChip : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isCurrent;

    public DesktopChip(int index, Guid id, string label, bool isCurrent, bool canDelete)
    {
        Index = index;
        Id = id;
        Label = label;
        CanDelete = canDelete;
        _isCurrent = isCurrent;
    }

    /// <summary>拖拽时指针压在这颗 chip 上 = 落点 (亮起来当靶子).</summary>
    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            PropertyChanged?.Invoke(this, new(nameof(IsDropTarget)));
        }
    }

    /// <summary>只剩一个桌面时不能删 — ✕ 隐藏.</summary>
    public bool CanDelete { get; }
    public System.Windows.Visibility DeleteVisibility =>
        CanDelete ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>0 基序号 (与 VirtualDesktop.DesktopInfo.Index 一致).</summary>
    public int Index { get; }

    /// <summary>虚拟桌面 GUID.</summary>
    public Guid Id { get; }

    /// <summary>显示文本 ("桌面 2" / "2 我的工作").</summary>
    public string Label { get; }

    /// <summary>是否当前桌面 (高亮).</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
