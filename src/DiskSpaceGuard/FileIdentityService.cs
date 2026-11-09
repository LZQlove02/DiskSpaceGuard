using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    private const uint FileNameNormalized = 0x00000000;
    private const uint VolumeNameDos = 0x00000000;

    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadOnly = 0x10;

    // 2 秒容差：NTFS 的 FILETIME 精确到 100ns，FAT/exFAT 只有 2 秒粒度。
    private const long LastWriteToleranceFileTime = 2L * 10_000_000L;

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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        uint dwBufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileTime(
        SafeFileHandle hFile,
        out long lpCreationTime,
        out long lpLastAccessTime,
        out long lpLastWriteTime);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInfoEx lpFileInformation,
        uint dwBufferSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

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

    /// <summary>
    /// 跟随重解析点打开，读取身份并解析最终落点。
    /// 用于删除计划：字面路径可能经由 junction/symlink 祖先落到受保护目录，
    /// 只比对字面路径会漏判（规格 §3.5 判定表第 2 行）。
    /// </summary>
    public static bool TryReadIdentityWithFinalPath(string path, out FileIdentity identity,
        out string finalPath, out string error)
    {
        identity = default;
        finalPath = string.Empty;
        error = string.Empty;

        using var handle = CreateFileW(path, GenericRead, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        if (!TryReadIdentity(handle, out identity, out error)) return false;

        finalPath = TryGetFinalPath(handle);
        return true;
    }

    /// <summary>读取句柄的最终落点；失败或无法规范化时返回空串（调用方按 fail-closed 处理）。</summary>
    private static string TryGetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity,
            VolumeNameDos | FileNameNormalized);
        if (length == 0) return string.Empty;

        // 返回值不小于容量表示缓冲区不够，按所需长度重试一次。
        if (length >= (uint)buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity,
                VolumeNameDos | FileNameNormalized);
            if (length == 0 || length >= (uint)buffer.Capacity) return string.Empty;
        }

        // VOLUME_NAME_DOS 返回 \\?\C:\...，交给 Normalize 剥离设备前缀并统一大小写语义。
        return ProtectionEvaluator.Normalize(buffer.ToString());
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
    /// 打开句柄 → 校验身份、大小与时间戳 → 通过同一句柄删除。
    /// 任一快照项不一致时返回 Changed，不执行删除。
    /// </summary>
    public static (VerifyOutcome Outcome, string Message) VerifyAndDelete(
        string path, long expectedSize, ulong expectedVolumeSerial, string expectedFileId,
        bool ignoreReadOnly, bool isDirectory = false, DateTime expectedLastWriteUtc = default)
    {
        using var handle = CreateFileW(path, DeleteAccess | GenericRead, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            var message = new Win32Exception(code).Message;
            if (code is 2 or 3) return (VerifyOutcome.Missing, message);
            if (IsVolumeUnavailable(code)) return (VerifyOutcome.VolumeUnavailable, message);
            return (VerifyOutcome.Unverifiable, message);
        }

        if (!TryReadIdentity(handle, out var identity, out var identityError))
            return (VerifyOutcome.Unverifiable, identityError);

        if (identity.VolumeSerial != expectedVolumeSerial ||
            !string.Equals(identity.ToPlanString(), expectedFileId, StringComparison.OrdinalIgnoreCase))
            return (VerifyOutcome.Changed, "对象身份已变化，已跳过");

        // 时间戳必须从句柄读取，不再按路径二次 stat（§3.2 检查表 / T17）。
        if (expectedLastWriteUtc != default && !isDirectory)
        {
            if (!GetFileTime(handle, out _, out _, out var lastWriteFileTime))
                return (VerifyOutcome.Unverifiable, "无法从句柄读取时间戳，已跳过");

            var expectedFileTime = expectedLastWriteUtc.ToFileTimeUtc();
            if (Math.Abs(lastWriteFileTime - expectedFileTime) > LastWriteToleranceFileTime)
                return (VerifyOutcome.Changed, "时间戳与计划不一致，已跳过");
        }

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
        /// <summary>设备断开 / 网络路径不可用；调用方应中止整份计划。</summary>
        VolumeUnavailable,
        Unverifiable
    }

    /// <summary>ERROR_NOT_READY / BAD_NETPATH / NETNAME_DELETED / DEVICE_NOT_CONNECTED 等。</summary>
    private static bool IsVolumeUnavailable(int code)
        => code is 21 or 53 or 64 or 67 or 1166 or 1167 or 1231;
}
