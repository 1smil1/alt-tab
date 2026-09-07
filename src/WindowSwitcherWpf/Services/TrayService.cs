using System;
using System.IO;
using System.Reflection;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// System-tray menu. Wraps <c>Hardcodet.NotifyIcon.Wpf</c>; entry points for
/// "Open order.json", "Reload" and "Exit". The icon is loaded from the
/// embedded assembly resource <c>Assets\app.ico</c> if present, otherwise the
/// default stock icon is used.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly Action _onReload;
    private readonly Action _onOpenConfig;

    public TrayService(Action onReload, Action onOpenConfig)
    {
        _onReload = onReload;
        _onOpenConfig = onOpenConfig;
        _tray = new TaskbarIcon
        {
            ToolTipText = "Window Switcher",
            Visibility = Visibility.Visible,
        };
        _tray.Icon = LoadIcon();
        var menu = new System.Windows.Controls.ContextMenu();

        var reload = new System.Windows.Controls.MenuItem { Header = "Reload order" };
        reload.Click += (_, _) => _onReload();
        var open = new System.Windows.Controls.MenuItem { Header = "Open order.json" };
        open.Click += (_, _) => _onOpenConfig();
        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(reload);
        menu.Items.Add(open);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exit);
        _tray.ContextMenu = menu;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resourceName = "WindowSwitcherWpf.Assets.app.ico";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is not null) return new System.Drawing.Icon(stream);
        var localPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(localPath)) return new System.Drawing.Icon(localPath);
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose() => _tray.Dispose();
}
