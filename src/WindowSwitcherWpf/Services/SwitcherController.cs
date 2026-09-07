using System;
using System.Collections.ObjectModel;
using System.Linq;
using WindowSwitcherWpf.Interop;
using WindowSwitcherWpf.Models;

namespace WindowSwitcherWpf.Services;

/// <summary>
/// Glue between the keyboard hook, the overlay, the live enumeration, and
/// persistence. Owns the active group list and selection index.
/// </summary>
public sealed class SwitcherController
{
    private readonly WindowEnumerator _enumerator;
    private readonly WindowGroupingService _grouping;
    private readonly OrderStore _store;
    private readonly Action<IntPtr> _activate;
    private OrderFile _order;

    public ObservableCollection<WindowGroup> Groups { get; private set; } = new();
    public int ActiveGroupIndex { get; private set; }
    public int ActiveWindowIndex { get; private set; }
    public bool HasWindows => Groups.Count > 0;
    public event Action? StateChanged;

    public SwitcherController(WindowEnumerator enumerator,
        WindowGroupingService grouping,
        OrderStore store,
        Action<IntPtr> activate)
    {
        _enumerator = enumerator;
        _grouping = grouping;
        _store = store;
        _activate = activate;
        _order = store.Load();
    }

    public void OpenOverlay()
    {
        Refresh();
        ActiveGroupIndex = 0;
        ActiveWindowIndex = 0;
        WindowSwitcherWpf.Services.Log.Info("Controller", $"OpenOverlay groups={Groups.Count}");
        StateChanged?.Invoke();
    }

    public void Advance(bool reverse)
    {
        if (Groups.Count == 0) return;
        ActiveGroupIndex = reverse
            ? (ActiveGroupIndex - 1 + Groups.Count) % Groups.Count
            : (ActiveGroupIndex + 1) % Groups.Count;
        ActiveWindowIndex = 0;
        StateChanged?.Invoke();
    }

    public void SelectByDigit(int digit)
    {
        if (Groups.Count == 0) return;
        var group = Groups[ActiveGroupIndex];
        if (digit < group.Windows.Count)
        {
            ActiveWindowIndex = digit;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Makes <paramref name="hwnd"/> the committed-on-Enter target
    /// (mouse click path — the visual selection must match the controller).</summary>
    public bool SelectByHwnd(IntPtr hwnd)
    {
        for (var gi = 0; gi < Groups.Count; gi++)
        {
            var g = Groups[gi];
            for (var wi = 0; wi < g.Windows.Count; wi++)
            {
                if (g.Windows[wi].Hwnd != hwnd) continue;
                ActiveGroupIndex = gi;
                ActiveWindowIndex = wi;
                StateChanged?.Invoke();
                return true;
            }
        }
        return false;
    }

    public void Commit()
    {
        if (!HasWindows) return;
        var group = Groups[ActiveGroupIndex];
        if (group.Windows.Count == 0) return;
        var entry = group.Windows[Math.Min(ActiveWindowIndex, group.Windows.Count - 1)];
        WindowSwitcherWpf.Services.Log.Info("Controller", $"commit 0x{entry.Hwnd.ToInt64():X} '{entry.Title}'");
        _activate(entry.Hwnd);
    }

    public void Cancel() { WindowSwitcherWpf.Services.Log.Info("Controller", "cancel"); }

    public void ReloadOrder()
    {
        _order = _store.Load();
        Refresh();
        StateChanged?.Invoke();
    }

    public void PersistOrder()
    {
        _order.Groups = Groups.Select(g => new GroupDefinition(g.Key, g.DisplayName)).ToList();
        _store.SaveDebounced(_order);
    }

    public void MoveGroup(int from, int to)
    {
        if (from < 0 || from >= Groups.Count) return;
        to = Math.Clamp(to, 0, Groups.Count - 1);
        if (from == to) return;
        var item = Groups[from];
        Groups.Move(from, to);
        ActiveGroupIndex = Groups.IndexOf(item);
        if (ActiveGroupIndex < 0) ActiveGroupIndex = 0;
        StateChanged?.Invoke();
        PersistOrder();
    }

    private void Refresh()
    {
        try
        {
            var live = _enumerator.Enumerate(onlyCurrentDesktop: true);
            var grouped = _grouping.Group(live, _order);
            Groups = grouped;
            if (ActiveGroupIndex >= Groups.Count) ActiveGroupIndex = 0;
            if (ActiveWindowIndex >= (Groups.Count == 0 ? 0 : Groups[ActiveGroupIndex].Windows.Count))
                ActiveWindowIndex = 0;
        }
        catch (Exception ex)
        {
            WindowSwitcherWpf.Services.Log.Exception("Controller", ex);
        }
    }
}
