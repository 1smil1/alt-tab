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

    public void ShowAndFocus()
    {
        try
        {
            _controller.OpenOverlay();
            Rebuild();
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
    }

    public void Cancel()
    {
        if (!_isOpen) return;
        _thumbTimer.Stop();
        UnregisterLiveThumbnails();
        _isOpen = false;
        Hide();
    }

    public void SelectDigit(int digit)
    {
        _controller.SelectByDigit(digit);
        RebuildSelection();
    }

    private void Rebuild()
    {
        _windows.Clear();
        foreach (var g in _controller.Groups)
        {
            for (var i = 0; i < g.Windows.Count; i++)
            {
                _windows.Add(new OverlayWindow(g.Windows[i], g.Key, i));
            }
        }
        WindowSwitcherWpf.Services.Log.Info("Overlay", $"rebuild windows={_windows.Count}");
        // Thumbnail capture happens via _thumbTimer, NEVER in Rebuild,
        // otherwise 113 PrintWindow calls block the dispatcher for ~2s
        // and the user releases Alt before the window is shown.
        if (_windows.Count > 0 && _controller.ActiveGroupIndex >= 0
            && _controller.ActiveGroupIndex < _controller.Groups.Count)
        {
            var key = _controller.Groups[_controller.ActiveGroupIndex].Key;
            var idx = _controller.ActiveWindowIndex;
            var first = _windows.FirstOrDefault(w => w.GroupKey == key);
            if (first is not null) first.IsSelected = true;
        }
        RefreshThumbnails();
    }

    private void RebuildSelection()
    {
        for (var i = 0; i < _windows.Count; i++)
            _windows[i].IsSelected = false;
        if (_controller.Groups.Count == 0) return;
        var group = _controller.Groups[_controller.ActiveGroupIndex];
        var entry = group.Windows.Count == 0
            ? null
            : group.Windows[Math.Min(_controller.ActiveWindowIndex, group.Windows.Count - 1)];
        if (entry is null) return;
        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i].Hwnd == entry.Hwnd) { _windows[i].IsSelected = true; break; }
        }
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
                            Dispatcher.BeginInvoke(new Action(() => w.Thumbnail = bmp));
                            thumbDone++;
                        }
                    }
                    if (w.Icon is null)
                    {
                        var icon = WindowSwitcherWpf.Interop.AppIcon.Resolve(w.Hwnd, w.ModulePath, 20);
                        if (icon is not null)
                        {
                            Dispatcher.BeginInvoke(new Action(() => w.Icon = icon));
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
            // Force layout to get the actual size before reading Width/Height.
            InvalidateMeasure();
            Measure(new Size(MaxWidth, MaxHeight));
            Arrange(new Rect(new Point(0, 0), DesiredSize));
            UpdateLayout();

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
            var w = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
            var h = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
            if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0) w = 800;
            if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0) h = 500;
            Left = work.Left + (work.Right - work.Left - w) / 2.0;
            Top = work.Top + (work.Bottom - work.Top - h) / 2.0;
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
        var minimized = _windows.Where(w => NativeMethods.IsIconic(w.Hwnd))
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
                // Batch dance: restore ALL cloaked, wait once so slow
                // renderers (Zotero/GTK need several hundred ms) finish
                // painting, then minimize ALL. Time is O(1) in window count.
                foreach (var h in minimized)
                {
                    var cloak = 1;
                    NativeMethods.DwmSetWindowAttribute(
                        h, NativeMethods.DWMWA_CLOAK, ref cloak, sizeof(int));
                    NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWNOACTIVATE);
                }
                System.Threading.Thread.Sleep(600);
                foreach (var h in minimized)
                {
                    NativeMethods.ShowWindow(h, NativeMethods.SW_SHOWMINNOACTIVE);
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
            Dispatcher.BeginInvoke(new Action(RegisterLiveThumbnails),
                DispatcherPriority.ApplicationIdle);
        });
    }

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
                w.HasLivePreview = true;
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
                        Right = left + (int)el.ActualWidth,
                        Bottom = top + (int)el.ActualHeight,
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
        if (sender is FrameworkElement fe && fe.DataContext is OverlayWindow ow)
        {
            // The controller's selection must follow the mouse, otherwise
            // Commit() would activate the keyboard-selected window instead.
            if (_controller.SelectByHwnd(ow.Hwnd)) Commit();
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
