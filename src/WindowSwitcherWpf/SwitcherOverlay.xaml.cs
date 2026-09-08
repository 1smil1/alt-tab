using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        InitializeComponent();
        DataContext = this;
        WindowsList.ItemsSource = _windows;

        // Native Alt+Tab panel nearly fills the screen center; content
        // beyond these caps scrolls (draggable scrollbar on the right).
        MaxWidth = SystemParameters.WorkArea.Width * 0.94;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;

        // Any movement of the window or its viewport changes the
        // client-coordinate destination rects of the DWM thumbnails.
        LocationChanged += (_, _) => UpdateLiveRects();
        SizeChanged += (_, _) => UpdateLiveRects();
        Scroller.ScrollChanged += (_, _) => UpdateLiveRects();

        _thumbTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _thumbTimer.Tick += (_, _) => RefreshThumbnails();
    }

    public ObservableCollection<OverlayWindow> AllWindows => _windows;

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
            _controller.OpenOverlay();
            Rebuild();
            RefreshHeaderUI();
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
            WindowSwitcherWpf.Services.Log.Info("Overlay", $"shown groups={_controller.Groups.Count} hwnd={new System.Windows.Interop.WindowInteropHelper(this).Handle:X}");
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
        if (!_isOpen) return;
        _thumbTimer.Stop();
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
        Hide();
        EnterIdleMode();
    }

    // Tray-app memory discipline: the overlay just did its bitmap burst;
    // drop visual-cache entries for windows that no longer exist and trim
    // the working set so idle RAM pages out to the pagefile.
    private void EnterIdleMode()
    {
        var dead = new List<IntPtr>();
        foreach (var kv in s_visualCache)
        {
            NativeMethods.GetWindowThreadProcessId(kv.Key, out var pidU);
            if (pidU == 0) dead.Add(kv.Key);
        }
        foreach (var h in dead) s_visualCache.Remove(h);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        NativeMethods.SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
        WindowSwitcherWpf.Services.Log.Info("Overlay", "idle: cache trimmed, working set requested");
    }

    public void SelectDigit(int digit)
    {
        _controller.SelectByDigit(digit);
        RebuildSelection();
    }

    private void Rebuild() => RebuildFromSlots(preserveScroll: false);

    // ---------- Header UI (右上角: 模板 / 智能排序 / 导出) ----------

    private bool _headerBusy; // guards SelectionChanged during programmatic fill

    /// <summary>Fill the template/sort combos from config.json. Keeps the
    /// previous selection when the lists still contain it.</summary>
    private void RefreshHeaderUI()
    {
        var cfg = _controller.Config;
        _headerBusy = true;
        try
        {
            TemplateCombo.Items.Clear();
            foreach (var t in cfg.Templates) TemplateCombo.Items.Add(t.Name);
            if (TemplateCombo.Items.Count > 0) TemplateCombo.SelectedIndex = 0;

            SortCombo.Items.Clear();
            foreach (var m in cfg.SortMethods) SortCombo.Items.Add(m.Name);
            if (SortCombo.Items.Count > 0) SortCombo.SelectedIndex = -1;
        }
        finally
        {
            _headerBusy = false;
        }
    }

    private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_headerBusy || TemplateCombo.SelectedIndex < 0) return;
        var name = TemplateCombo.SelectedItem as string;
        var template = _controller.Config.Templates.Find(t => t.Name == name);
        if (template is null) return;
        WindowSwitcherWpf.Services.Log.Info("Overlay", $"template pick '{name}'");
        _controller.ApplyTemplate(template);
        RebuildFromSlots(preserveScroll: true);
    }

    private void OnSortSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_headerBusy || SortCombo.SelectedIndex < 0) return;
        ApplySelectedSort();
    }

    private void OnSortApply(object sender, RoutedEventArgs e)
    {
        if (SortCombo.SelectedIndex < 0)
        {
            SortCombo.SelectedIndex = 0;
            return;
        }
        ApplySelectedSort();
    }

    private void ApplySelectedSort()
    {
        var name = SortCombo.SelectedItem as string;
        var method = _controller.Config.SortMethods.Find(m => m.Name == name);
        if (method is null) return;
        WindowSwitcherWpf.Services.Log.Info("Overlay", $"smart sort pick '{name}'");
        _controller.ApplySmartSort(method);
        RebuildFromSlots(preserveScroll: true);
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var name = "导出-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
        var n = _controller.ExportCurrentAsTemplate(name);
        RefreshHeaderUI();
        // Point the combo at what we just exported so re-applying is easy.
        var idx = -1;
        for (var i = 0; i < TemplateCombo.Items.Count; i++)
            if ((string)TemplateCombo.Items[i] == name) { idx = i; break; }
        _headerBusy = true;
        TemplateCombo.SelectedIndex = idx;
        _headerBusy = false;
        WindowSwitcherWpf.Services.Log.Info("Overlay", $"export '{name}' slots={n}");
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
                    IsPinned = sw.IsPinned,
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
            var desired = new HashSet<IntPtr>(_windows.Select(w => w.Hwnd));
            if (!_controller.IsInStackView)
                foreach (var w in _windows)
                    if (_controller.StackInfoFor(w.Hwnd) is { } st2)
                        foreach (var m in st2.Members)
                            desired.Add(m.Hwnd);
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

    /// <summary>Main page header (模板/排序) vs sub view header (堆叠标题 +
    /// 操作提示) — only one is visible at a time.</summary>
    private void UpdateHeaderForView()
    {
        var sub = _controller.IsInStackView;
        MainHeader.Visibility = sub ? Visibility.Collapsed : Visibility.Visible;
        StackHeader.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
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

    private void RebuildSelection()
    {
        for (var i = 0; i < _windows.Count; i++)
            _windows[i].IsSelected = false;
        if (_controller.Nav.Count == 0) return;
        var entry = _controller.Nav[Math.Min(_controller.ActiveFlatIndex, _controller.Nav.Count - 1)];
        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i].Hwnd == entry.Hwnd) { _windows[i].IsSelected = true; break; }
        }
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
            // layout so the window resizes to the new cap.
            MaxWidth = workW * 0.94;
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

    private readonly Dictionary<IntPtr, IntPtr> _liveThumbs = new();

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
                // the overlay closed mid-dance.
                foreach (var h in restored)
                    NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWMINNOACTIVE);
                foreach (var h in minimized)
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

    private void RegisterLiveThumbnails()
    {
        if (!_isOpen) return; // closed while the cloaked-restore dance ran
        UnregisterLiveThumbnails();
        if (PresentationSource.FromVisual(this) is not HwndSource src) return;

        void Register(IntPtr hwnd, OverlayWindow? vm)
        {
            if (_liveThumbs.ContainsKey(hwnd)) return;
            try
            {
                var hr = NativeMethods.DwmRegisterThumbnail(src.Handle, hwnd, out var id);
                if (hr != 0)
                {
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"dwm register failed hr=0x{hr:X} hwnd=0x{hwnd.ToInt64():X}");
                    return;
                }
                _liveThumbs[hwnd] = id;
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
            Register(w.Hwnd, w);
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
                if (!_controller.IsInStackView
                    && _controller.StackInfoFor(w.Hwnd) is { } st && st.Members.Count >= 2)
                {
                    UpdateCascadeRects(w, st, origin, cascadeDone);
                    continue;
                }
                if (!_liveThumbs.TryGetValue(w.Hwnd, out var id)) continue;
                var el = FindThumbBorder(w);
                if (el is null || !el.IsLoaded) continue;
                var tl = el.PointToScreen(new Point(0, 0));
                // PointToScreen is PHYSICAL px while ActualWidth/Height are
                // DIPs — convert the size too, or thumbnails render at
                // 1/scale of the card on >100% monitors.
                var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                var left = (int)(tl.X - origin.X);
                var top = (int)(tl.Y - origin.Y);
                var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                              | NativeMethods.DWM_TNP_VISIBLE
                              | NativeMethods.DWM_TNP_SOURCECLIENTAREA,
                    rcDestination = new NativeMethods.RECT
                    {
                        Left = left,
                        Top = top,
                        Right = left + (int)(br.X - tl.X),
                        Bottom = top + (int)(br.Y - tl.Y),
                    },
                    fVisible = true,
                    fSourceClientAreaOnly = true,
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
            if (!_liveThumbs.TryGetValue(m.Hwnd, out var id)) continue;
            var el = i == 0 ? headEl
                : _cascadeThumbs.TryGetValue(m.Hwnd, out var t) ? t : null;
            if (el is null || !el.IsLoaded) continue;
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            var width = br.X - tl.X;
            var height = br.Y - tl.Y;
            var last = i == n - 1;
            // Visible width: full for the last card, half otherwise.
            var visW = last ? width : width / 2;
            var flags = NativeMethods.DWM_TNP_RECTDESTINATION
                        | NativeMethods.DWM_TNP_VISIBLE;
            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                rcDestination = new NativeMethods.RECT
                {
                    Left = (int)(tl.X - origin.X),
                    Top = (int)(tl.Y - origin.Y),
                    Right = (int)(tl.X - origin.X + visW),
                    Bottom = (int)(tl.Y - origin.Y + height),
                },
                fVisible = true,
                opacity = 255,
            };
            if (last)
            {
                flags |= NativeMethods.DWM_TNP_SOURCECLIENTAREA;
                props.fSourceClientAreaOnly = true;
            }
            else
            {
                // Source = left half of the client area, same aspect as the
                // visible half — a true "half window" preview.
                flags |= NativeMethods.DWM_TNP_RECTSOURCE;
                NativeMethods.GetClientRect(m.Hwnd, out var crc);
                props.rcSource = new NativeMethods.RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = (int)((crc.Right - crc.Left) / 2),
                    Bottom = crc.Bottom - crc.Top,
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
    private readonly Dictionary<IntPtr, Border> _cascadeThumbs = new();

    private void BuildCascades()
    {
        _cascadeThumbs.Clear();
        WindowSwitcherWpf.Services.Log.Info("Cascade",
            $"build open={_isOpen} stackView={_controller.IsInStackView} windows={_windows.Count}");
        if (!_isOpen || _controller.IsInStackView) return;
        foreach (var ow in _windows)
        {
            if (_controller.StackInfoFor(ow.Hwnd) is not { } st
                || st.Members.Count < 2) continue;
            if (WindowsList.ItemContainerGenerator.ContainerFromItem(ow)
                is not ContentPresenter cp)
            {
                WindowSwitcherWpf.Services.Log.Info("Cascade", "no container");
                continue;
            }
            if (WindowsList.ItemTemplate?.FindName("CardRoot", cp) is not Grid root)
            {
                WindowSwitcherWpf.Services.Log.Info("Cascade", "no CardRoot");
                continue;
            }
            for (var i = root.Children.Count - 1; i >= 0; i--)
                if (root.Children[i] is Border b && b.Tag is IntPtr)
                    root.Children.RemoveAt(i);
            var cardW = ow.ThumbWidth + 16;
            var peek = cardW / 2;
            var accent = _accentMap.TryGetValue(ow.GroupKey, out var c)
                ? c : System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A);
            var added = 0;
            for (var i = 1; i < st.Members.Count; i++)
            {
                var m = st.Members[i];
                var sub = BuildCascadeCard(m, ow.ThumbWidth, cardW,
                    i * peek, accent, i == st.Members.Count - 1);
                root.Children.Add(sub);
                added++;
            }
            WindowSwitcherWpf.Services.Log.Info("Cascade",
                $"head='{ow.DisplayTitle}' cardW={cardW:F0} added={added}");
        }
        // Freshly added sub-cards have no layout yet at Loaded priority —
        // re-measure the rects once the pass at Render priority has run.
        Dispatcher.BeginInvoke(new Action(UpdateLiveRects), DispatcherPriority.Render);
    }

    /// <summary>One overlapped member card: title strip + half-visible
    /// live preview. Plain click = 直达, drag = that member (peel/re-stack).
    /// The last member also carries the pin/close tools.</summary>
    private Border BuildCascadeCard(WindowEntry m, double thumbW, double cardW,
        double offsetX, System.Windows.Media.Color accent, bool last)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 7, 8, 7) };
        var icon = new Image
        {
            Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center,
            Source = LookupCache(m.Hwnd)?.Icon,
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon,
            System.Windows.Media.BitmapScalingMode.HighQuality);
        strip.Children.Add(icon);
        strip.Children.Add(new TextBlock
        {
            Text = m.Title, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
            MaxWidth = Math.Max(cardW - (last ? 130 : 60), 30),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (last)
        {
            // Tools live on the fully visible card (the head's own tools
            // are covered by the cascade). Pin acts on the stack identity.
            strip.Children.Add(MakeCascadeTool(m.Hwnd, "📌", "固定此堆叠", OnCascadePinClick));
            strip.Children.Add(MakeCascadeTool(m.Hwnd, "✕", "关闭此窗口", OnCascadeCloseClick));
        }
        var thumb = new Border
        {
            Height = 150,
            Width = thumbW,
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
        _cascadeThumbs[m.Hwnd] = thumb;

        var panel = new StackPanel();
        panel.Children.Add(strip);
        panel.Children.Add(thumb);
        var sub = new Border
        {
            Width = cardW,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(offsetX, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = m.Hwnd, // member identity for drag/hover handlers
            ToolTip = m.Title,
            Child = panel,
        };
        sub.MouseLeftButtonDown += OnCascadeDown;
        sub.MouseMove += OnCardMouseMove;
        sub.MouseLeftButtonUp += OnCascadeUp;
        sub.MouseEnter += OnCascadeMouseEnter;
        sub.MouseLeave += OnCascadeMouseLeave;
        return sub;
    }

    private static Border MakeCascadeTool(IntPtr hwnd, string glyph, string tip,
        MouseButtonEventHandler up)
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
            Text = glyph, FontSize = 10,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x61, 0x61, 0x61)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return b;
    }

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

    /// <summary>Release resolver for fan-strip/cascade-member drags: over a
    /// card, resolve member-precise (cascade) or join (lone); off the cards,
    /// the release peels the member out (剥离). Shared by OnCascadeUp and
    /// OnOverlayMouseUp so routing can never double-handle.</summary>
    private void DropFanCardAt(Point p, IntPtr dragHwnd)
    {
        var card = CardAt(p);
        if (card is not null && card.DataContext is OverlayWindow t
            && t.Hwnd != dragHwnd)
        {
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var relPt = new Point(p.X - pos.X, p.Y - pos.Y);
            if (_controller.StackInfoFor(t.Hwnd) is { } st)
                DropOnCascadeMember(t, st, relPt, card, dragHwnd);
            else
                _controller.StackDrop(dragHwnd, t.Hwnd, before: relPt.X < card.ActualWidth / 2);
            RebuildFromSlots(preserveScroll: true);
            return;
        }
        if (_controller.PeelFromStack(dragHwnd))
            RebuildFromSlots(preserveScroll: true);
    }

    /// <summary>Which cascade member region the point sits in, and whether
    /// it is that member's insert-before half. Shared by the drag preview
    /// (the line indicator) and the drop so they can never disagree.</summary>
    private static (int index, bool before)? CascadeRegion(
        SwitcherController.WindowStackInfo st, double cardW, double x)
    {
        var n = st.Members.Count;
        if (n == 0 || cardW <= 0) return null;
        var peek = cardW / (n + 1); // visible step; each sub-card is 2·peek
        var i = Math.Min((int)(x / peek), n - 1);
        var visW = i == n - 1 ? cardW - i * peek : peek;
        return (i, x - i * peek < visW / 2);
    }

    /// <summary>Member-precise cascade drop (成员精确排序): the member
    /// region under the pointer is the target, its visible half decides
    /// before/after; dropping on another cascade's member joins that stack
    /// at that spot.</summary>
    private void DropOnCascadeMember(OverlayWindow head, SwitcherController.WindowStackInfo st,
        Point p, FrameworkElement fe, IntPtr dragHwnd)
    {
        if (CascadeRegion(st, fe.ActualWidth, p.X) is not { } r) return;
        var target = st.Members[r.index].Hwnd;
        if (target != dragHwnd)
            _controller.StackDrop(dragHwnd, target, r.before);
    }

    // ---------- Cascade hover (悬停哪个成员 = 选中哪个) ----------
    // The cascade shows only half of each covered member, so the pointer
    // position decides the target: the hovered member's preview border
    // turns blue, and a plain click activates exactly that member.
    private static readonly System.Windows.Media.Brush CascadeHoverBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));

    private Border? _hoverThumb;

    /// <summary>Highlight (or restore) the preview border of one member —
    /// sub-card thumbs by hwnd, the head via its declarative ThumbBorder.
    /// Returns the highlighted Border for state tracking.</summary>
    private void SetCascadeHover(IntPtr hwnd, bool on)
    {
        Border? tb = null;
        var src = "member";
        if (_cascadeThumbs.TryGetValue(hwnd, out var t)) tb = t;
        else
        {
            // The head has no sub-card: its ThumbBorder comes from the
            // template (its exposed half is the head's clickable region).
            src = "head";
            var vm = _windows.FirstOrDefault(w => w.Hwnd == hwnd);
            if (vm is not null) tb = FindThumbBorder(vm) as Border;
        }
        WindowSwitcherWpf.Services.Log.Info("CascadeHover",
            $"{src} hwnd=0x{hwnd:X} on={on} found={tb is not null}");
        if (tb is null) return;
        if (on)
        {
            tb.BorderBrush = CascadeHoverBrush;
            tb.BorderThickness = new Thickness(2);
            _hoverThumb = tb;
        }
        else
        {
            tb.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x22, 0, 0, 0));
            tb.BorderThickness = new Thickness(1);
            if (ReferenceEquals(_hoverThumb, tb)) _hoverThumb = null;
        }
    }

    private void OnCascadeMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntPtr h }) SetCascadeHover(h, true);
    }

    private void OnCascadeMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: IntPtr h }) SetCascadeHover(h, false);
    }

    /// <summary>Hover on the head's exposed (left) half — the declarative
    /// panel is hit only where no cascade sub-card covers it.</summary>
    private void OnHeadHoverEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayWindow ow })
            SetCascadeHover(ow.Hwnd, true);
    }

    private void OnHeadHoverLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayWindow ow })
            SetCascadeHover(ow.Hwnd, false);
    }

    private void OnCascadePinClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
        {
            _controller.TogglePin(ow.Hwnd); // head identity = the stack card
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

    private bool CanDrag(OverlayWindow ow) =>
        _controller.IsInStackView || (ow.SlotBadge != "0" && !ow.IsPinned);

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
        if (!_dragActive) { EndDrag(); return; }
        var dragHwnd = _dragHwnd;
        var fromStrip = _dragFromFanStrip;
        EndDrag();
        if (dragHwnd == IntPtr.Zero) return;
        var p = e.GetPosition(WindowsList);
        var inGrid = InCardGrid(p);
        var card = CardAt(p);
        if (card is not null && card.DataContext is OverlayWindow target)
        {
            if (target.Hwnd == dragHwnd) return; // released on itself: no-op
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            var rel = (p.X - pos.X) / Math.Max(card.ActualWidth, 1);
            if (fromStrip)
            {
                // A cascade member carries just itself: dropping it on a
                // cascade resolves the exact member under the pointer; on a
                // lone card it joins that card's stack (集中). Shared with
                // OnCascadeUp so both routings behave identically.
                DropFanCardAt(p, dragHwnd);
                return;
            }
            else if (_controller.IsInStackView)
            {
                // Sub view: every card is a member of the same stack, so any
                // card drop reorders members (StackDropCore's same-stack
                // branch) — left half lands before, right half after.
                _controller.StackDrop(dragHwnd, target.Hwnd, before: rel < 0.5);
            }
            else
            {
                // Zones: outer 25% bands = 排序 (before/after), middle
                // = 集中 (drop onto the card → stack).
                if (rel < 0.25)
                    _controller.MoveRelative(dragHwnd, target.Hwnd, before: true);
                else if (rel > 0.75)
                    _controller.MoveRelative(dragHwnd, target.Hwnd, before: false);
                else
                    _controller.StackDrop(dragHwnd, target.Hwnd, before: false);
            }
            RebuildFromSlots(preserveScroll: true);
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
        if (_controller.IsInStackView)
            _controller.StackDrop(dragHwnd, d.target, d.before); // member reorder
        else
            _controller.MoveRelative(dragHwnd, d.target, d.before);
        RebuildFromSlots(preserveScroll: true);
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
        if (!_dragActive) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        var p = e.GetPosition(WindowsList);
        var card = CardAt(p);
        if (card is not null)
        {
            var vm = card.DataContext as OverlayWindow;
            var pos = card.TransformToVisual(WindowsList).Transform(new Point(0, 0));
            double? lineX = null;
            if (_dragFromFanStrip && vm is not null
                && _controller.StackInfoFor(vm.Hwnd) is { } st
                && CascadeRegion(st, card.ActualWidth, p.X - pos.X) is { } r)
            {
                // Member in hand over a cascade: preview the exact member
                // boundary — the line that 标签内部排序 will drop at.
                var peek = card.ActualWidth / (st.Members.Count + 1);
                var visW = r.index == st.Members.Count - 1
                    ? card.ActualWidth - r.index * peek : peek;
                lineX = pos.X + r.index * peek + (r.before ? 0 : visW);
            }
            else
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
            WindowSwitcherWpf.Services.Log.Info("CardEvent", $"pinclick '{ow.Title}'");
            // The controller recomputes slots (pin freezes the number) and
            // persists to order.json; the overlay re-renders from it.
            _controller.TogglePin(ow.Hwnd);
            RebuildFromSlots(preserveScroll: true);
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
        {
            // Graceful close (same as the window's own ✕), not a kill.
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
        IsPinned ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4))
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

    // Card thumbnail area width: fixed 150px height, width follows the
    // window's real aspect ratio (native Alt+Tab sizing).
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
