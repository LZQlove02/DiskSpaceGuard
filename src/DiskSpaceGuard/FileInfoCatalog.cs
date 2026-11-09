using System.IO;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard.Models;

public enum FileSafety
{
    Blocked,
    Keep,
    ConfirmBeforeDelete,
    Cleanable,
    Personal,
    Unknown
}

public sealed record FileVerdict(FileSafety Level, string Label, string Description);

/// <summary>离线扩展名说明库与保护判定；联网搜索只发送文件名。</summary>
public static class FileInfoCatalog
{
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys", "dll", "exe", "msi", "cab", "wim", "ini", "log", "tmp", "mp4", "mkv", "mov",
        "mp3", "psd", "docx", "xlsx", "pptx", "pdf", "json", "db", "sqlite", "iso", "bin", "dat"
    };

    private static readonly HashSet<string> KeepExtensions = new(StringComparer.OrdinalIgnoreCase) { "sys", "dll", "wim" };
    private static readonly HashSet<string> ConfirmExtensions = new(StringComparer.OrdinalIgnoreCase) { "exe", "msi" };
    private static readonly HashSet<string> CleanableExtensions = new(StringComparer.OrdinalIgnoreCase) { "tmp", "log", "cab" };
    private static readonly HashSet<string> PersonalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "mov", "mp3", "psd", "docx", "xlsx", "pptx", "pdf", "iso"
    };

    public static string ExtensionOf(string name)
    {
        var index = name.LastIndexOf('.');
        return index > 0 && index < name.Length - 1 ? name[(index + 1)..].ToLowerInvariant() : string.Empty;
    }

    public static string Describe(string name)
    {
        var ext = ExtensionOf(name);
        return KnownExtensions.Contains(ext)
            ? Text("Knowledge." + ext)
            : Text("FileInfo.NoKnowledge");
    }

    private static string Text(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);

    public static FileVerdict Evaluate(string fullPath, string name)
    {
        var lowerPath = fullPath.ToLowerInvariant();
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
        var fileName = name.ToLowerInvariant();

        if (IsUnder(lowerPath, windowsRoot) ||
            fileName is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" ||
            lowerPath.Contains("\\$recycle.bin", StringComparison.Ordinal))
        {
            return new FileVerdict(FileSafety.Blocked, Text("Verdict.L0"), Text("Verdict.L0Desc"));
        }

        var ext = ExtensionOf(name);
        if (KeepExtensions.Contains(ext))
            return new FileVerdict(FileSafety.Keep, Text("Verdict.Keep"), Text("Verdict.KeepDesc"));
        if (ConfirmExtensions.Contains(ext))
            return new FileVerdict(FileSafety.ConfirmBeforeDelete, Text("Verdict.Confirm"), Text("Verdict.ConfirmDesc"));
        if (CleanableExtensions.Contains(ext))
            return new FileVerdict(FileSafety.Cleanable, Text("Verdict.Cleanable"), Text("Verdict.CleanableDesc"));
        if (PersonalExtensions.Contains(ext))
            return new FileVerdict(FileSafety.Personal, Text("Verdict.Personal"), Text("Verdict.PersonalDesc"));

        return new FileVerdict(FileSafety.Unknown, Text("Verdict.Unknown"), Text("Verdict.UnknownDesc"));
    }

    public static string BuildQuery(string name)
        => string.Format(Text("Verdict.Query"), name);

    /// <summary>路径边界比较：C:\WindowsOld 不得命中 C:\Windows。</summary>
    private static bool IsUnder(string candidate, string root)
    {
        var r = root.TrimEnd('\\');
        if (string.Equals(candidate, r, StringComparison.Ordinal)) return true;
        return candidate.StartsWith(r + "\\", StringComparison.Ordinal);
    }
}
