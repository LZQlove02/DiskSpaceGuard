using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskSpaceGuard.Models;

public enum AppSource
{
    Registry64,
    Registry32,
    RegistryHKCU,
    Appx
}

public enum AppKind
{
    Msi,
    Exe,
    Msix
}

/// <summary>
/// §15 已安装应用。只描述检测到的客观字段；是否可卸载由 AppCatalog 统一判定。
/// 不可卸载时 BlockReasonKey 非空；默认隐藏时 HiddenReasonKey 非空。
/// </summary>
public sealed record InstalledApp
{
    public string AppKey { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string DisplayVersion { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public DateTime? InstallDate { get; init; }
    public long? EstimatedSizeBytes { get; init; }
    public string InstallLocation { get; init; } = string.Empty;
    public AppSource Source { get; init; }
    public AppKind Kind { get; init; }

    /// <summary>原始卸载命令，只读展示；执行时也只使用它（或 QuietUninstallCommand）。</summary>
    public string UninstallCommand { get; init; } = string.Empty;
    public string QuietUninstallCommand { get; init; } = string.Empty;
    public string PackageFullName { get; init; } = string.Empty;

    public bool HasQuietMode => !string.IsNullOrWhiteSpace(QuietUninstallCommand);
    public bool RequiresElevation { get; init; }
    public bool IsDependent { get; init; }
    public bool IsDeveloperTool { get; init; }
    public bool IsSystemComponent { get; init; }
    public bool IsPreinstalled { get; init; }
    public bool IsUninstallable { get; init; }
    public string BlockReasonKey { get; init; } = string.Empty;
    public string HiddenReasonKey { get; init; } = string.Empty;

    public bool HiddenByDefault => !string.IsNullOrEmpty(HiddenReasonKey);
}

public sealed record InstalledAppSnapshot(
    IReadOnlyList<InstalledApp> Apps,
    int VisibleCount,
    int HiddenCount,
    string? AppxError,
    DateTime ScannedUtc);

/// <summary>
/// 应用条目的字段映射、去重键与安全判定。所有判定都是纯函数，便于自动化测试。
/// 判定原则：无法确认安全时不给卸载入口。
/// </summary>
public static class AppCatalog
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly string[] DeveloperTools =
    {
        "visual studio", "vs code", "visual studio code", "jetbrains", "intellij",
        "pycharm", "webstorm", "android studio", "unity", "unreal engine",
        "docker desktop", "node.js", "python", "git", "github desktop", "postman", "qt"
    };

    private static readonly Regex GuidPattern = new(
        @"\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?",
        RegexOptions.Compiled);

    private static readonly Regex AppxPackageNamePattern = new(
        @"^[A-Za-z0-9._~-]+$", RegexOptions.Compiled);

    /// <summary>解析注册表 Uninstall 子键：产品码、去重键与 MSI/EXE 类型。</summary>
    public static (string AppKey, AppKind Kind, string? ProductCode) BuildIdentity(
        string subKeyName, bool windowsInstaller, string uninstallCommand,
        string displayName, string publisher, string version)
    {
        var productCode = NormalizeProductCode(subKeyName) ??
                          (LooksLikeMsiCommand(uninstallCommand) ? ProductCodeFromCommand(uninstallCommand) : null);
        var isMsi = windowsInstaller || productCode is not null || LooksLikeMsiCommand(uninstallCommand);

        if (isMsi)
        {
            return (productCode is null
                    ? "exe:" + HashKey(displayName, publisher, version)
                    : "msi:" + productCode.ToUpperInvariant(),
                AppKind.Msi, productCode);
        }

        return ("exe:" + HashKey(displayName, publisher, version), AppKind.Exe, null);
    }

    public static string BuildAppxCommand(string packageFullName)
        => string.IsNullOrWhiteSpace(packageFullName) ||
           !AppxPackageNamePattern.IsMatch(packageFullName)
            ? string.Empty
            : $"powershell.exe -NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{packageFullName}'\"";

    public static bool IsValidPackageFullName(string packageFullName)
        => !string.IsNullOrWhiteSpace(packageFullName) && AppxPackageNamePattern.IsMatch(packageFullName);

    public static string? NormalizeProductCode(string keyName)
    {
        var text = (keyName ?? string.Empty).Trim().Trim('{', '}');
        return Guid.TryParse(text, out var guid) ? guid.ToString("D").ToUpperInvariant() : null;
    }

    public static string? ProductCodeFromCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = GuidPattern.Match(command);
        return match.Success ? Guid.Parse(match.Groups[1].Value).ToString("D").ToUpperInvariant() : null;
    }

    private static bool LooksLikeMsiCommand(string command)
        => !string.IsNullOrWhiteSpace(command) &&
           (command.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("msiinstall", StringComparison.OrdinalIgnoreCase));

    public static string HashKey(string displayName, string publisher, string version)
    {
        var text = Normalize(displayName) + "|" + Normalize(publisher) + "|" + Normalize(version);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    public static string Normalize(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Regex.Replace(trimmed, @"\s+", " ");
    }

    /// <summary>InstallDate 形如 20240131；解析失败返回 null，不填当天。</summary>
    public static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>注册表 EstimatedSize 的单位是 KB，只做换算；取不到返回 null。</summary>
    public static long? EstimatedSizeToBytes(object? value)
    {
        if (value is null) return null;
        long kb;
        switch (value)
        {
            case int i: kb = i; break;
            case long l: kb = l; break;
            case string s when long.TryParse(s, out var parsed): kb = parsed; break;
            default: return null;
        }
        if (kb <= 0) return null;
        return kb > long.MaxValue / 1024 ? long.MaxValue : kb * 1024;
    }

    /// <summary>Appx 的 Publisher 是 DN，取 CN 便于阅读；其他发布者原样返回。</summary>
    public static string SimplifyPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;
        var text = publisher.Trim();
        if (!text.Contains("CN=", StringComparison.OrdinalIgnoreCase)) return text;

        var start = text.IndexOf("CN=", StringComparison.OrdinalIgnoreCase) + 3;
        var end = text.IndexOf(',', start);
        var value = end < 0 ? text[start..] : text[start..end];
        value = value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? text : value;
    }

    public static bool IsUnderWindows(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(WindowsDir)) return false;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var windows = Path.GetFullPath(WindowsDir).TrimEnd(Path.DirectorySeparatorChar);
            return full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>硬禁止卸载的原因；null 表示允许（仍需有官方入口）。</summary>
    public static string? BlockReason(InstalledApp app)
    {
        var name = app.DisplayName ?? string.Empty;

        if (app.IsSystemComponent) return "Apps.Blocked.SystemComponent";
        if (IsSecurityComponent(name)) return "Apps.Blocked.Security";
        if (IsSystemRuntime(name)) return "Apps.Blocked.Runtime";
        if (IsSystemSideVisualCpp(name, app.InstallLocation)) return "Apps.Blocked.Runtime";
        if (IsSystemDriver(name, app.Publisher, app.InstallLocation)) return "Apps.Blocked.Driver";
        if (IsWindowsOptionalFeature(name)) return "Apps.Blocked.Feature";

        // 默认隐藏的父项/补丁即使被显示，也不提供单独卸载入口。
        if (app.HiddenReasonKey == "Apps.Hidden.Parent") return "Apps.Blocked.Parent";
        if (app.HiddenReasonKey == "Apps.Hidden.Update") return "Apps.Blocked.Update";
        if (app.HiddenReasonKey == "Apps.Hidden.Framework") return "Apps.Blocked.Framework";
        if (app.HiddenReasonKey == "Apps.Hidden.Resource") return "Apps.Blocked.Resource";

        if (string.IsNullOrWhiteSpace(app.UninstallCommand)) return "Apps.Blocked.NoEntry";
        return null;
    }

    public static InstalledApp ApplyPolicy(InstalledApp app)
    {
        var block = app.BlockReasonKey;
        if (string.IsNullOrEmpty(block)) block = BlockReason(app) ?? string.Empty;

        return app with
        {
            IsDependent = app.IsDependent || IsDependentComponent(app.DisplayName, app.Publisher),
            IsDeveloperTool = app.IsDeveloperTool || IsDeveloperToolApp(app.DisplayName),
            BlockReasonKey = block,
            IsUninstallable = string.IsNullOrEmpty(block) &&
                              !string.IsNullOrWhiteSpace(app.UninstallCommand)
        };
    }

    public static bool IsDependentComponent(string name, string publisher)
    {
        var text = (name + " " + publisher).ToLowerInvariant();
        if (text.Contains("webview2")) return true;
        if (text.Contains("visual c++") || text.Contains("vc++ redistributable")) return true;
        if (text.Contains("java") && (text.Contains("runtime") || text.Contains("jre"))) return true;
        if (text.Contains(".net") &&
            (text.Contains("runtime") || text.Contains("framework") || text.Contains("host")))
            return true;
        return false;
    }

    public static bool IsDeveloperToolApp(string name)
    {
        var text = (name ?? string.Empty).ToLowerInvariant();
        return DeveloperTools.Any(t => text.Contains(t, StringComparison.Ordinal));
    }

    private static bool IsSecurityComponent(string name)
    {
        var text = (name ?? string.Empty).ToLowerInvariant();
        return text.Contains("windows defender") || text.Contains("microsoft defender") ||
               text.Contains("security essentials") || text.Contains("windows security") ||
               text.Contains("安全中心") || text.Contains("windows 安全");
    }

    private static bool IsSystemRuntime(string name)
    {
        var text = (name ?? string.Empty).ToLowerInvariant();
        return text.Contains(".net runtime") || text.Contains("windows desktop runtime") ||
               text.Contains("aspnet core runtime") || text.Contains("microsoft .net framework") ||
               text.Contains("microsoft .net host") || text.Contains(".net native runtime");
    }

    /// <summary>系统侧 VC++ 运行时位于 Windows 目录；普通 VC++ 发行包不在此列。</summary>
    private static bool IsSystemSideVisualCpp(string name, string location)
        => (name ?? string.Empty).Contains("visual c++", StringComparison.OrdinalIgnoreCase) &&
           IsUnderWindows(location);

    private static bool IsSystemDriver(string name, string publisher, string location)
    {
        var text = (name + " " + publisher).ToLowerInvariant();
        var driverWord = text.Contains("driver") || text.Contains("驱动");
        if (!driverWord) return false;

        var vendor = text.Contains("graphics") || text.Contains("display") ||
                     text.Contains("chipset") || text.Contains("nvidia") ||
                     text.Contains("amd") || text.Contains("intel") || text.Contains("realtek") ||
                     text.Contains("audio") || text.Contains("bluetooth") || text.Contains("蓝牙") ||
                     text.Contains("图形") || text.Contains("显卡") || text.Contains("芯片组");
        return vendor || IsUnderWindows(location);
    }

    private static bool IsWindowsOptionalFeature(string name)
    {
        var text = (name ?? string.Empty).ToLowerInvariant();
        var windowsOwned = text.StartsWith("windows ", StringComparison.Ordinal) ||
                           text.StartsWith("microsoft windows ", StringComparison.Ordinal);
        if (!windowsOwned) return false;
        return text.Contains("feature") || text.Contains("optional") ||
               text.Contains("subsystem") || text.Contains("sandbox") ||
               text.Contains("hyper-v") || text.Contains("功能") || text.Contains("可选");
    }
}
