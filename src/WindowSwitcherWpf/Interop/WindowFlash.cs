using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WindowSwitcherWpf.Services;
using static WindowSwitcherWpf.Interop.NativeMethods;
// NB: Interop 命名空间里也有名为 Rectangle 的 P/Invoke 结构体, 会盖掉
// System.Windows.Shapes.Rectangle — 那处必须写全名.

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// 切到某个窗口时, 在**那个窗口的外框**上闪一圈蓝光然后马上消失
/// (用户: Alt+Tab 选中一个最小化的标签, 它恢复到桌面那一刻, 页面边框
/// 画一个蓝色边框马上消失).
///
/// 与 overlay 卡上的选中框完全无关 —— 这里画在真实桌面上的目标窗口
/// 外围, 激活成功后才闪 (失败不闪).
///
/// 实现: 一个无边框 / 透明 / 置顶 / 点击穿透的临时 WPF 窗口, 尺寸位置
/// 贴 GetWindowRect (物理像素, 与 overlay 的 CenterOnCursorMonitor 同一
/// 套坐标口径), 内容是一个只有 Stroke 的 Rectangle + DropShadowEffect
/// 做外发光, Opacity 1→0 走完就关窗.
/// </summary>
public static class WindowFlash
{
    /// <summary>config SelectionGlow (null = 默认开). 由 App 启动时写入.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Flash(IntPtr hwnd)
    {
        if (!Enabled || hwnd == IntPtr.Zero) return;
        var app = Application.Current;
        if (app is null) return;
        // 激活是同步的 (SetForegroundWindow + 重试), 拿到 rect 要等目标窗口
        // 已经摆好位置, 所以推到 dispatcher 让它排在激活之后.
        app.Dispatcher.BeginInvoke(new Action(() => Run(hwnd)));
    }

    private static void Run(IntPtr hwnd)
    {
        try
        {
            // 真实边框: 优先 DWM 的 EXTENDED_FRAME_BOUNDS (去掉最大化那圈不可见
            // resize border), 拿不到再退 GetWindowRect.
            var hr = DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out var r, Marshal.SizeOf<RECT>());
            if (hr != 0 && !GetWindowRect(hwnd, out r)) return;

            // 再夹到所在显示器工作区 — 某些全屏窗口的 rect 仍会越出屏幕,
            // 描边画出去就等于看不见.
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                r.Left = Math.Max(r.Left, mi.rcWork.Left);
                r.Top = Math.Max(r.Top, mi.rcWork.Top);
                r.Right = Math.Min(r.Right, mi.rcWork.Right);
                r.Bottom = Math.Min(r.Bottom, mi.rcWork.Bottom);
            }

            var pw = r.Right - r.Left;
            var ph = r.Bottom - r.Top;
            if (pw <= 0 || ph <= 0) return;

            // 本进程 PerMonitorV2: GetWindowRect = 物理像素, WPF 窗口位置 = DIP.
            // 目标窗口在别的显示器且缩放不是 100% 时, 不换算就会画飞.
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var s = dpi / 96.0;
            var w = pw / s;
            var h = ph / s;

            System.Windows.Window win = new System.Windows.Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                IsHitTestVisible = false,
                Left = r.Left / s,
                Top = r.Top / s,
                Width = w,
                Height = h,
            };
            // 3px 描边贴着窗口边缘: 内容区 = 窗口 rect, Stroke 居中占外半.
            // NB: Interop 命名空间里有个 P/Invoke 的 Rectangle 结构体, 会盖掉
            // System.Windows.Shapes.Rectangle — 这里必须写全名.
            var rect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0xA6, 0xFF)),
                StrokeThickness = 3,
                RadiusX = 6,
                RadiusY = 6,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromArgb(0xFF, 0x4D, 0xA6, 0xFF),
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.9,
                },
            };
            // 描边画在内缩 1.5px 的矩形上, 让线正好压在窗口边框上而不是被裁掉.
            rect.Margin = new Thickness(1.5);
            var host = new Border
            {
                Background = Brushes.Transparent,
                Child = rect,
            };
            win.Content = host;

            var done = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            done.Tick += (_, _) =>
            {
                done.Stop();
                try { win.Close(); } catch { }
            };
            // NB: WPF Window 没有 Opened 事件 (那是 Popup 的); ContentRendered
            // 是首次渲染完成后, 正好是淡出动画该起的时刻.
            win.ContentRendered += (_, _) =>
            {
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500))
                {
                    FillBehavior = FillBehavior.HoldEnd,
                };
                anim.Completed += (_, _) =>
                {
                    try { win.Close(); } catch { }
                };
                host.BeginAnimation(System.Windows.UIElement.OpacityProperty, anim);
            };
            win.Show();
            WindowSwitcherWpf.Services.Log.Info("Flash",
                $"window glow 0x{hwnd.ToInt64():X} dwm={hr} px=({r.Left},{r.Top},{pw}x{ph}) dpi={dpi} dip=({r.Left / s},{r.Top / s},{w}x{h})");
        }
        catch (Exception ex)
        {
            Log.Exception("WindowFlash", ex);
        }
    }
}