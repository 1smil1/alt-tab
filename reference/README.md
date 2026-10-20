# reference/ — 上游参考代码（只读参考，不参与编译）

本目录是**外部项目的源码副本**，只为对照 Windows 虚拟桌面 API 的实现方式。
不参与本仓库的编译，不做修改。

| 目录 | 上游 | 许可 | 我们参考了什么 |
|---|---|---|---|
| `WindowsVirtualDesktopHelper/` | https://github.com/dankrusi/WindowsVirtualDesktopHelper （966★） | MIT | `Source/VirtualDesktopAPI/` 下的 COM interop 与按 Windows 版本分派的实现（`Loader.cs` 决定了本机 22631.6199 走 `VirtualDesktopWin11_23H2_2921`） |
| `VirtualDesktop/` | https://github.com/MScholtes/VirtualDesktop （794★） | MIT | `VirtualDesktop11.cs` / `VirtualDesktop11-24H2.cs`：切换桌面所需的最小 COM 调用序列（`GetCount` / `GetDesktops` / `GetCurrentDesktop` / `SwitchDesktop`），以及 24H2 vtable 多一个方法的差异 |
| `VirtualDesktopSwitcher/` | https://github.com/choyy/VirtualDesktopSwitcher （169★，中文，2026-08 仍更新） | MIT | **交互设计**参考：`Alt+1..9` 直达桌面、`Alt+\`` 回上一桌面、**拖窗口到指示器移桌面**、**窗口固定到所有桌面**（`IVirtualDesktopPinnedApps`）、切桌面后激活目标桌面顶层窗口；`notes/dev.md` 记录了两个坑：`LLKHF_ALTDOWN` 不可靠、`IsWindowOnCurrentVirtualDesktop` 不可靠 |

## star 数说明

`WindowsVirtualDesktopHelper`(966) 与 `VirtualDesktop`(794) 不是星数最多的虚拟桌面项目，但它们是
**COM interop 实现最干净、最权威**的两份（其它项目基本都转手自它们或 `zgdump/windows-virtualdesktopindicator`）。
星数更高的项目见下表，实现上各有取舍：

| 项目 | ★ | 语言 | 做法 | 我们不选它的原因 |
|---|---|---|---|---|
| `sdias/win-10-virtual-desktop-enhancer` | 1792 | AutoHotkey | 靠 `VirtualDesktopAccessor.dll`，2023 起停止维护 | AHK 脚本，无法内嵌进 WPF 进程；热键写死在 ini |
| `henkman/virgo` | 1462 | C（323 行） | **根本不用 Windows 虚拟桌面** —— 自己 `ShowWindow` 显隐伪造 4 个桌面 | 不是真桌面，跟我们的目标不符（我们要的是真桌面） |
| `pmb6tz/windows-desktop-switcher` | 1416 | AutoHotkey | `CapsLock+数字` 切桌面，依赖 DLL | AHK 外挂 |
| `Ciantic/VirtualDesktopAccessor` | 1118 | C++ DLL | 给脚本调用的 DLL | 只是 DLL，没交互层 |
| `choyy/VirtualDesktopSwitcher` | 169 | C++ | 真桌面 + 完整交互层 | ★ 少但**交互最接近我们**（拖窗口移桌面 / 固定到所有桌面） |

我们自己的 `Interop/VirtualDesktop.cs` 按 build 号门控，比上面所有项目都严格：
Windows 更新改了 vtable 时是"功能不生效 + 日志说明"，不是随机崩溃。

两边都基于 `zgdump/windows-virtualdesktopindicator` 的思路（MIT）。

## 我们的实现

`src/WindowSwitcherWpf/Interop/VirtualDesktop.cs` 是按上面两份代码**改写**的精简版：

- 只保留切桌面需要的接口（`IServiceProvider10` / `IVirtualDesktopManagerInternal` / `IVirtualDesktop` /
  `IObjectArray` / 公开的 `IVirtualDesktopManager`）。
- 按 Windows build 选 vtable 变体：22621–26099 用 23H2 变体，≥26100 用 24H2 变体
  （24H2 的 `IVirtualDesktopManagerInternal` 多了一个 `SwitchDesktopAndMoveForegroundView`，
  照抄 23H2 的接口声明会调错槽位）。
- 所有 COM 调用都包在 try/catch 里，失败只写日志、绝不让 app 崩。

## 许可

两份上游均为 MIT 许可（见各自目录内的 `LICENSE.md` / `LICENSE`）。
我们改写自它们的代码保留原作者署名，注释里标注了来源。
