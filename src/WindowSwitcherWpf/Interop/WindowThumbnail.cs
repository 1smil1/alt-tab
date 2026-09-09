using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// Window preview via PrintWindow with PW_RENDERFULLCONTENT. DWM-thumbnail
/// approaches were tried but fail in the GDI readback path (DWM composes
/// to the GPU surface, GDI sees an empty bitmap). PrintWindow
/// (PW_RENDERFULLCONTENT) works reliably for most apps: Electron, native
/// Win32, Chromium-based, etc. It fails (returns black) for some
/// DirectX / hardware-accelerated apps, which is a known limitation.
/// </summary>
public static class WindowThumbnail
{
    // Cards display at 150px; 200px leaves DPI headroom while keeping the
    // cached bitmaps (24 windows ≈ 10MB) small.
    private const int CaptureHeight = 200;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public static BitmapSource? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            // Native Alt+Tab look: fixed thumbnail height, width follows
            // the window's real aspect ratio. Capture at 2x the on-screen
            // height (2x150) so the downscale stays crisp. Minimized
            // windows report a tiny rect -> keep the default 1.6 aspect.
            var aspect = 1.6;
            if (NativeMethods.GetWindowRect(hwnd, out var r))
            {
                var ww = r.Right - r.Left;
                var wh = r.Bottom - r.Top;
                if (ww > 100 && wh > 100) aspect = (double)ww / wh;
            }
            aspect = Math.Clamp(aspect, 0.6, 2.6);
            var w = (int)Math.Clamp(CaptureHeight * aspect, 160, 832);
            return PrintWindowToBitmap(hwnd, w, CaptureHeight);
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("WindowThumbnail.Capture", ex);
            return null;
        }
    }

    private static BitmapSource? PrintWindowToBitmap(IntPtr hwnd, int w, int h)
    {
        var hdcSrc = GetDC(hwnd);
        if (hdcSrc == IntPtr.Zero) return null;
        try
        {
            var hdcMem = CreateCompatibleDC(hdcSrc);
            var hBmp = CreateCompatibleBitmap(hdcSrc, w, h);
            if (hBmp == IntPtr.Zero) { DeleteDC(hdcMem); return null; }
            var oldObj = SelectObject(hdcMem, hBmp);
            try
            {
                if (!PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT))
                {
                    PrintWindow(hwnd, hdcMem, 0);
                }
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                return bmp;
            }
            finally
            {
                SelectObject(hdcMem, oldObj);
                DeleteObject(hBmp);
                DeleteDC(hdcMem);
            }
        }
        finally { ReleaseDC(hwnd, hdcSrc); }
    }

    // ---------- P/Invoke ----------

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
}
