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

    /// <summary>Alt+数字 quick jump: select the slot with that badge and
    /// activate it immediately (快速打开这个标签).</summary>
    public void SelectSlotAndCommit(int digit)
    {
        if (!_isOpen) return;
        _controller.SelectByDigit(digit);
        RebuildSelection();
        Commit();
    }

    /// <summary>Re-renders the card list from the controller's slot model
    /// (pins + flow + drag result). Slot 0 = most recent, 1..10 managed,
    /// -1 unmanaged (no badge). Pin toggles and drags rebuild with the
    /// scroll position kept so the card doesn't jump under the cursor.</summary>
    private void RebuildFromSlots(bool preserveScroll)
    {
        var offset = preserveScroll ? Scroller.VerticalOffset : 0.0;
        _windows.Clear();
        foreach (var sw in _controller.Slotted)
        {
            var ow = new OverlayWindow(sw.Entry, sw.Entry.GroupKey, 0)
            {
                SlotBadge = sw.Slot >= 0 ? sw.Slot.ToString() : "",
                IsPinned = sw.IsPinned,
            };
            var cached = LookupCache(ow.Hwnd);
            if (cached is not null)
            {
                ow.Thumbnail = cached.Thumb;
                ow.Icon = cached.Icon;
            }
            _windows.Add(ow);
        }
        WindowSwitcherWpf.Services.Log.Info("Overlay", $"rebuild windows={_windows.Count}");
        AssignGroupAccents();
        // Thumbnail capture happens via _thumbTimer, NEVER in Rebuild,
        // otherwise 113 PrintWindow calls block the dispatcher for ~2s
        // and the user releases Alt before the window is shown.
        RebuildSelection();
        RefreshThumbnails();
        Dispatcher.BeginInvoke(new Action(LogCardRects), DispatcherPriority.Loaded);
        if (preserveScroll)
            Dispatcher.BeginInvoke(() => Scroller.ScrollToVerticalOffset(offset),
                DispatcherPriority.Loaded);
    }

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
        var map = new System.Collections.Generic.Dictionary<string, System.Windows.Media.Color>();
        foreach (var w in _windows)
        {
            if (!map.TryGetValue(w.GroupKey, out var color))
            {
                color = GroupPalette[Fnv1a(w.GroupKey) % (uint)GroupPalette.Length];
                map[w.GroupKey] = color;
            }
            w.SetGroupAccent(color);
        }
    }

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
        if (_controller.Flat.Count == 0) return;
        var entry = _controller.Flat[Math.Min(_controller.ActiveFlatIndex, _controller.Flat.Count - 1)];
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
        var n = _controller.Flat.Count;
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

        foreach (var w in _windows)
        {
            if (_liveThumbs.ContainsKey(w.Hwnd)) continue;
            try
            {
                var hr = NativeMethods.DwmRegisterThumbnail(src.Handle, w.Hwnd, out var id);
                if (hr != 0)
                {
                    WindowSwitcherWpf.Services.Log.Info("Overlay",
                        $"dwm register failed hr=0x{hr:X} hwnd=0x{w.Hwnd.ToInt64():X}");
                    continue;
                }
                _liveThumbs[w.Hwnd] = id;
                // Never-danced minimized windows would show their tiny
                // minimized frame; keep the static/placeholder card up
                // until the dance upgrades the surface (~1s).
                w.HasLivePreview = !NativeMethods.IsIconic(w.Hwnd)
                    || s_dancedHwnds.Contains(w.Hwnd.ToInt64());
            }
            catch (Exception ex)
            {
                WindowSwitcherWpf.Services.Log.Exception("Overlay.Register", ex);
            }
        }
        WindowSwitcherWpf.Services.Log.Info("Overlay",
            $"live thumbnails registered={_liveThumbs.Count}/{_windows.Count}");
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
            foreach (var w in _windows)
            {
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

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe0 && fe0.DataContext is OverlayWindow ow0)
            WindowSwitcherWpf.Services.Log.Info("CardEvent",
                $"cardclick '{ow0.Title}' dragActive={_dragActive}");
        var wasDrag = _dragActive;
        var dragged = _dragItem;
        EndDrag();
        if (wasDrag && dragged is not null)
        {
            // Drop: insert relative to the card under the cursor, side by
            // which half of the target the release point is in.
            if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow target)
            {
                var p = e.GetPosition(fe);
                var before = p.X < fe.ActualWidth / 2;
                _controller.MoveRelative(dragged.Hwnd, target.Hwnd, before);
                RebuildFromSlots(preserveScroll: true);
            }
            e.Handled = true;
            return;
        }
        if (sender is FrameworkElement fe2 && fe2.DataContext is OverlayWindow ow)
        {
            // The controller's selection must follow the mouse, otherwise
            // Commit() would activate the keyboard-selected window instead.
            if (_controller.SelectByHwnd(ow.Hwnd)) Commit();
        }
    }

    // ---------- Drag to reorder (slot model) ----------
    // Draggable: every card except slot 0 (the special most-recent) and
    // pinned ones. The drop renumbers unpinned slots, so a window dragged
    // to the left of a pinned one claims the vacant number.
    private OverlayWindow? _dragItem;
    private Point _dragStart;
    private bool _dragActive;

    private bool CanDrag(OverlayWindow ow) => ow.SlotBadge != "0" && !ow.IsPinned;

    /// <summary>Pin/close presses must not arm the card drag.</summary>
    private void OnToolDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe0 && fe0.DataContext is OverlayWindow ow0)
        {
            var scr = fe0.PointToScreen(new Point(0, 0));
            Interop.NativeMethods.GetCursorPos(out var cpt);
            var cur = new Point(cpt.x, cpt.y);
            WindowSwitcherWpf.Services.Log.Info("CardEvent",
                $"down '{ow0.Title}' at={e.GetPosition(fe0)} cardTL={scr} cursorPhys={cur}");
        }
        _dragItem = null;
        _dragActive = false;
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow && CanDrag(ow))
        {
            _dragItem = ow;
            _dragStart = e.GetPosition(this);
        }
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragActive &&
            Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) > 8)
        {
            _dragActive = true;
            if (DragCard(_dragItem) is { } b) b.Opacity = 0.55;
        }
    }

    /// <summary>Cleanup when the release lands on padding instead of a card
    /// (card handlers only fire over card content).</summary>
    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_dragItem is not null && DragCard(_dragItem) is { } b) b.Opacity = 1;
        _dragItem = null;
        _dragActive = false;
    }

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
            NativeMethods.PostMessage(ow.Hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            WindowSwitcherWpf.Services.Log.Info("Overlay", $"close posted hwnd=0x{ow.Hwnd:X}");
            e.Handled = true;
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
