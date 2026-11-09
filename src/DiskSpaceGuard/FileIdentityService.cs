using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceGuard.Services;

public readonly record struct FileIdentity(ulong VolumeSerial, ulong FileIdHigh, ulong FileIdLow)
{
    public bool IsValid => VolumeSerial != 0 || FileIdHigh != 0 || FileIdLow != 0;

    public string ToPlanString() => $"{VolumeSerial:X16}{FileIdHigh:X16}{FileIdLow:X16}";
}

/// <summary>
/// 身份读取与句柄绑定删除：CreateFileW（含 DELETE）→ FileIdInfo 校验 → SetFileInformationByHandle。
/// 降低路径替换竞态；不宣称消除 TOCTOU。
/// </summary>
public static class FileIdentityService
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const int FileIdInfoClass = 18;
    private const int FileDispositionInfoExClass = 21;

    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    // 原生 FILE_ID_INFO：DWORD VolumeSerialNumber + FILE_ID_128（16 字节）= 20 字节。
    // 实测 GetFileInformationByHandleEx(FileIdInfo) 拒绝 20–23 字节缓冲（ERROR_BAD_LENGTH），
    // 要求 ≥24 字节但只写入前 20 字节：Pack=4 保证字段偏移与原生一致，末尾 4 字节为填充。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileIdInfo
    {
        public uint VolumeSerialNumber;
        public ulong FileIdLow;           // FILE_ID_128 低 8 字节（偏移 4）
        public ulong FileIdHigh;          // FILE_ID_128 高 8 字节（偏移 12）
        private readonly uint _padding;   // 偏移 20：满足 API 的 24 字节缓冲下限，不读取
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInfoEx lpFileInformation,
        uint dwBufferSize);

    public static bool TryReadIdentity(string path, out FileIdentity identity, out string error)
    {
        identity = default;
        error = string.Empty;

        using var handle = CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        return TryReadIdentity(handle, out identity, out error);
    }

    private static bool TryReadIdentity(SafeFileHandle handle, out FileIdentity identity, out string error)
    {
        identity = default;
        error = string.Empty;

        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        identity = new FileIdentity(info.VolumeSerialNumber, info.FileIdHigh, info.FileIdLow);
        return identity.IsValid;
    }

    /// <summary>
    /// 打开句柄 → 校验身份与快照 → 通过同一句柄删除。
    /// 身份不一致时返回 Changed，不执行删除。
    /// </summary>
    public static (VerifyOutcome Outcome, string Message) VerifyAndDelete(
        string path, long expectedSize, ulong expectedVolumeSerial, string expectedFileId,
        bool ignoreReadOnly, bool isDirectory = false)
    {
        using var handle = CreateFileW(path, DeleteAccess | GenericRead, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            var message = new Win32Exception(code).Message;
            if (code is 2 or 3) return (VerifyOutcome.Missing, message);
            return (VerifyOutcome.Unverifiable, message);
        }

        if (!TryReadIdentity(handle, out var identity, out var identityError))
            return (VerifyOutcome.Unverifiable, identityError);

        if (identity.VolumeSerial != expectedVolumeSerial ||
            !string.Equals(identity.ToPlanString(), expectedFileId, StringComparison.OrdinalIgnoreCase))
            return (VerifyOutcome.Changed, "对象身份已变化，已跳过");

        try
        {
            // 目录的大小不是可比较的快照字段；只对文件做大小复核。
            var info = new FileInfo(path);
            if (!isDirectory && info.Exists)
            {
                if (info.Length != expectedSize)
                return (VerifyOutcome.Changed, "大小与计划不一致，已跳过");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var disposition = new FileDispositionInfoEx
        {
            Flags = DispositionDelete | DispositionPosixSemantics |
                    (ignoreReadOnly ? DispositionIgnoreReadOnly : 0)
        };

        if (!SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfoEx>()))
        {
            var code = Marshal.GetLastWin32Error();
            return (VerifyOutcome.Unverifiable, $"删除失败：{new Win32Exception(code).Message}");
        }

        return (VerifyOutcome.Matched, string.Empty);
    }

    public enum VerifyOutcome
    {
        Matched,
        Changed,
        Missing,
        Unverifiable
    }
}
