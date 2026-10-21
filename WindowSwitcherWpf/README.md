# Window Switcher (WPF / .NET 8)

A modern Windows Alt+Tab replacement. Live window previews in a Windows 11 style overlay.

## Status: v0.1 (early preview)

This is the first version. The default hotkey is **Alt+`** (backtick). The system Alt+Tab is **not** intercepted yet — that's the next milestone.

## Features

- Live window previews captured via `PrintWindow(PW_RENDERFULLCONTENT)`
- Windows 11 style overlay: light rounded panel, drop shadow, wrap-grid of thumbnails
- Same-application grouping (each visible window is its own card)
- Group key for pinned order persisted in `%APPDATA%\WindowSwitcherWpf\order.json`
- Single-instance via named mutex; tray icon with Reload / Open config folder / Exit
- Windows virtual desktop support (undocumented COM, gated per Windows build):
  - switch desktops with a configurable prefix + digit (`Alt+Ctrl`+`2`)
  - move a window to the adjacent desktop with a configurable prefix + arrow keys
    (`Alt+Ctrl+Shift`+`←`/`→`)
  - drag a card onto the desktop bar at the top of the panel to move that window to
    another desktop, or press the migrate prefix + digit while the panel is open
  - desktop bar shows every desktop, highlights the current one, `✕` deletes it, `+`
    creates one
  - tag scope is configurable: per-desktop (Windows Alt+Tab behaviour, the default)
    or one shared list across all desktops
- Single hotkey: hold Alt, tap ` to cycle, release Alt to commit, Esc to cancel
- Arrow keys cycle, Enter commits
- `--debug-enum` CLI mode prints the live enumeration for verification

## Build

```bash
cd D:\window_switcher
dotnet build -c Release
```

Output: `src\WindowSwitcherWpf\bin\Release\net8.0-windows\WindowSwitcherWpf.exe`

## Run

```bash
dotnet run -c Release --project src\WindowSwitcherWpf
```

Verify the enumeration matches the reference:

```bash
dotnet run -c Release --project src\WindowSwitcherWpf -- --debug-enum
```

## Reference

This project is a from-scratch C#/WPF re-implementation of
[`sigoden/window-switcher`](https://github.com/sigoden/window-switcher) (MIT, v1.19.0),
which lives in `D:\window_switcher\window-switcher\` for reference. We study
that codebase but do not vendor any of its code. The upstream license is
preserved at `REFERENCE_LICENSE`.

Key reference files we read:

- `src/utils/window.rs` — EnumWindows pipeline, AUMID splitting for Chrome/Edge
- `src/keyboard.rs` — WH_KEYBOARD_LL state machine (tap-once, hold-and-cycle)
- `src/painter.rs` — overlay rendering patterns
- `src/foreground.rs` — SetWinEventHook blacklist pattern

### Virtual desktop references

The Windows virtual desktop support (`Interop/VirtualDesktop.cs`) is written from
scratch against undocumented COM interfaces, using these projects as reference.
Source copies live under [`reference/`](reference/README.md) (not compiled, not
modified):

| Project | ★ | License | What we took from it |
|---|---|---|---|
| [dankrusi/WindowsVirtualDesktopHelper](https://github.com/dankrusi/WindowsVirtualDesktopHelper) | 966 | MIT | COM interop layout and the per-Windows-build dispatch (`VirtualDesktopWin11_23H2_2921` is what this machine, build 22631.6199, uses) |
| [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) | 794 | MIT | Minimal call sequence to switch desktops (`GetCount` / `GetDesktops` / `GetCurrentDesktop` / `SwitchDesktop`) and the 24H2 vtable difference |
| [choyy/VirtualDesktopSwitcher](https://github.com/choyy/VirtualDesktopSwitcher) | 169 | MIT | Interaction design: desktop hotkeys, dragging a window onto a desktop indicator, pinning windows to all desktops; its `notes/dev.md` documents two traps — `LLKHF_ALTDOWN` and `IsWindowOnCurrentVirtualDesktop` are unreliable |

Both MIT licenses are preserved in their `reference/` subdirectories, and our
rewritten interop files keep the original attribution in their headers.
`reference/README.md` also lists the higher-starred alternatives we rejected
(AutoHotkey wrappers, DLL shims, and one project that fakes desktops with
`ShowWindow` instead of using real ones) and why.

## Roadmap

- v0.2: drag-to-reorder of cards
- v0.3: optional replacement of the system Alt+Tab (gated behind a setting)
- v0.4: hotkey customization via JSON
- v0.5: same-app grouping with one card per group + expand
