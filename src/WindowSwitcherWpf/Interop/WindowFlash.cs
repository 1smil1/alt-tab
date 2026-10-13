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
/// 切到某个窗口时, 在**那个窗口的外框**上闪一圈暖金虚线然后马上消失
/// (用户: Alt+Tab 选中一个最小化的标签, 它恢复到桌面那一刻, 页面边框
/// 画一圈边框马上消失).
///
/// 与 overlay 卡上的选中框完全无关 —— 这里画在真实桌面上的目标窗口
/// 外围, 激活成功后才闪 (失败不闪).
///
/// 实现: 一个**复用**的无边框 / 透明 / 置顶 / 点击穿透 WPF 窗口. 每次闪
/// 只是改位置尺寸 + 重启动画, 结束隐藏 (不是 Close): AllowsTransparency
/// 窗口建一次 ~100ms+, 每次重建的话边框会明显滞后于窗口切换 — 用户反馈
/// "alt tab 后有延迟才出现".
/// </summary>
public static class WindowFlash
{
    /// <summary>config SelectionGlow (null = 默认开). 由 App 启动时写入.</summary>
    public static bool Enabled { get; set; } = true;

    // 暖金 (#FFB454) — 比冷蓝柔和, 在深色 UI 上不刺眼, 在浅色窗口上也不突兀.
    private static readonly Color GlowColor = Color.FromRgb(0xFF, 0xB4, 0x54);

    private static System.Windows.Window? _win;
    private static Border? _host;
    private static System.Windows.Shapes.Rectangle? _rect;
    private static System.Windows.Threading.DispatcherTimer? _timer;

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

            var win = EnsureWindow();
            if (win is null) return;
            win.Left = r.Left / s;
            win.Top = r.Top / s;
            win.Width = pw / s;
            win.Height = ph / s;
            if (!win.IsVisible) win.Show();
            // 置顶窗口的通用抬升法: 去掉再挂上, 保证压在刚激活的窗口之上.
            win.Topmost = false;
            win.Topmost = true;

            Start();

            Log.Info("Flash",
                $"window glow 0x{hwnd.ToInt64():X} dwm={hr} px=({r.Left},{r.Top},{pw}x{ph}) dpi={dpi}");
        }
        catch (Exception ex)
        {
            Log.Exception("WindowFlash", ex);
        }
    }

    private static System.Windows.Window? EnsureWindow()
    {
        if (_win is not null) return _win;

        // NB: Interop 命名空间里有个 P/Invoke 的 Rectangle 结构体, 会盖掉
        // System.Windows.Shapes.Rectangle — 这里必须写全名.
        var rect = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(GlowColor),
            StrokeThickness = 2.5,
            // 虚线 + 圆头: 比实线柔和, 一闪而过时也不刺眼
            StrokeDashArray = new DoubleCollection { 16, 10 },
            StrokeDashCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RadiusX = 8,
            RadiusY = 8,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = GlowColor,
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.7,
            },
            // 描边画在内缩的矩形上, 让线正好压在窗口边框上而不是被裁掉.
            Margin = new Thickness(2),
        };
        var host = new Border
        {
            Background = Brushes.Transparent,
            Child = rect,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.03, 1.03),
        };
        var win = new System.Windows.Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            IsHitTestVisible = false,
            Content = host,
            // 注意: 不透明度只作用在 host 上 (动画对象). 设到窗口上会把
            // 所有内容一起隐藏 — 之前就是这里写的 0, 导致复用后永远看不见.
            Opacity = 1,
        };

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            // 兜底: 动画万一没跑完 (窗口被挡/系统繁忙) 也一定收掉
            Interval = TimeSpan.FromMilliseconds(900),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_win is not null) _win.Hide();
        };

        _rect = rect;
        _host = host;
        _win = win;
        _timer = timer;
        return win;
    }

    private static void Start()
    {
        if (_win is null || _host is null || _rect is null || _timer is null) return;
        var win = _win;
        var host = _host;
        var rect = _rect;

        // 清掉上一轮的残留动画 (复用窗口的关键), 否则 Fade 会被冻在 Opacity=0.
        host.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
        rect.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
        host.Opacity = 1;

        // 动画不等首次渲染 (ContentRendered) — 那会白等一帧, 正是用户看到的
        // "延迟才出现". 直接起, Show() 之后的第一帧就在动.
        var dur = TimeSpan.FromMilliseconds(750);

        // 1) 虚线流动: dash offset 一直走到淡出结束
        var flow = new DoubleAnimation(0, -52, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rect.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, flow);

        // 2) 轻微回弹: 1.03 -> 1.0, 让"贴上来"的感觉更实
        var pop = new DoubleAnimation(1.0, dur)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        var st = (ScaleTransform)host.RenderTransform;
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);

        // 3) 渐变离开: 前 35% 保持全亮, 之后平滑淡出
        var fade = new DoubleAnimationUsingKeyFrames
        {
            Duration = dur,
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.35)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        fade.Completed += (_, _) =>
        {
            try { win.Hide(); } catch { }
        };
        host.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);

        _timer.Stop();
        _timer.Start();
    }
}