using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WindowSwitcherWpf.Services;

namespace WindowSwitcherWpf.Interop;

/// <summary>一个虚拟桌面: 序号 (0 基) + GUID (稳定标识) + 名字.</summary>
public sealed record DesktopInfo(int Index, Guid Id, string Name);

/// <summary>
/// Windows 虚拟桌面 (Win10/11) 的最小 COM interop: 桌面数量 / 当前桌面 /
/// 切桌面 / 窗口所属桌面.
///
/// 改写自 (MIT 许可, 保留原作者署名):
///   - MScholtes/VirtualDesktop  — VirtualDesktop11.cs (切换序列最小实现)
///   - dankrusi/WindowsVirtualDesktopHelper — Source/VirtualDesktopAPI/
/// 关键坑: `IVirtualDesktopManagerInternal` 的 **vtable 随 Windows 版本变**
/// (24H2 起多了一个 SwitchDesktopAndMoveForegroundView), 照抄错版本的接口
/// 声明会调到错误的槽位 —— 那是非托管崩溃, catch 不住. 所以按 build 号
/// 选变体, 不认识的版本**直接不碰 COM** (只记日志), 绝不冒险.
///
/// 用法: 全是静态方法, 失败一律返回 false + 写日志, 绝不抛给调用方.
/// </summary>
public static class VirtualDesktop
{
    // ---- COM GUID ----
    private static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid IID_IVirtualDesktopManagerInternal = new("53F5CA0B-158F-4124-900C-057158060B27");
    private static readonly Guid IID_IVirtualDesktop = new("3F07F4BE-B107-441A-AF0F-39D82529072C");
    private static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    /// <summary>本机 build 号 (22631 = Win11 23H2, 26100+ = 24H2).</summary>
    private static int Build => Environment.OSVersion.Version.Build;

    /// <summary>UBR (修订号) — 同一 build 内的 COM 布局还会再变.</summary>
    private static int Revision => Environment.OSVersion.Version.Revision;

    /// <summary>内部 COM 的 vtable 只在 23H2 (22621–26099) 上验证过.
    /// 24H2 起多了一个方法, 槽位会错位 → 宁可不支持, 也不乱调.</summary>
    private static bool InternalApiSupported => Build is >= 22621 and < 26100;

    /// <summary>23H2 的 2921 变体 (CreateDesktop 多一个 IntPtr 参数).
    /// 判据同上游 dankrusi 的 Loader: build ≥ 22621 且 revision ∈
    /// [2921, 3007) 或 ≥ 3061.</summary>
    private static bool Uses2921Layout =>
        Build >= 22621 && ((Revision is >= 2921 and < 3007) || Revision >= 3061);

    // ---- COM 声明 ----

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    internal interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    internal interface IVirtualDesktop
    {
        bool IsViewVisible(IntPtr view);
        Guid GetId();
        [return: MarshalAs(UnmanagedType.HString)] string GetName();
        [return: MarshalAs(UnmanagedType.HString)] string GetWallpaperPath();
        bool IsRemote();
    }

    /// <summary>Win11 23H2 的接口声明 (上游 VirtualDesktop11.cs 原样).
    /// 方法顺序 = vtable 顺序, 不可改.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    internal interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void MoveDesktop(IVirtualDesktop desktop, int nIndex);
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
        IVirtualDesktop FindDesktop(ref Guid desktopid);
        void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray u1, out IObjectArray u2);
        void SetDesktopName(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
        void SetDesktopWallpaper(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string path);
        void UpdateWallpaperPathForAllDesktops([MarshalAs(UnmanagedType.HString)] string path);
        void CopyDesktopState(IntPtr view0, IntPtr view1);
        void CreateRemoteDesktop([MarshalAs(UnmanagedType.HString)] string path, out IVirtualDesktop desktop);
        void SwitchRemoteDesktop(IVirtualDesktop desktop, IntPtr switchtype);
        void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
        void GetLastActiveDesktop(out IVirtualDesktop desktop);
        void WaitForAnimationToComplete();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    internal interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
    }

    /// <summary>Win11 23H2 **2921 变体** (build 22621 且 revision ∈
    /// [2921,3007) 或 ≥3061 — 含本机 22631.6199): 与基线同一 IID、同一
    /// 槽位顺序, 但 CreateDesktop / MoveDesktop **多一个 IntPtr 参数**.
    /// 照基线声明去调 = 传参个数不对 → 读栈上的垃圾当 HWND 用.
    /// 只有要建桌面时才用这个声明.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    internal interface IVirtualDesktopManagerInternal2921
    {
        int GetCount();
        void MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop(IntPtr hWndOrMon);
        void MoveDesktop(IVirtualDesktop desktop, IntPtr hWndOrMon, int nIndex);
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
        IVirtualDesktop FindDesktop(ref Guid desktopid);
        void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray u1, out IObjectArray u2);
        void SetDesktopName(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
        void SetDesktopWallpaper(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string path);
        void UpdateWallpaperPathForAllDesktops([MarshalAs(UnmanagedType.HString)] string path);
        void CopyDesktopState(IntPtr view0, IntPtr view1);
        void CreateRemoteDesktop([MarshalAs(UnmanagedType.HString)] string path, out IVirtualDesktop desktop);
        void SwitchRemoteDesktop(IVirtualDesktop desktop, IntPtr switchtype);
        void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
        void GetLastActiveDesktop(out IVirtualDesktop desktop);
        void WaitForAnimationToComplete();
    }

    /// <summary>公开 COM (1903+): vtable 稳定, 查/移窗口所属桌面用.
    /// Phase 2 (卡上显示桌面号) 与 Phase 3 (迁窗口) 走这里.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    internal interface IVirtualDesktopManager
    {
        bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
        Guid GetWindowDesktopId(IntPtr topLevelWindow);
        void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private static IVirtualDesktopManagerInternal? _manager;
    private static IVirtualDesktopManager? _publicManager;
    private static bool _probed;

    /// <summary>桌面数量 (0 = 不可用).</summary>
    public static int Count
    {
        get
        {
            var mgr = GetManager();
            if (mgr is null) return 0;
            try
            {
                var n = mgr.GetCount();
                return n is >= 1 and <= 64 ? n : 0;
            }
            catch (Exception ex)
            {
                Log.Exception("VirtualDesktop", ex);
                return 0;
            }
        }
    }

    /// <summary>当前桌面序号 (0 基; -1 = 未知).</summary>
    public static int CurrentIndex
    {
        get
        {
            var mgr = GetManager();
            if (mgr is null) return -1;
            try
            {
                var cur = mgr.GetCurrentDesktop();
                return cur is null ? -1 : GetDesktopIndex(mgr, cur);
            }
            catch (Exception ex)
            {
                Log.Exception("VirtualDesktop", ex);
                return -1;
            }
        }
    }

    /// <summary>当前桌面的 GUID (空 = 未知).</summary>
    public static Guid CurrentGuid
    {
        get
        {
            var mgr = GetManager();
            if (mgr is null) return Guid.Empty;
            try
            {
                var cur = mgr.GetCurrentDesktop();
                return cur is null ? Guid.Empty : cur.GetId();
            }
            catch (Exception ex)
            {
                Log.Exception("VirtualDesktop", ex);
                return Guid.Empty;
            }
        }
    }

    /// <summary>全部桌面 (序号 + GUID + 名字). 空表 = 不可用.</summary>
    public static IReadOnlyList<DesktopInfo> List()
    {
        var mgr = GetManager();
        if (mgr is null) return Array.Empty<DesktopInfo>();
        try
        {
            var count = mgr.GetCount();
            var list = new List<DesktopInfo>(count);
            for (var i = 0; i < count; i++)
            {
                var d = GetDesktop(mgr, i);
                if (d is null) continue;
                var id = d.GetId();
                list.Add(new DesktopInfo(i, id, NameOf(id, i + 1)));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            return Array.Empty<DesktopInfo>();
        }
    }

    /// <summary>桌面名: 注册表里的自定义名 (Windows 存这儿). 没名字 = 空串
    /// (显示层自己补 "桌面 N"). 上游 (dankrusi / choyy) 读的都是这个键.</summary>
    private static string NameOf(Guid id, int oneBased)
    {
        try
        {
            var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{{{id}}}";
            var name = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\" + key, "Name", null)?.ToString();
            return name?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>切到指定桌面 (0 基). 失败 (不支持的 build / 桌面不存在 /
    /// COM 报错) 返回 false, 只写日志.</summary>
    public static bool SwitchTo(int index0)
    {
        var mgr = GetManager();
        if (mgr is null) return false;
        try
        {
            var count = mgr.GetCount();
            if (index0 < 0 || index0 >= count)
            {
                Log.Warn("VirtualDesktop",
                    $"桌面 {index0 + 1} 不存在 (共 {count} 个)");
                return false;
            }
            var desktop = GetDesktop(mgr, index0);
            if (desktop is null) return false;
            mgr.SwitchDesktop(desktop);
            Log.Info("VirtualDesktop", $"switch → 桌面 {index0 + 1}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            return false;
        }
    }

    /// <summary>窗口所属桌面的 GUID (公开 COM). Guid.Empty = 取不到.</summary>
    public static Guid DesktopIdOfWindow(IntPtr hwnd)
    {
        try
        {
            var mgr = GetPublicManager();
            return mgr is null ? Guid.Empty : mgr.GetWindowDesktopId(hwnd);
        }
        catch (Exception ex)
        {
            // TYPE_E_ELEMENTNOTFOUND (0x8002802B): 桌面切换动画期间查旧桌面的
            // 窗口, 或窗口正在销毁 — 常态, 不是错误 (CLR 把它映射成
            // ElementNotFoundException, 不是 COMException).
            if (ex.HResult == unchecked((int)0x8002802B))
            {
                Log.Verbose("VirtualDesktop", $"GetWindowDesktopId(0x{hwnd.ToInt64():X}) not found");
                return Guid.Empty;
            }
            Log.Exception("VirtualDesktop", ex);
            return Guid.Empty;
        }
    }

    /// <summary>窗口是否在当前桌面上 (公开 COM 的 GetWindowDesktopId 比
    /// IsWindowOnCurrentVirtualDesktop 可靠 — 上游 choyy 明确标了后者不可用).</summary>
    public static bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        var cur = CurrentGuid;
        if (cur == Guid.Empty) return true; // 取不到桌面信息 → 不拦
        var id = DesktopIdOfWindow(hwnd);
        return id == Guid.Empty || id == cur;
    }

    /// <summary>新建一个虚拟桌面. 返回新桌面 (空 = 失败). 新桌面排在最后,
    /// 不自动切过去.</summary>
    public static DesktopInfo? Create()
    {
        var mgr = GetManager();
        if (mgr is null) return null;
        try
        {
            // 建桌面在 vtable 的第 8 槽 — 正好是 23H2_2921 变体改过签名
            // 的地方, 所以必须按 build/revision 选声明.
            IVirtualDesktop? desktop = Uses2921Layout
                ? ((IVirtualDesktopManagerInternal2921)(object)mgr).CreateDesktop(IntPtr.Zero)
                : mgr.CreateDesktop();
            if (desktop is null) return null;
            var id = desktop.GetId();
            var index = GetDesktopIndex(mgr, desktop);
            Log.Info("VirtualDesktop", $"created desktop index={index} (2921={Uses2921Layout})");
            return new DesktopInfo(index, id, NameOf(id, index + 1));
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            return null;
        }
    }

    /// <summary>删除一个虚拟桌面 (内部 COM RemoveDesktop). 不能删最后一个 —
    /// Windows 自己也不允许. 删除会**关闭**该桌面上的窗口, 所以调用方要先
    /// 跟用户确认. fallback = 删完之后切过去的面 (左边那个; 删最左就退到
    /// 第二个).</summary>
    public static bool Remove(Guid desktopId)
    {
        if (desktopId == Guid.Empty) return false;
        var list = List();
        if (list.Count <= 1)
        {
            Log.Warn("VirtualDesktop", "只剩一个桌面, 不能删");
            return false;
        }
        var index = -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id == desktopId) { index = i; break; }
        if (index < 0)
        {
            Log.Warn("VirtualDesktop", $"桌面 {desktopId} 不在列表里");
            return false;
        }
        var mgr = GetManager();
        if (mgr is null) return false;
        try
        {
            var desktop = GetDesktop(mgr, index);
            if (desktop is null) return false;
            var fallback = GetDesktop(mgr, index == 0 ? 1 : index - 1);
            if (fallback is null) return false;
            mgr.RemoveDesktop(desktop, fallback);
            Log.Info("VirtualDesktop", $"removed 桌面 {index + 1} ({desktopId})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            return false;
        }
    }

    /// <summary>把窗口搬到指定桌面 (公开 COM, vtable 稳定). 失败返回 false.</summary>
    public static bool MoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
    {
        if (hwnd == IntPtr.Zero || desktopId == Guid.Empty) return false;
        try
        {
            var mgr = GetPublicManager();
            if (mgr is null) return false;
            var id = desktopId; // ref 参数 → 需要可写副本
            mgr.MoveWindowToDesktop(hwnd, ref id);
            Log.Info("VirtualDesktop", $"move window 0x{hwnd.ToInt64():X} → {desktopId}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            return false;
        }
    }

    // ---- 内部 ----

    private static IVirtualDesktopManagerInternal? GetManager()
    {
        if (_probed) return _manager;
        _probed = true;
        if (!InternalApiSupported)
        {
            Log.Warn("VirtualDesktop",
                $"build {Build} 未验证的虚拟桌面内部 API — 切换功能关闭 (不冒险调错 vtable)");
            return null;
        }
        try
        {
            var shell = (IServiceProvider10)Activator.CreateInstance(
                Type.GetTypeFromCLSID(CLSID_ImmersiveShell)!);
            var clsid = CLSID_VirtualDesktopManagerInternal;
            var iid = IID_IVirtualDesktopManagerInternal;
            var mgr = (IVirtualDesktopManagerInternal)shell.QueryService(
                ref clsid, ref iid);
            var n = mgr.GetCount();
            if (n is < 1 or > 64) throw new InvalidOperationException($"桌面数异常: {n}");
            _manager = mgr;
            Log.Info("VirtualDesktop", $"COM ok, 桌面数={n}, build={Build}");
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            _manager = null;
        }
        return _manager;
    }

    private static IVirtualDesktopManager? GetPublicManager()
    {
        if (_publicManager is not null) return _publicManager;
        try
        {
            _publicManager = (IVirtualDesktopManager)Activator.CreateInstance(
                Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager)!);
        }
        catch (Exception ex)
        {
            Log.Exception("VirtualDesktop", ex);
            _publicManager = null;
        }
        return _publicManager;
    }

    private static IVirtualDesktop? GetDesktop(IVirtualDesktopManagerInternal mgr, int index)
    {
        mgr.GetDesktops(out var desktops);
        try
        {
            var iid = IID_IVirtualDesktop;
            desktops.GetAt(index, ref iid, out var obj);
            return obj as IVirtualDesktop;
        }
        finally
        {
            Marshal.ReleaseComObject(desktops);
        }
    }

    private static int GetDesktopIndex(IVirtualDesktopManagerInternal mgr, IVirtualDesktop target)
    {
        var id = target.GetId();
        mgr.GetDesktops(out var desktops);
        try
        {
            desktops.GetCount(out var count);
            var iid = IID_IVirtualDesktop;
            for (var i = 0; i < count; i++)
            {
                desktops.GetAt(i, ref iid, out var obj);
                if (obj is IVirtualDesktop d && d.GetId() == id) return i;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(desktops);
        }
        return -1;
    }
}
