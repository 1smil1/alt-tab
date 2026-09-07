using System;
using System.IO;
using System.Threading;
using System.Windows;
using WindowSwitcherWpf.Interop;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf;

public partial class App : Application
{
    private static readonly bool IsFirstInstance;
    private static readonly Mutex? SingleInstanceMutex;

    private KeyboardHook? _keyboard;
    private ForegroundWatcher? _foreground;
    private TrayService? _tray;
    private OrderStore? _orderStore;
    private WindowGroupingService? _grouping;
    private SwitcherController? _controller;
    private SwitcherOverlay? _overlay;
    private MainWindow? _host;

    static App()
    {
        SingleInstanceMutex = new Mutex(initiallyOwned: true,
            "Global\\WindowSwitcherWpfMutex", out var isOwner);
        IsFirstInstance = isOwner;
    }

    [STAThread]
    public static int Main()
    {
        if (!IsFirstInstance)
        {
            MessageBox.Show("Window Switcher is already running.", "Window Switcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            Log.Error("AppDomain", "UnhandledException: " + args.ExceptionObject);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, args) =>
        {
            Log.Exception("Dispatcher", args.Exception);
            args.Handled = true;
        };
        Log.Info("App", $"startup args={string.Join(' ', e.Args)}");
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--debug-enum")
        {
            RunDebugEnum();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--enumerate")
        {
            RunEnumerate();
            Shutdown();
            return;
        }

        _orderStore = new OrderStore();
        _grouping = new WindowGroupingService();

        _host = new MainWindow();
        _host.Show();
        _host.Hide();

        _controller = new SwitcherController(
            new WindowEnumerator(),
            _grouping,
            _orderStore,
            hwnd => ActivationService.Activate(hwnd));

        _overlay = new SwitcherOverlay(_controller);
        _overlay.Hide();

        _keyboard = new KeyboardHook();
        _keyboard.OverlayRequested += OnOverlayRequested;
        _keyboard.OverlayAdvance += OnOverlayAdvance;
        _keyboard.OverlayCommit += OnOverlayCommit;
        _keyboard.OverlayCancel += OnOverlayCancel;
        _keyboard.EscapePressed += () => _overlay?.Cancel();
        _keyboard.Install();

        _foreground = new ForegroundWatcher();
        _foreground.Install();

        _tray = new TrayService(
            onReload: () => _controller?.ReloadOrder(),
            onOpenConfig: () =>
            {
                if (_orderStore is not null)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(_orderStore.FilePath);
                        if (!string.IsNullOrEmpty(dir))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = dir,
                                UseShellExecute = true,
                            });
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }
            });
    }

    private void OnOverlayRequested()
    {
        Log.Info("App", "OnOverlayRequested");
        _overlay?.ShowAndFocus();
        _keyboard?.SetOverlayOpen(true);
    }

    private void OnOverlayAdvance(bool reverse) => _overlay?.Advance(reverse);

    private void OnOverlayCommit()
    {
        Log.Info("App", "OnOverlayCommit");
        // Activate the selected window FIRST (the overlay window still
        // owns the foreground while visible; we have to release that
        // before the target window can take focus).
        _controller?.Commit();
        _overlay?.Commit();
        _keyboard?.SetOverlayOpen(false);
    }

    private void OnOverlayCancel()
    {
        Log.Info("App", "OnOverlayCancel");
        _overlay?.Cancel();
        _keyboard?.SetOverlayOpen(false);
    }

    private void RunDebugEnum()
    {
        var enumerator = new WindowEnumerator();
        foreach (var w in enumerator.Enumerate(onlyCurrentDesktop: true))
        {
            Console.WriteLine($"{w.GroupKey}\t{w.ProcessName}\t{w.Title}\t0x{w.Hwnd.ToInt64():X}");
        }
    }

    private void RunEnumerate()
    {
        var enumerator = new WindowEnumerator();
        var list = enumerator.Enumerate(onlyCurrentDesktop: true);
        Console.WriteLine($"=== final count = {list.Count} ===");
        foreach (var w in list)
        {
            var exStyle = WindowSwitcherWpf.Interop.NativeMethods.GetWindowLongPtr64(
                w.Hwnd, WindowSwitcherWpf.Interop.NativeMethods.GWL_EXSTYLE);
            var style = WindowSwitcherWpf.Interop.NativeMethods.GetWindowLongPtr64(
                w.Hwnd, WindowSwitcherWpf.Interop.NativeMethods.GWL_STYLE);
            Console.WriteLine($"  0x{w.Hwnd.ToInt64():X} ex=0x{exStyle:X} st=0x{style:X} '{w.Title}' [{w.ProcessName}]");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _foreground?.Dispose();
        _keyboard?.Dispose();
        SingleInstanceMutex?.ReleaseMutex();
        SingleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
