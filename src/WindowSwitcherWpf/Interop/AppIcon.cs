using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// Resolves the per-window icon to display at the top of each card.
/// Tries WM_GETICON first (gives the actual app icon), then falls back
/// to the executable's shell icon.
/// </summary>
public static class AppIcon
{
    public static BitmapSource? Resolve(IntPtr hwnd, string modulePath, int size = 24)
    {
        IntPtr hIcon = IntPtr.Zero;
        try { hIcon = SendMessage(hwnd, WM_GETICON, ICON_BIG, 0); } catch { }
        if (hIcon == IntPtr.Zero)
        {
            try { hIcon = GetClassLongPtr(hwnd, GCLP_HICON); } catch { }
        }
        if (hIcon == IntPtr.Zero && !string.IsNullOrEmpty(modulePath))
        {
            hIcon = ExtractFromExecutable(modulePath);
        }
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            bmp.Freeze();
            return bmp;
        }
        finally { DestroyIcon(hIcon); }
    }

    private const uint WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int GCLP_HICON = -14;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(GetClassLong32(hWnd, nIndex));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private static IntPtr ExtractFromExecutable(string path)
    {
        var info = new SHFILEINFO();
        var sz = (uint)Marshal.SizeOf<SHFILEINFO>();
        var handle = SHGetFileInfoW(path, 0, ref info, sz, SHGFI_ICON | SHGFI_LARGEICON);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        return info.hIcon;
    }
}
