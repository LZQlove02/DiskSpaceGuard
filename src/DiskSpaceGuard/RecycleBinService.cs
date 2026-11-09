using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DiskSpaceGuard.Services;

/// <summary>回收站：只能按路径操作；失败必须如实返回，禁止降级为永久删除。</summary>
public static class RecycleBinService
{
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>把单个文件移入回收站。返回 false 时不重试、不改用永久删除。</summary>
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

        // 关键安全点：绝不能带 FOF_NOCONFIRMATION / FOF_SILENT。
        // 前者会对「无法放入回收站，是否永久删除？」自动回答「全是」，
        // 导致 Shell 静默永久删除却返回成功并让台账误记为「可还原」。
        // 去掉后由用户显式确认，或由 Shell 返回非零错误码交回用户。
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FoDelete,
            pFrom = path + '\0' + '\0',
            fFlags = FofAllowUndo | FofNoErrorUi
        };

        try
        {
            var code = SHFileOperation(ref operation);
            if (operation.fAnyOperationsAborted != 0)
            {
                error = "用户取消了回收站操作";
                return false;
            }

            if (code != 0)
            {
                error = code switch
                {
                    2 => "回收站操作被取消",
                    0x71 => "回收站不可用（可能位于网络盘或已禁用）",
                    0x78 => "回收站配额不足或卷不支持回收站",
                    _ => $"回收站操作失败（0x{code:X}）"
                };
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
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }
}
