using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowSwitcherWpf.Services;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// 切到某个窗口时, 在那个窗口外框闪一圈暖金虚线流光然后淡出
/// (用户的美化重点效果: 虚线 + 圆头 + 流动 + 渐隐).
///
/// 为什么不用 WPF 窗口画: 2026-10-23 这台机器 (Win11 22631) 上, 全屏级
/// WPF 分层窗口上屏前会被 DWM 合成白色矩形约 1s — 预热/屏外预渲染/去
/// DropShadowEffect 都救不回来, 表现为"Alt+Tab 先白屏 1s 才出内容".
/// 原生 DWMWA_BORDER_COLOR 试过: hr=0 但 1px 边框在最大化的窗口上看不见.
///
/// 所以这里用纯 Win32 分层窗口 + GDI+ UpdateLayeredWindow: 窗口类背景刷
/// 为 NULL, 像素先画进位图再一次性上屏, DWM 从头到尾只看得到画好的
/// 像素, 结构上没有"未渲染首帧"可白. 虚线笔/圆头/动画偏移全部照旧.
/// </summary>
public static class WindowFlash
{
    /// <summary>config SelectionGlow (null = 默认开). 由 App 启动时写入.</summary>
    public static bool Enabled { get; set; } = true;

    // 暖金 #FFB454 — 比冷蓝柔和, 深浅色 UI 都不刺眼.
    private static readonly Color Glow = Color.FromArgb(255, 0xFF, 0xB4, 0x54);

    private const int FrameMs = 33;         // ~30fps 的流光动画
    private const int TotalMs = 900;        // 整段闪烁时长
    private const int MarginPx = 12;        // 线外留白 (晕圈空间)
    private const float LineWidth = 2.5f;
    private const float HaloWidth = 8f;
    private const float DashAdvance = 3.4f; // 每帧虚线流动步进 (px)

    private static IntPtr _hwnd;
    private static DispatcherTimer? _timer;
    private static DateTime _started;
    private static float _dashOffset;
    private static RECT _target;            // 物理像素, 含 MarginPx 外扩
    private static bool _ulwFailedLogged;

    // ---------- 公开入口 ----------

    public static void Flash(IntPtr hwnd)
    {
        if (!Enabled || hwnd == IntPtr.Zero) return;
        try
        {
            // 目标窗口真实外框 (物理像素): 优先 DWM 扩展边框, 退 GetWindowRect.
            var hr = DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out var r, Marshal.SizeOf<RECT>());
            if (hr != 0 && !GetWindowRect(hwnd, out r)) return;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return;

            _target.Left = r.Left - MarginPx;
            _target.Top = r.Top - MarginPx;
            _target.Right = r.Right + MarginPx;
            _target.Bottom = r.Bottom + MarginPx;

            if (_hwnd == IntPtr.Zero && !CreateLayeredWindow()) return;
            ShowWindow(_hwnd, 8); // SW_SHOWNA — ULW 不会唤醒隐藏的窗口
            _started = DateTime.UtcNow;
            _dashOffset = 0;
            _timer ??= NewTimer();
            _timer.Stop();
            _timer.Start();
            RenderFrame(); // 首帧立即上屏, 不等第一个 tick
            Log.Info("Flash",
                $"window glow(dash) 0x{hwnd.ToInt64():X} px=({_target.Left},{_target.Top},{_target.Right - _target.Left}x{_target.Bottom - _target.Top})");
        }
        catch (Exception ex)
        {
            Log.Exception("WindowFlash", ex);
        }
    }

    // ---------- 帧渲染: GDI+ 画虚线, UpdateLayeredWindow 上屏 ----------

    private static DispatcherTimer NewTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMs) };
        t.Tick += (_, _) =>
        {
            var elapsed = (int)(DateTime.UtcNow - _started).TotalMilliseconds;
            if (elapsed >= TotalMs)
            {
                t.Stop();
                ShowWindow(_hwnd, 0); // SW_HIDE
                return;
            }
            _dashOffset += DashAdvance;
            RenderFrame();
        };
        return t;
    }

    private static void RenderFrame()
    {
        var elapsed = (int)(DateTime.UtcNow - _started).TotalMilliseconds;
        // 前 40% 全亮, 之后平滑淡出.
        var alpha = elapsed < TotalMs * 0.4
            ? 1.0
            : 1.0 - (elapsed - TotalMs * 0.4) / (TotalMs * 0.6);
        if (alpha < 0) alpha = 0;

        var w = _target.Right - _target.Left;
        var h = _target.Bottom - _target.Top;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRectPath(w, h);
            // 晕圈: 宽的半透明虚线垫底, 实线压上面 — 和旧 WPF 版的双层观感一致.
            var haloA = (int)(90 * alpha);
            var lineA = (int)(255 * alpha);
            using var halo = new Pen(Color.FromArgb(haloA, Glow), HaloWidth)
            {
                DashStyle = DashStyle.Dash,
                DashPattern = new float[] { 16f, 10f },
                DashCap = DashCap.Round,
                DashOffset = _dashOffset * 0.6f,
            };
            using var line = new Pen(Color.FromArgb(lineA, Glow), LineWidth)
            {
                DashStyle = DashStyle.Dash,
                DashPattern = new float[] { 16f, 10f },
                DashCap = DashCap.Round,
                DashOffset = _dashOffset,
            };
            g.DrawPath(halo, path);
            g.DrawPath(line, path);
        }

        ULW(bmp, w, h);
    }

    private static GraphicsPath RoundedRectPath(int w, int h)
    {
        var p = new GraphicsPath();
        const float rad = 8f, inset = 3f;
        var x = inset;
        var y = inset;
        var cw = w - inset * 2;
        var ch = h - inset * 2;
        p.AddArc(x, y, rad * 2, rad * 2, 180, 90);
        p.AddArc(x + cw - rad * 2, y, rad * 2, rad * 2, 270, 90);
        p.AddArc(x + cw - rad * 2, y + ch - rad * 2, rad * 2, rad * 2, 0, 90);
        p.AddArc(x, y + ch - rad * 2, rad * 2, rad * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>ULW 要预乘 alpha (RGB 分量已乘 alpha), 而 GDI+ 位图是直通
    /// alpha. 不预乘时虚线边缘会出白花, 部分 DWM 状态下整窗直接不显示.
    /// 虚线以外的区域 alpha=0, 跳过 — 真正要处理的只有线条周围的 AA 像素.</summary>
    private static void Premultiply(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect,
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var row = (byte*)data.Scan0;
                for (var y = 0; y < data.Height; y++, row += data.Stride)
                {
                    var px = row;
                    for (var x = 0; x < data.Width; x++, px += 4)
                    {
                        var a = px[3];
                        if (a == 255) continue;
                        px[0] = (byte)(px[0] * a >> 8);
                        px[1] = (byte)(px[1] * a >> 8);
                        px[2] = (byte)(px[2] * a >> 8);
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>位图交给窗口: 一次调用同时定位置/尺寸/像素.</summary>
    private static void ULW(Bitmap bmp, int w, int h)
    {
        Premultiply(bmp);
        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(hdcMem, hBmp);
        try
        {
            var dst = new POINT { x = _target.Left, y = _target.Top };
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0, // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1, // AC_SRC_ALPHA — 用位图自带逐像素 alpha
            };
            if (!UpdateLayeredWindow(_hwnd, hdcScreen, ref dst, ref size,
                    hdcMem, ref src, 0, ref blend, 2 /* ULW_ALPHA */)
                && !_ulwFailedLogged)
            {
                _ulwFailedLogged = true;
                Log.Warn("Flash",
                    $"UpdateLayeredWindow failed err={Marshal.GetLastWin32Error()} size={w}x{h}");
            }
        }
        finally
        {
            _ = SelectObject(hdcMem, old);
            _ = DeleteObject(hBmp);
            _ = DeleteDC(hdcMem);
            _ = ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    // ---------- Win32 窗口创建 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className,
        string windowName, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    private static bool CreateLayeredWindow()
    {
        // WS_EX_LAYERED 分层 | TRANSPARENT 点击穿透 | TOPMOST 压顶 |
        // NOACTIVATE 不抢焦点 | TOOLWINDOW 不进 Alt+Tab.
        // 窗口类用系统现成的 STATIC — 自己 RegisterClassExW 在这台机器上
        // 稳定回 err=87 (结构体封送坑), 没必要跟它纠缠; ULW 不需要消息循环.
        const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20,
            WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000,
            WS_EX_TOOLWINDOW = 0x80;
        const uint WS_POPUP = 0x80000000;

        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST |
            WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "STATIC", "", WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Warn("Flash", $"CreateWindowExW failed err={Marshal.GetLastWin32Error()}");
            return false;
        }
        Log.Info("Flash", $"glow window created hwnd=0x{_hwnd.ToInt64():X}");
        return true;
    }
}
