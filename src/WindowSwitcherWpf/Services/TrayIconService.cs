using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// 托盘图标 (Shell_NotifyIcon, 零依赖零资产): 图标用 WPF DrawingVisual 现画
/// (两张叠起的窗口卡片), 挂在隐藏宿主 MainWindow 的 HWND 上收回调.
/// 左键=设置, 右键=菜单(设置/显示切换面板/退出), Explorer 重启自动重挂.
/// 内存代价: 同进程一个 HICON + 一个 shell 注册项, 可忽略.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_APP_TRAY = 0x8000; // WM_APP + 0
    private const int CmdSettings = 1;
    private const int CmdShowOverlay = 2;
    private const int CmdExit = 3;

    private readonly IntPtr _host;
    private readonly Action _openSettings;
    private readonly Action _showOverlay;
    private readonly Action _exit;
    private IntPtr _icon;
    private uint _taskbarCreatedMsg;
    private bool _added;

    public TrayIconService(IntPtr hostHwnd, Action openSettings,
        Action showOverlay, Action exit)
    {
        _host = hostHwnd;
        _openSettings = openSettings;
        _showOverlay = showOverlay;
        _exit = exit;
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    private void Add()
    {
        if (_icon == IntPtr.Zero) _icon = CreateAppIcon();
        if (_icon == IntPtr.Zero)
        {
            Log.Error("Tray", "CreateAppIcon failed");
            return;
        }
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _host,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = "alt-tab",
        };
        if (Shell_NotifyIcon(NIM_ADD, ref data))
        {
            data.uVersion = 3; // NOTIFYICON_VERSION: lParam=mouse msg, no coords
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
            _added = true;
            Log.Info("Tray", "icon added");
        }
        else
        {
            Log.Error("Tray", "NIM_ADD failed");
        }
    }

    private void Remove()
    {
        if (!_added) return;
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _host,
            uID = 1,
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>HwndSource hook — 在 App.OnStartup 里 AddHook 接入.</summary>
    public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if ((uint)msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            Remove(); // explorer 重启后旧图标已随旧 tray 消亡
            Add();
            handled = true;
            return IntPtr.Zero;
        }
        if (msg != (int)WM_APP_TRAY) return IntPtr.Zero;
        switch (lParam.ToInt64() & 0xFFFF)
        {
            case 0x0202: // WM_LBUTTONUP: 单击直达设置
                _openSettings();
                handled = true;
                break;
            case 0x0205: // WM_RBUTTONUP: 弹菜单
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // TrackPopupMenu 的标准托盘姿势: 先抢前台, 点外面才会自动收起.
        SetForegroundWindow(_host);
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        AppendMenuW(menu, MF_STRING, (IntPtr)CmdSettings, "设置…");
        AppendMenuW(menu, MF_STRING, (IntPtr)CmdShowOverlay, "显示切换面板\tAlt+`");
        AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (IntPtr)CmdExit, "退出");
        GetCursorPos(out var p);
        var cmd = TrackPopupMenuEx(menu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
            p.X, p.Y, _host, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case CmdSettings: _openSettings(); break;
            case CmdShowOverlay: _showOverlay(); break;
            case CmdExit: _exit(); break;
        }
    }

    public void Dispose()
    {
        Remove();
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    // ---------- 图标: WPF 画 16×16, 拷进 DIB 后 CreateIconIndirect ----------

    private static IntPtr CreateAppIcon()
    {
        const int size = 16;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 两张叠起的窗口卡片 = 切换器主题
            var back = new RectangleGeometry(new Rect(1, 1, 9.5, 8.5), 1.6, 1.6);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x9D, 0xC3, 0xE6)),
                null, back.Rect);
            var front = new RectangleGeometry(new Rect(5, 6, 10, 9), 1.6, 1.6);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)),
                new Pen(Brushes.White, 1), front.Rect);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var pixels = new byte[size * size * 4];
        rtb.CopyPixels(pixels, size * 4, 0);

        var bi = new BITMAPINFO
        {
            biSize = 40,
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bits = IntPtr.Zero;
        var color = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        // 全 0 掩码 = 逐像素用 alpha (32bpp 图标的通行做法)
        var mask = CreateBitmap(size, size, 1, 1, new byte[size / 2 * size]);
        var info = new ICONINFO { fIcon = true, xHotspot = 0, yHotspot = 0,
            hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    // ---------- P/Invoke (self-contained, 不污染 NativeMethods) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion; // union uTimeout/uVersion
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2,
        TPM_BOTTOMALIGN = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags,
        int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;   // 默认 marshal = 4 字节 BOOL
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    // BITMAPINFO 平铺 header (32bpp BI_RGB 不需要颜色表)
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes,
        uint bitsPerPixel, byte[]? bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
