using System;
using System.Runtime.InteropServices;
using static WindowSwitcherWpf.Interop.NativeMethods;

namespace WindowSwitcherWpf.Interop;

/// <summary>
/// Lightweight wrapper over DWM thumbnail API. Used by the overlay to render
/// a live snapshot of each window into a WPF Image. Falls back to
/// <see cref="DestroyThumbnail"/> on unregister / window close.
/// </summary>
public sealed class ThumbnailHandle : IDisposable
{
    public IntPtr ThumbnailId { get; private set; }
    public IntPtr Source { get; }
    public IntPtr Destination { get; }
    public bool IsValid => ThumbnailId != IntPtr.Zero;

    public ThumbnailHandle(IntPtr source, IntPtr destination)
    {
        Source = source;
        Destination = destination;
        if (DwmRegisterThumbnail(destination, source, out var id) == 0)
            ThumbnailId = id;
    }

    public bool Update(Rectangle destination, Rectangle source, bool visible = true)
    {
        if (!IsValid) return false;
        // NB: never add DWM_TNP_SOURCECLIENTAREA here — it ghosts Mica
        // backdrop windows (alpha-0 raw surface). Keep rcSource
        // window-relative, like native Alt+Tab.
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE
                    | DWM_TNP_OPACITY,
            fVisible = visible,
            opacity = 255,
            rcDestination = new RECT { Left = destination.Left, Top = destination.Top, Right = destination.Right, Bottom = destination.Bottom },
            rcSource = new RECT { Left = source.Left, Top = source.Top, Right = source.Right, Bottom = source.Bottom },
        };
        return DwmUpdateThumbnailProperties(ThumbnailId, ref props) == 0;
    }

    public void Dispose()
    {
        if (IsValid)
        {
            DwmUnregisterThumbnail(ThumbnailId);
            ThumbnailId = IntPtr.Zero;
        }
    }
}

public readonly record struct Rectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class ThumbnailCapture
{
    public static ThumbnailHandle Register(IntPtr source, IntPtr destination) =>
        new(source, destination);

    public static ThumbnailHandle TryRegisterForSource(IntPtr source, IntPtr overlayHwnd) =>
        new(source, overlayHwnd);
}
