using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WindowSwitcherWpf.Services;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// WH_KEYBOARD_LL global hook on a DEDICATED pump thread. LL 回调经安装线程
/// 的消息循环派发 — 之前装在 UI 线程上, 开 overlay/GC 期间回调排队超过
/// LowLevelHooksTimeout, Windows 把该次按键直接递给系统 = 原生 Alt+Tab
/// 冒出来 (用户: 系统 alt tab 也起作用). 独立线程专职泵消息, 回调微秒级
/// 返回, UI 重活走 BeginInvoke, 原生切换器不再漏出.
/// Trigger key is configurable via <see cref="TriggerVirtualKey"/> /
/// <see cref="TriggerScanCode"/>.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action? OverlayRequested;
    public event Action<bool>? OverlayAdvance;
    public event Action<int>? OverlayArrow;
    /// <summary>Alt+0..9 (main row + numpad) while the overlay is open:
    /// quick-jump to the slot with that badge (Alt+数字 直达标签).</summary>
    public event Action<int>? OverlayDigit;
    public event Action? OverlayCommit;
    public event Action? OverlayCancel;
    /// <summary>Esc while the overlay is open. The subscriber decides what
    /// it means: leaving a stack view (子标签页面 → 主页面) or closing
    /// the overlay — the hook swallows it either way.</summary>
    public event Action? OverlayEscape;
    public event Action? EscapePressed;
    /// <summary>一次直达 (overlay closed): quick-modifier + digit pressed.
    /// The app decides tap (=jump) vs hold (≥300ms on a堆叠 = 子标签页面)
    /// — the hook only reports press/release.</summary>
    public event Action<int>? QuickJumpDown;
    /// <summary>The quick-jump digit released (快速松开 = 跳转).</summary>
    public event Action<int>? QuickJumpUp;
    /// <summary>虚拟桌面热键 (修饰键前缀 + 数字, overlay 关着时): 切到第
    /// N 个 Windows 虚拟桌面 (0 = 第 10 个).</summary>
    public event Action<int>? VirtualDesktopSwitch;

    /// <summary>Alt+Ctrl+Shift+←/→: 把窗口搬到左 (-1) / 右 (+1) 相邻虚拟
    /// 桌面. 语义同原生 Win+Ctrl+Shift+方向键 (只搬窗口, 不切桌面), 但
    /// 面板开着时搬的是**选中的那张卡**, 而不是挡在面板后面的前台窗口;
    /// 不用 Win 键是怕跟系统快捷键抢, 也因为 Win 键注入不到, 没法自测.</summary>
    public event Action<int>? VirtualDesktopMove;

    /// <summary>迁移窗口热键的修饰键 (默认 Alt+Ctrl+Shift), 后面跟 ←/→.
    /// 解析失败/留空 = 空数组 = 该功能关闭.</summary>
    public uint[] MoveWindowModifierVks { get; set; } = Array.Empty<uint>();

    /// <summary>Alt+Tab 拦截开关 (config SuppressAltTab 解析后的最终值):
    /// 开 = Alt+Tab 永远到不了系统 (被我们的 overlay 取代).</summary>
    public bool SuppressAltTabActive { get; set; }

    /// <summary>一次直达修饰键的 virtual keys (默认 Alt 三键). Set from
    /// config QuickJumpModifier via HotkeyParser.ModifierKeys.</summary>
    public uint[] QuickModifierVirtualKeys { get; set; } = { 0x12, 0xA4, 0xA5 };

    /// <summary>虚拟桌面热键的修饰键前缀 (config VirtualDesktopHotkey 解析
    /// 结果, 如 Alt+Ctrl → Alt+Ctrl 六个 vk). 空 = 该功能关闭.
    /// 按住**全部**这些键再按数字 = 切虚拟桌面.</summary>
    public uint[] VirtualDesktopModifierVks { get; set; } = Array.Empty<uint>();

    public uint TriggerVirtualKey { get; set; } = 0xC0; // VK_OEM_3 = backtick
    public uint TriggerScanCode { get; set; } = 0x29;

    /// <summary>第二热键 vk (一按就开 overlay, 持续存在; 直到 Esc 关).
    /// 0 = 禁用. 区别于主热键: 不挂 Alt-up commit, 不进入循环 (OverlayAdvance
    /// 走主热键的 Alt+Tab 体感).</summary>
    public uint SecondTriggerVirtualKey { get; set; } = 0xC0; // VK_OEM_3
    public uint SecondTriggerScanCode { get; set; } = 0x29;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private ManualResetEventSlim? _ready;
    private volatile bool _pumpAlive;
    private volatile bool _stopRequested;
    private bool _altDown;
    // Shift/Ctrl 也自己跟一份: GetAsyncKeyState 在 LL 钩子里读修饰键有已知的
    // 不可靠窗口 (按键刚按下时异步状态还没更新), 实测注入按键时 Shift 恒读到
    // False. async 读数与钩子跟踪取或, 谁先更新用谁.
    private volatile bool _shiftDown;
    private volatile bool _ctrlDown;
    private volatile bool _overlayOpen;      // UI 线程 SetOverlayOpen 跨线程写
    // 一次直达状态: quick 修饰键是否按住 + 当前按住的数字 vk (0=无)
    private bool _quickModDown;
    private volatile uint _quickDigitVk;
    /// <summary>第二热键打开的 overlay: Alt-up 不要自动 commit (一按就
    /// 出现 + 持续存在, Esc 才关). 主热键打开则该标记 false, 保留松开
    /// Alt 即提交的 Native 体感.</summary>
    private volatile bool _suppressAltCommit;

    public KeyboardHook()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_pumpThread is not null) return;
        _stopRequested = false;
        _ready = new ManualResetEventSlim();
        _pumpThread = new Thread(HookThreadPump)
        {
            IsBackground = true,
            Name = "KeyboardHookPump",
        };
        _pumpThread.Start();
        _ready.Wait(2000);
    }

    // LL 钩子回调在本线程的消息循环里派发 — 永不被 UI 线程阻塞.
    // 健壮性: 之前 pump 每 ~15s 死一次 (OS 没明显发 WM_QUIT 但 GetMessageW
    // 返回 0) → 那段时间内 Alt+Tab 全走系统原生. 用户实测 "1次我们的, 2次系统".
    // 现在: try/catch 兜底, _stopRequested 标志, 任何退出路径都 log 原因,
    // 看守者间隔从 15s 缩到 2s → 死后 2s 内 reinstall, 用户感知不到.
    private void HookThreadPump()
    {
        // 代际隔离: 本线程只操作自己的局部 hook / 只在自己仍是"当前代"时
        // 写共享状态. 否则 Dispose→Install 交接时, 旧线程从 WM_QUIT 醒来跑
        // finally 会 UnhookWindowsHookEx(_hook) — 而 _hook 已指向新线程刚装
        // 的 hook → 新 hook 被自己卸掉 (键盘事件全丢), 并且把 _pumpAlive
        // 踩回 false → 看守者再装再被踩, 死循环. 实测症状: Alt+Tab / Alt+`
        // 全无反应, 日志每 15s 一轮 "reinstalled after death".
        var me = Thread.CurrentThread;
        IntPtr myHook = IntPtr.Zero;
        try
        {
            _pumpThreadId = GetCurrentThreadId();
            var hMod = GetModuleHandleW(null);
            myHook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
            if (myHook == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                Log.Error("KeyboardHook", $"SetWindowsHookEx failed err={err}");
                _ready!.Set();
                return;
            }
            _hook = myHook;
            Log.Info("KeyboardHook", $"installed (dedicated pump thread) tid={me.ManagedThreadId}");
            // 先标 alive 再放行 Install() 的 _ready.Wait — 否则看守者第一拍
            // 可能读到 false → 误判死.
            _pumpAlive = true;
            _ready!.Set();
            while (!_stopRequested)
            {
                if (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    _pumpAlive = true; // watchdog 通过 IsPumpAlive 周期性确认 pump 还在 pump.
                    DispatchMessageW(ref msg);
                }
                else
                {
                    // GetMessageW 返回 0 (WM_QUIT) 或 -1 (错误). 都视为退出.
                    Log.Warn("KeyboardHook",
                        $"GetMessageW<=0 stop={_stopRequested} — exiting pump");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception("KeyboardHook.pump", ex);
        }
        finally
        {
            // 只有仍是当前代才改共享状态 (已被 Install() 换代会跳过).
            if (ReferenceEquals(_pumpThread, me))
            {
                _pumpAlive = false; // 让 watchdog 立刻看见死亡
                _hook = IntPtr.Zero;
            }
            try { if (myHook != IntPtr.Zero) UnhookWindowsHookEx(myHook); } catch { }
            Log.Info("KeyboardHook",
                $"pump exited, hook removed tid={me.ManagedThreadId} current={_pumpThread?.ManagedThreadId} stop={_stopRequested}");
        }
    }

    /// <summary>App watchdog 周期性读, 确认 pump 线程还在循环.</summary>
    public bool IsPumpAlive => _pumpAlive;

    public void SetOverlayOpen(bool open)
    {
        _overlayOpen = open;
        Log.Info("KeyboardHook", $"overlay open={open}");
    }

    public void Dispose()
    {
        if (_pumpThread is null) return;
        _stopRequested = true;
        // 双保险: 既设标志, 又发 WM_QUIT, 以防 pump 卡在 GetMessageW.
        PostThreadMessageW(_pumpThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _pumpThread.Join(1000);
        _pumpThread = null;
        Log.Info("KeyboardHook", "disposed");
    }

    /// <summary>Dispose + Install. commit 后 OS 可能禁用 LL hook (前台切换
    /// 走的别进程), 强制重装让回调重新被 OS 调. App OnOverlayCommit 调.</summary>
    public void Reinstall()
    {
        try
        {
            Dispose();
            _shiftDown = false;
            _ctrlDown = false;
            _altDown = false;
            Install();
            Log.Info("KeyboardHook", "reinstalled after commit");
        }
        catch (Exception ex)
        {
            Log.Exception("KeyboardHook.Reinstall", ex);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var isUp = (data.flags & LLKHF_UP) != 0;
        var vk = data.vkCode;
        var sc = data.scanCode;
        // 调研 log: 每次 Tab/Alt 按键回调都记 fg/pid, 验证 OS 是否在我们
        // hook 之前就决定了切换器 (回调时焦点若已是切换器窗口 = OS 先决).
        if (vk == 0x09 || vk == 0x12 || vk == 0xA4 || vk == 0xA5
            || vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28)
        {
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out var fgPid);
            Log.Info("KeyboardHook",
                $"cb vk=0x{vk:X2} sc=0x{sc:X2} up={isUp} fg=0x{fg.ToInt64():X} pid={fgPid}");
        }
        // WPF Keyboard.Modifiers 有 UI 线程亲和 — 专用钩子线程上用原生查询.
        var shift = ShiftHeld();
        // Alt 实时状态 — 不依赖 _altDown 标志. 标志会被合成 Alt-up 事件
        // (前台切换后 OS 释放旧前台进程的 Alt) 错位; GetAsyncKeyState 直
        // 读物理状态, 更准.
        var altHeld = (GetAsyncKeyState(0x12) & 0x8000) != 0
                   || (GetAsyncKeyState(0xA4) & 0x8000) != 0
                   || (GetAsyncKeyState(0xA5) & 0x8000) != 0;

        // 修饰键跟踪: Alt (overlay 触发/提交) + quick 修饰键 (一次直达).
        // Alt 三键同时属于两个角色 — 都要更新, 缺一不可.
        if (vk is 0x10 or 0xA0 or 0xA1) _shiftDown = !isUp;
        else if (vk is 0x11 or 0xA2 or 0xA3) _ctrlDown = !isUp;
        if (vk == 0x12 || vk == 0xA4 || vk == 0xA5
            || Array.IndexOf(QuickModifierVirtualKeys, vk) >= 0)
        {
            if (vk == 0x12 || vk == 0xA4 || vk == 0xA5)
            {
                _altDown = !isUp;
                if (isUp && _overlayOpen && !_suppressAltCommit)
                {
                    Log.Info("KeyboardHook", "alt-up commit");
                    Fire(OverlayCommit);
                }
                // Alt-up 重置 suppress 标记 (下次再开第二热键再标记)
                if (isUp) _suppressAltCommit = false;
            }
            if (Array.IndexOf(QuickModifierVirtualKeys, vk) >= 0)
                _quickModDown = !isUp;
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // quick 数字键松开 → 跳转 (仅当 down 是被我们吞掉的那次).
        if (isUp && _quickDigitVk != 0 && vk == _quickDigitVk)
        {
            var digit = DigitFromVk(vk);
            _quickDigitVk = 0;
            Log.Info("KeyboardHook", $"quick jump up {digit}");
            Fire(() => QuickJumpUp?.Invoke(digit));
            return new IntPtr(1);
        }

        // Alt+Tab 拦截 (SuppressAltTab): 热键本身是 Tab 时触发分支已经
        // 吞掉了; 这里补拦截的是 "热键≠Tab 但配置了拦截" 的组合.
        if (SuppressAltTabActive && altHeld && !isUp && vk == 0x09
            && TriggerVirtualKey != 0x09)
        {
            Log.Info("KeyboardHook", "suppressed system alt+tab");
            return new IntPtr(1);
        }

        if (altHeld && !isUp)
        {
            Log.Info("KeyboardHook", $"alt+key vk=0x{vk:X2} sc=0x{sc:X2}");
            if (vk == TriggerVirtualKey || data.scanCode == TriggerScanCode)
            {
                if (!_overlayOpen) { _overlayOpen = true; Log.Info("KeyboardHook", "fire OverlayRequested"); Fire(OverlayRequested); }
                else { Log.Info("KeyboardHook", $"fire OverlayAdvance reverse={shift}"); Fire(() => OverlayAdvance?.Invoke(shift)); }
                return new IntPtr(1);
            }
            // 第二热键 (一按就开, 不挂 Alt-up commit): 仅在 _altDown 路径
            // 触发是因为第二热键必须含 Alt (HotkeyParser.HasAlt 校验);
            // 不参与 OverlayAdvance 循环 (主热键体感专属) — 仅首次开 overlay,
            // 第二次按同一热键不重复触发.
            if (SecondTriggerVirtualKey != 0
                && (vk == SecondTriggerVirtualKey || data.scanCode == SecondTriggerScanCode))
            {
                if (!_overlayOpen)
                {
                    _overlayOpen = true;
                    _suppressAltCommit = true; // 第二热键打开: Alt-up 不 commit
                    Log.Info("KeyboardHook", "fire OverlayRequested (second hotkey)");
                    Fire(OverlayRequested);
                }
                return new IntPtr(1);
            }
        }

        // Alt+Ctrl+Shift+←/→: 把窗口搬到相邻虚拟桌面 (原生 Win+Ctrl+Shift
        // +方向键 的同款语义: 只搬窗口, 不切桌面). 必须排在下面"方向键 =
        // 面板导航"之前 — 面板开着时 Alt 一定按着, 不先拦掉就变成移动选中项
        // 而不是迁移窗口.
        if (!isUp && (vk == 0x25 || vk == 0x27) && MoveWindowModsHeld())
        {
            var dir = vk == 0x25 ? -1 : 1;
            Log.Info("KeyboardHook", $"virtual desktop move window {(dir < 0 ? "left" : "right")}");
            Fire(() => VirtualDesktopMove?.Invoke(dir));
            return new IntPtr(1);
        }

        // Esc / 方向键 / 数字键: 必须 Alt 按着 OR overlay 开着 (第二热键
        // 开 overlay 后松 Alt, 这些键仍要响应). 主热键开 overlay 路径下
        // Alt 一直按着, 也覆盖; overlay 关且 Alt up 时不进 — 让原生
        // Esc/方向键照常工作.
        if ((altHeld || _overlayOpen) && !isUp)
        {
            if (vk == 0x1B)
            {
                if (_overlayOpen)
                {
                    Log.Info("KeyboardHook", "fire OverlayEscape");
                    Fire(OverlayEscape);
                }
                else
                {
                    Fire(EscapePressed);
                }
                return new IntPtr(1);
            }
            if (_overlayOpen)
            {
                if (vk == 0x26 || vk == 0x28 || vk == 0x25 || vk == 0x27)
                {
                    var vkArrow = (int)vk;
                    Fire(() => OverlayArrow?.Invoke(vkArrow));
                    return new IntPtr(1);
                }
                // overlay 开着: 数字键 (主行 + numpad) 跳编号; 第二热键场景
                // 下不需要按 Alt, 是因为 overlay 已经"接管屏幕".
                if (vk is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69)
                {
                    var digit = DigitFromVk(vk);
                    Log.Info("KeyboardHook", $"digit {digit} (overlay open)");
                    Fire(() => OverlayDigit?.Invoke(digit));
                    return new IntPtr(1);
                }
            }
        }

        // 虚拟桌面 (overlay closed): 修饰键前缀 + 数字 → 切第 N 个虚拟桌面.
        // 必须排在"一次直达"前面: 默认 quick 修饰键就是 Alt, Alt+Ctrl+1
        // 会同时满足一次直达的条件, 不先拦掉就会两个都触发.
        if (!_overlayOpen && !isUp
            && VirtualDesktopModifierVks.Length > 0
            && vk is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69
            && VirtualDesktopModsHeld())
        {
            var digit = DigitFromVk(vk);
            Log.Info("KeyboardHook", $"virtual desktop switch {digit}");
            Fire(() => VirtualDesktopSwitch?.Invoke(digit));
            return new IntPtr(1);
        }

        // 一次直达 (overlay closed): quick+数字. 整个组合被吞掉 — 不让
        // 焦点应用收到 Alt+1 (浏览器切 tab 之类). 按住等 QuickJumpUp,
        // App 层区分 快速松开=跳转 / 按住堆叠≥300ms=子标签页面.
        if (!_overlayOpen && _quickModDown && !isUp && _quickDigitVk == 0
            && !VirtualDesktopModsHeld()
            && vk is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69)
        {
            var digit = DigitFromVk(vk);
            _quickDigitVk = vk;
            Log.Info("KeyboardHook", $"quick jump down {digit}");
            Fire(() => QuickJumpDown?.Invoke(digit));
            return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static int DigitFromVk(uint vk) =>
        vk >= 0x60 ? (int)(vk - 0x60) : (int)(vk - 0x30);

    // 修饰键组位掩码 (Alt=1, Ctrl=2, Shift=4, Win=8).
    private const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;

    private static int ModMask(uint[] vks)
    {
        var mask = 0;
        foreach (var k in vks)
            mask |= k switch
            {
                0x12 or 0xA4 or 0xA5 => ModAlt,
                0x11 or 0xA2 or 0xA3 => ModCtrl,
                0x10 or 0xA0 or 0xA1 => ModShift,
                0x5B or 0x5C => ModWin,
                _ => 0,
            };
        return mask;
    }

    /// <summary>虚拟桌面热键此刻是否"完整按住". 两个条件:
    /// 1) 前缀里的修饰键组全都按着;
    /// 2) 前缀必须比"一次直达"的修饰键**更具体** (严格超集) — 否则
    /// Alt+1 这种组合会同时命中两者 (用户把前缀也设成 Alt 时就撞了).</summary>
    private bool VirtualDesktopModsHeld()
    {
        var vd = ModMask(VirtualDesktopModifierVks);
        if (vd == 0) return false;
        if ((vd & ~ModMask(QuickModifierVirtualKeys)) == 0) return false;
        if ((vd & ModAlt) != 0 && !AltHeld()) return false;
        if ((vd & ModCtrl) != 0 && !CtrlHeld()) return false;
        if ((vd & ModShift) != 0 && !ShiftHeld()) return false;
        if ((vd & ModWin) != 0 && !WinHeld()) return false;
        return true;
    }

    /// <summary>迁移窗口热键此刻是否完整按住. 要求前缀比"一次直达"的修饰键
    /// 更具体 (严格超集) — 否则用户把前缀设成 Alt 时, Alt+←/→ 会被抢走
    /// (面板开着时那是移动选中项).</summary>
    private bool MoveWindowModsHeld()
    {
        var m = ModMask(MoveWindowModifierVks);
        if (m == 0) return false;
        if ((m & ~ModMask(QuickModifierVirtualKeys)) == 0) return false;
        if ((m & ModAlt) != 0 && !AltHeld()) return false;
        if ((m & ModCtrl) != 0 && !CtrlHeld()) return false;
        if ((m & ModShift) != 0 && !ShiftHeld()) return false;
        if ((m & ModWin) != 0 && !WinHeld()) return false;
        return true;
    }

    // 修饰键"此刻按着" — LLKHF_ALTDOWN 在 WM_KEYDOWN 上不可靠 (见
    // reference/VirtualDesktopSwitcher/notes/dev.md), 一律现场读 async 状态.
    private static bool KeyHeld(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    private static bool AltHeld() => KeyHeld(0x12) || KeyHeld(0xA4) || KeyHeld(0xA5);
    private bool CtrlHeld() => _ctrlDown || KeyHeld(0x11) || KeyHeld(0xA2) || KeyHeld(0xA3);
    private bool ShiftHeld() => _shiftDown || KeyHeld(0x10) || KeyHeld(0xA0) || KeyHeld(0xA1);
    private static bool WinHeld() => KeyHeld(0x5B) || KeyHeld(0x5C);

    // ---------- 专用泵线程的 message loop P/Invoke (self-contained) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void Fire(Action? action)
    {
        if (action is null) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
}
