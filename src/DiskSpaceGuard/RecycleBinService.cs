using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DiskSpaceGuard.Services;

/// <summary>回收站：只能按路径操作；失败必须如实返回，禁止降级为永久删除。</summary>
public static class RecycleBinService
{
    private const uint FofAllowUndo = 0x0040;
    private const uint FofSilent = 0x0004;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofWantNukeWarning = 0x4000;

    private const uint ClsCtxAll = 0x17;
    private const uint CoInitMultithreaded = 0x0;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");

    /// <summary>把单个文件/目录移入回收站。返回 false 时不重试、不改用永久删除。</summary>
    public static bool TryMoveToRecycleBin(string path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }

        // 网络映射盘没有回收站：提前交回用户，不交给 Shell 猜测其行为。
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
            {
                error = "网络盘不支持回收站，已取消（未删除任何对象）";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // 卷类型无法判定时继续交给 Shell，由返回码决定成败。
        }

        // IFileOperation 是 COM 接口：调用线程必须先初始化 COM。
        var comInitialized = false;
        var hrInit = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (hrInit >= 0) comInitialized = true;
        else if (hrInit != RpcChangedMode)
        {
            error = $"COM 初始化失败（0x{hrInit:X8}）";
            return false;
        }

        try
        {
            var clsid = ClsidFileOperation;
            var iid = IidFileOperation;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var fileOp);
            if (hr < 0 || fileOp is null)
            {
                error = $"无法创建文件操作对象（0x{hr:X8}）";
                return false;
            }

            // 关键安全点：不允许 NOCONFIRMATION / NOSILENT 之外的静默永久删除。
            // FOF_ALLOWUNDO 让对象优先进入回收站；无法回收时由 FOF_WANTNUKEWARNING 触发警告。
            hr = fileOp.SetOperationFlags(FofAllowUndo | FofSilent | FofNoErrorUi | FofWantNukeWarning);
            if (hr < 0)
            {
                error = $"设置回收站参数失败（0x{hr:X8}）";
                return false;
            }

            var shellItemId = typeof(IShellItem).GUID;
            hr = SHCreateItemFromParsingName(path, IntPtr.Zero, shellItemId, out var shellItem);
            if (hr < 0 || shellItem is null)
            {
                error = $"无法创建 Shell 项（0x{hr:X8}）";
                return false;
            }

            hr = fileOp.DeleteItem(shellItem, null);
            if (hr < 0)
            {
                error = $"回收站操作排队失败（0x{hr:X8}）";
                return false;
            }

            hr = fileOp.PerformOperations();
            if (hr < 0)
            {
                error = $"回收站操作失败（0x{hr:X8}）";
                return false;
            }

            if (fileOp.GetAnyOperationsAborted(out var aborted) >= 0 && aborted)
            {
                error = "用户取消了回收站操作";
                return false;
            }

            // 目录必须单独判断：File.Exists 对目录始终返回 false。
            if (File.Exists(path) || Directory.Exists(path))
            {
                error = "回收站操作返回成功，但对象仍在原位置";
                return false;
            }

            return true;
        }
        catch (COMException ex)
        {
            error = $"回收站操作失败：{ex.Message}";
            return false;
        }
        catch (InvalidCastException ex)
        {
            error = $"回收站操作失败：{ex.Message}";
            return false;
        }
        finally
        {
            if (comInitialized) CoUninitialize();
        }
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] object pfops, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object popd);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object pproparray);
        [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem psiItem);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object punkItems);
        [PreserveSig] int RenameItem(IShellItem psiItem,
            [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
            [MarshalAs(UnmanagedType.Interface)] object pfopsItem);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.Interface)] object pUnkItems,
            [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        [PreserveSig] int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
            [MarshalAs(UnmanagedType.Interface)] object pfopsItem);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.Interface)] object punkItems,
            IShellItem psiDestinationFolder);
        [PreserveSig] int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName,
            [MarshalAs(UnmanagedType.Interface)] object pfopsItem);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.Interface)] object punkItems,
            IShellItem psiDestinationFolder);
        [PreserveSig] int DeleteItem(IShellItem psiItem,
            [MarshalAs(UnmanagedType.Interface)] object? pfopsItem);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.Interface)] object punkItems);
        [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName,
            [MarshalAs(UnmanagedType.Interface)] object pfopsItem);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IFileOperation ppv);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
