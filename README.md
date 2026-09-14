# alt-tab

**Languages:** [English](README.md) · [简体中文](README.zh-CN.md)

A native **Alt-Tab replacement for Windows** — pinned slot numbers (1..10 stay forever), stack cascade, Alt+digit quick jump. C#/WPF/.NET 8, ~50MB self-contained installer.

![screenshot](docs/screenshot.png)

## Features

- **Pinned slot model (fixed = fixed digit)**: pin a window to a number and it stays there forever (slot 3 is always slot 3); unpinned windows flow into the remaining digits by recent use; dragging an unpinned window next to a pinned one makes it "naturally" claim the vacant digit.
- **Full Alt+Tab replacement overlay**: live DWM-composed thumbnails (Mica background renders correctly), copy card of the current window (leftmost), most-recent card (slot 0, always present).
- **Quick-jump toggle**: `Alt+digit` jumps straight to that slot without opening the overlay; if the target is already foreground, pressing again **minimizes** it, and again restores.
- **Same-app stacking (cascade sub-cards)**: multiple windows of the same app collapse into one card, fanned out left-to-right; `Alt+digit` (held) enters the stack's member view — digits/arrows move between members, drag out = peel.
- **Member pin**: pin a position inside a stack; the parent stack pin and member pin stay in sync.
- **Smart sort**: rules (by process / path / title / group) reorder the unpinned flow fill; pinned cards are never moved by sort rules.
- **Templates**: export the current slot layout as a template, then apply later one-to-one (a template is a complete pin set).
- **Duplicate copy cards**: when the current window or most-recent window is itself a pinned window, the real card stays in the numbered region and a display copy appears at the front — "pinned cards stay forever unless unpinned".
- **Tray-resident**: left-click = settings, right-click menu, auto-re-attach on Explorer restart.
- **Lightweight**: GC heap ceiling 128MB, low idle memory.

## Install

Download `alt-tab-setup-*.exe` from [Releases](../../releases):

1. Double-click to install (custom path, no admin needed).
2. Optional: create a desktop shortcut / launch at startup.
3. Runs in the tray; press `Alt+Tab` anywhere to invoke.

Uninstall: Windows Settings → Apps → alt-tab. Uninstall keeps your settings and pins (`%APPDATA%\WindowSwitcherWpf`).

## Usage

Default hotkeys (all editable in the Settings window):

| Key | Action |
|---|---|
| `Alt + Tab` | Open the overlay; hold Alt and tap Tab to cycle, release Alt to commit, `Esc` to cancel |
| `Alt + 1..9` | Quick-jump to that digit (no overlay); target already foreground = minimize |
| `Alt + 0` | Quick-jump to the most-recently-used window |
| Digit keys (in overlay) | Jump to that slot's card; for a stack card, enter its member view |
| Arrow keys | Move in the overlay grid; `Esc` cancels or returns |

Mouse: click a card to activate; 📌 pin/unpin; ✕ close; drag to reorder (dragging next to a pin claims the vacant digit); drag onto another card = stack, drag out of a stack = peel.

## Configuration

Settings window (tray left-click / ⚙ top-right of the overlay): main hotkey, quick-jump modifier, Alt+Tab suppression, templates, smart-sort rules — all hot-applied on save.

Data files (no manual editing needed):

- `%APPDATA%\WindowSwitcherWpf\config.json` — settings (hotkey / templates / smart sort); stacks also persist here.
- `%APPDATA%\WindowSwitcherWpf\order.json` — pins (including stack-member pins).

The legacy v1.0 portable `config.json` next to the exe is auto-migrated to the above location on first run; the old file is then deleted.

## Build

```bash
dotnet build -c Release src/WindowSwitcherWpf
# output: src/WindowSwitcherWpf/bin/Release/net8.0-windows/WindowSwitcherWpf.exe

# self-tests (pure in-memory, never touch real config)
src/WindowSwitcherWpf/bin/Release/net8.0-windows/WindowSwitcherWpf.exe --test-slots
src/WindowSwitcherWpf/bin/Release/net8.0-windows/WindowSwitcherWpf.exe --test-stack
src/WindowSwitcherWpf/bin/Release/net8.0-windows/WindowSwitcherWpf.exe --test-config

# installer (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
installer/build-installer.cmd
```

## Alternatives

| Project | Platform | Paradigm | Compared to alt-tab |
|---|---|---|---|
| [MrBeanCpp/AltTaber](https://github.com/MrBeanCpp/AltTaber) | Windows | Native-style cycle + app grouping | No pinned slots, no stacking, no quick-jump toggle |
| [sigoden/window-switcher](https://github.com/sigoden/window-switcher) | Windows | Rust · app groups + single hotkey cycle | No pinned slots, no stacking, no quick-jump toggle |
| Windows built-in `Alt+Tab` | Windows | Pure MRU flow | No pinned slots, no stacking, depends on Windows shell thumbnails |
| **alt-tab** (this project) | Windows | **Pinned slots + stack cascade + quick-jump toggle** | — |

## Acknowledgements

Interaction design references [sigoden/window-switcher](https://github.com/sigoden/window-switcher) (MIT). This is an **independent original implementation** (C#/WPF rewritten from scratch), not a fork; no source copied. Their MIT notice is at [REFERENCE_LICENSE](REFERENCE_LICENSE).

## License

[MIT](LICENSE) © 2026 1smil1
