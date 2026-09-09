using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WindowSwitcherWpf.Controls;

/// <summary>
/// A WPF <see cref="HwndHost"/> whose child window is the destination
/// of a DWM thumbnail. The DWM compositor draws the live source window
/// directly into this child window's surface in real time, with zero
/// CPU readback. This is the same architecture Windows 11 Alt+Tab uses.
/// </summary>
public sealed class ThumbnailHost : HwndHost
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(IntPtr), typeof(ThumbnailHost),
        new PropertyMetadata(IntPtr.Zero, OnSourceChanged));

    private IntPtr _child = IntPtr.Zero;
    private IntPtr _thumb = IntPtr.Zero;

    public IntPtr Source
    {
        get => (IntPtr)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailHost self) self.Reattach();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _child = CreateWindowEx(
            WS_EX_NOPARENTNOTIFY | WS_EX_TRANSPARENT,
            "Static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, (int)Width, (int)Height,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Reattach();
        return new HandleRef(this, _child);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_thumb != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
        DestroyWindow(hwnd.Handle);
        _child = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateRect();
    }

    private void Reattach()
    {
        WindowSwitcherWpf.Services.Log.Info("ThumbnailHost",
            $"Reattach child=0x{_child.ToInt64():X} source=0x{Source.ToInt64():X}");
        if (_child == IntPtr.Zero || Source == IntPtr.Zero) return;
        if (_thumb != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
        int hr = DwmRegisterThumbnail(_child, Source, out _thumb);
        WindowSwitcherWpf.Services.Log.Info("ThumbnailHost",
            $"DwmRegisterThumbnail hr=0x{hr:X} thumb=0x{_thumb.ToInt64():X}");
        if (hr != 0) _thumb = IntPtr.Zero;
        UpdateRect();
    }

    private void UpdateRect()
    {
        if (_thumb == IntPtr.Zero || _child == IntPtr.Zero) return;
        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        if (w <= 0 || h <= 0) { w = 100; h = 60; }
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            // NB: never add DWM_TNP_SOURCECLIENTAREA — it ghosts Mica
            // backdrop windows (alpha-0 raw surface).
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE,
            fVisible = true,
            rcDestination = new RECT { Left = 0, Top = 0, Right = w, Bottom = h },
            rcSource = new RECT(),
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CLIPCHILDREN = 0x02000000L;
    private const long WS_EX_NOPARENTNOTIFY = 0x00000004L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    private const int DWM_TNP_RECTDESTINATION = 0x00000001;
    private const int DWM_TNP_RECTSOURCE = 0x00000002;
    private const int DWM_TNP_OPACITY = 0x00000004;
    private const int DWM_TNP_VISIBLE = 0x00000008;
    private const int DWM_TNP_SOURCECLIENTAREA = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        long dwExStyle, string lpClassName, string lpWindowName,
        long dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr id, ref DWM_THUMBNAIL_PROPERTIES props);
}
