using System.IO;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>五级保护模型（§3.5）。只增加保护，不减少内置规则。</summary>
public sealed class ProtectionEvaluator
{
    private readonly List<string> _customProtectedRoots = new();

    public IReadOnlyList<string> CustomProtectedRoots => _customProtectedRoots;

    public void AddCustomRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);
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
        var lower = path.ToLowerInvariant();

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

    private static bool IsHardBlocked(string lower, string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        foreach (var root in new[] { windows, system, systemX86 })
        {
            if (!string.IsNullOrEmpty(root) && IsUnder(path, root))
                return true;
        }

        // 卷根
        var volumeRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(volumeRoot) && string.Equals(path.TrimEnd('\\'), volumeRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return true;

        var name = Path.GetFileName(lower);
        if (name is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" or "bootmgr" or "ntldr")
            return true;

        if (lower.Contains("\\$recycle.bin", StringComparison.Ordinal) ||
            lower.Contains("\\system volume information", StringComparison.Ordinal))
            return true;

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) && IsUnder(path, Path.GetDirectoryName(exe)!))
            return true;

        return false;
    }

    private static bool IsSensitiveFolder(string lower)
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var normalized = root.TrimEnd('\\').ToLowerInvariant();
            if (lower.StartsWith(normalized + "\\", StringComparison.Ordinal) ||
                lower.Equals(normalized, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>路径边界比较：禁止裸 StartsWith（避免 C:\WindowsOld 命中 C:\Windows）。</summary>
    public static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root)) return false;
        var c = Normalize(candidate);
        var r = Normalize(root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
