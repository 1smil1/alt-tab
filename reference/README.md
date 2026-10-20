# reference/ — 上游参考代码（只读参考，不参与编译）

本目录是**外部项目的源码副本**，只为对照 Windows 虚拟桌面 API 的实现方式。
不参与本仓库的编译，不做修改。

| 目录 | 上游 | 许可 | 我们参考了什么 |
|---|---|---|---|
| `WindowsVirtualDesktopHelper/` | https://github.com/dankrusi/WindowsVirtualDesktopHelper （966★） | MIT | `Source/VirtualDesktopAPI/` 下的 COM interop 与按 Windows 版本分派的实现（`Loader.cs` 决定了本机 22631.6199 走 `VirtualDesktopWin11_23H2_2921`） |
| `VirtualDesktop/` | https://github.com/MScholtes/VirtualDesktop （794★） | MIT | `VirtualDesktop11.cs` / `VirtualDesktop11-24H2.cs`：切换桌面所需的最小 COM 调用序列（`GetCount` / `GetDesktops` / `GetCurrentDesktop` / `SwitchDesktop`），以及 24H2 vtable 多一个方法的差异 |

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
