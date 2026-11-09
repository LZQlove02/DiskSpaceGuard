using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>五级保护模型（§3.5）。只增加保护，不减少内置规则。</summary>
public sealed class ProtectionEvaluator
{
    // 每个文件都调用 Environment.GetFolderPath 会产生数 GB 级 Gen0 分配；
    // 系统目录在一次进程内不变，静态缓存并预先规范化/小写化。
    private static readonly string[] HardRoots = BuildHardRoots();
    private static readonly string[] SensitiveRoots = BuildSensitiveRoots();

    private readonly List<string> _customProtectedRoots = new();

    public IReadOnlyList<string> CustomProtectedRoots => _customProtectedRoots;

    private static string[] BuildHardRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            // 规格 §3.5：应用自身数据目录同样是 L0。
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskSpaceGuard")
        };

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Normalize(root).TrimEnd(Path.DirectorySeparatorChar))
            .Where(root => root.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildSensitiveRoots()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        // 规格 §3.5：Windows.old / $Windows.~BT / $Windows.~WS 归 L2（强确认）。
        var systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrEmpty(systemDrive))
        {
            roots.Add(Path.Combine(systemDrive, "Windows.old"));
            roots.Add(Path.Combine(systemDrive, "$Windows.~BT"));
            roots.Add(Path.Combine(systemDrive, "$Windows.~WS"));
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Normalize(root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant())
            .Where(root => root.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void AddCustomRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);
        // 空串会让 IsUnder 对所有路径误判为"在保护根下"，不可进入列表。
        if (normalized.Length == 0) return;
        if (!_customProtectedRoots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            _customProtectedRoots.Add(normalized);
    }

    /// <summary>用设置界面保存的列表整体替换 L4 保护目录（只增不减：移除也需显式操作）。</summary>
    public void SetCustomRoots(IEnumerable<string> roots)
    {
        _customProtectedRoots.Clear();
        foreach (var root in roots) AddCustomRoot(root);
    }

    public ProtectionLevel Evaluate(string fullPath, FileAttributes attributes, out string reasonKey)
    {
        var path = Normalize(fullPath);
        // 无法规范化 = 无法判定保护边界，一律按 L0 硬禁止（fail-closed）。
        if (path.Length == 0)
        {
            reasonKey = "protected.l0";
            return ProtectionLevel.L0;
        }

        var lower = path.ToLowerInvariant();

        // 规格 §3.5：所有卷根与每个挂载点根都是 L0（目录属性才需要做这次 API 调用）。
        if ((attributes & FileAttributes.Directory) != 0 && IsVolumeMountPointRoot(path))
        {
            reasonKey = "protected.l0";
            return ProtectionLevel.L0;
        }

        if (IsHardBlocked(lower, path))
        {
            reasonKey = "protected.l0";
            return ProtectionLevel.L0;
        }

        if (_customProtectedRoots.Any(root => IsUnder(path, root)))
        {
            reasonKey = "protected.custom";
            return ProtectionLevel.L4;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            reasonKey = "protected.reparse";
            return ProtectionLevel.L1;
        }

        if (IsSensitiveFolder(lower))
        {
            reasonKey = "protected.strong-confirm";
            return ProtectionLevel.L2;
        }

        reasonKey = "protected.normal";
        return ProtectionLevel.L3;
    }

    /// <summary>
    /// 词法判定 + 最终落点判定，取更严格的一级。
    /// 字面路径（如经 junction 祖先到达的临时目录）可能指向 L0 系统目录，
    /// 只按字符串比较会漏判；规格 §3.5 判定表第 2 行要求禁止穿透。
    /// 最终落点无法用本地盘符表示（UNC / 卷 GUID）时退回词法判定：
    /// 这类目标不在本地保护根的范围内，不应因此阻断映射盘等正常删除。
    /// </summary>
    public ProtectionLevel EvaluateWithFinalPath(string fullPath, FileAttributes attributes,
        string finalPath, out string reasonKey)
    {
        var lexical = Evaluate(fullPath, attributes, out var lexicalReason);
        var resolved = string.IsNullOrEmpty(finalPath) ? string.Empty : Normalize(finalPath);
        if (resolved.Length == 0)
        {
            reasonKey = lexicalReason;
            return lexical;
        }

        var final = Evaluate(resolved, attributes, out var finalReason);
        if (Severity(final) < Severity(lexical))
        {
            reasonKey = finalReason;
            return final;
        }

        reasonKey = lexicalReason;
        return lexical;
    }

    /// <summary>冲突裁决顺序：L0 &gt; L1 &gt; L4 &gt; L2 &gt; L3（规格 §3.5）。</summary>
    private static int Severity(ProtectionLevel level) => level switch
    {
        ProtectionLevel.L0 => 0,
        ProtectionLevel.L1 => 1,
        ProtectionLevel.L4 => 2,
        ProtectionLevel.L2 => 3,
        _ => 4
    };

    /// <summary>目录是否为卷挂载点根（含盘符根）。非挂载点返回 false。</summary>
    private static bool IsVolumeMountPointRoot(string normalizedPath)
    {
        try
        {
            var buffer = new StringBuilder(64);
            return GetVolumeNameForVolumeMountPoint(
                normalizedPath + Path.DirectorySeparatorChar, buffer, buffer.Capacity);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException)
        {
            return false;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);

    private static bool IsHardBlocked(string lower, string path)
    {
        foreach (var root in HardRoots)
        {
            if (IsUnderNormalized(path, root)) return true;
        }

        // 卷根
        var volumeRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(volumeRoot) && string.Equals(path.TrimEnd('\\'), volumeRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return true;

        var name = Path.GetFileName(lower);
        if (name is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" or "bootmgr" or "ntldr")
            return true;

        // 按路径段比较：裸 Contains 会把 $Recycle.BinBackup 之类的子串误判为系统目录。
        foreach (var segment in lower.Split(Path.DirectorySeparatorChar))
        {
            if (segment is "$recycle.bin" or "system volume information") return true;
        }

        var exe = Environment.ProcessPath;
        var exeDir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        if (!string.IsNullOrEmpty(exeDir) && IsUnder(path, exeDir))
            return true;

        return false;
    }

    private static bool IsSensitiveFolder(string lower)
    {
        foreach (var root in SensitiveRoots)
        {
            if (lower.StartsWith(root + "\\", StringComparison.Ordinal) ||
                lower.Equals(root, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>已规范化的候选与根做边界比较，避免每文件重复 GetFullPath。</summary>
    private static bool IsUnderNormalized(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>路径边界比较：禁止裸 StartsWith（避免 C:\WindowsOld 命中 C:\Windows）。</summary>
    public static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;
        var c = Normalize(candidate);
        // 候选无法判定时保守视为"可能位于受保护根下"，避免 fail-open 绕过。
        if (c.Length == 0) return true;
        var r = Normalize(root).TrimEnd(Path.DirectorySeparatorChar);
        if (r.Length == 0) return false;
        return IsUnderNormalized(c, r);
    }

    /// <summary>
    /// 规范化路径用于保护判定。返回空串表示「不可信 / 无法判定」，调用方必须按 fail-closed 处理。
    /// 关键：Path.GetFullPath 会保留 \\?\ 与 \\.\ 设备前缀，必须先剥离，否则路径边界比较失效。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var candidate = path;
        if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
            candidate = candidate[4..];

        // UNC / 设备命名空间：无法据本地保护根判定，一律拒绝。
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
            return string.Empty;

        // 只接受本地盘符绝对路径：卷 GUID 路径与相对路径无法可靠判定保护边界。
        if (candidate.Length < 2 || !char.IsLetter(candidate[0]) || candidate[1] != ':')
            return string.Empty;

        // 尾随空格 / 点 / 备用数据流不参与正常路径语义，直接拒绝而不是猜测。
        if (candidate != candidate.TrimEnd(' ', '.')) return string.Empty;
        if (candidate.IndexOf(':', 2) >= 0) return string.Empty;

        try
        {
            return Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
