using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

public sealed record AppLeftover(string DisplayName, string InstallLocation, DateTime RecordedUtc);

/// <summary>
/// §15.4 卸载残留登记：只登记「卸载已验证完成、安装目录仍存在」的路径。
/// 登记本身不删除任何内容；清理规则 app.leftovers 仅把它作为候选，仍需预演与确认。
/// </summary>
public static class AppLeftoverStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceGuard");

    private static string FilePath => Path.Combine(Dir, "app-leftovers.json");

    public static IReadOnlyList<AppLeftover> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<AppLeftover>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<AppLeftover>>(json) ?? new List<AppLeftover>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<AppLeftover>();
        }
    }

    /// <summary>卸载并重新检测确认已移除后调用；返回是否登记成功。</summary>
    public static bool RecordIfValid(InstalledApp app)
    {
        var location = app.InstallLocation;
        if (!IsSafeLocation(location) || !Directory.Exists(location)) return false;

        try
        {
            Directory.CreateDirectory(Dir);
            var items = Load().Where(l =>
                !string.Equals(l.InstallLocation, location, StringComparison.OrdinalIgnoreCase)).ToList();
            items.Add(new AppLeftover(app.DisplayName, location, DateTime.UtcNow));
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cannot record app leftover: {ex.Message}");
            return false;
        }
    }

    /// <summary>给规则引擎生成 app.leftovers 候选；目录已消失的残留不再生成。</summary>
    public static IReadOnlyList<CleanupRule> BuildRules()
    {
        try
        {
            return Load()
                .Where(l => IsSafeLocation(l.InstallLocation) && Directory.Exists(l.InstallLocation))
                .Select(l => new CleanupRule(
                    "app.leftovers",
                    "应用卸载残留",
                    "应用残留",
                    l.InstallLocation,
                    TimeSpan.Zero,
                    $"卸载 {l.DisplayName} 后安装目录仍存在；需人工确认后再清理"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<CleanupRule>();
        }
    }

    /// <summary>
    /// 拒绝共享根本身、Windows 目录与不可信路径形态。
    /// app-leftovers.json 可被同用户进程篡改，必须先剥离设备前缀并拒绝 UNC / 卷 GUID / ADS，
    /// 否则可构造指向系统目录的清理规则（配合 recursive 展开会进入删除计划）。
    /// </summary>
    internal static bool IsSafeLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = ProtectionEvaluator.Normalize(path);
        if (full.Length == 0) return false;

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        if (root.Length < 2 || !char.IsLetter(root[0]) || root[1] != ':') return false;
        if (full.Length <= root.Length) return false;
        if (AppCatalog.IsUnderWindows(full)) return false;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shared = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            userProfile,
            string.IsNullOrWhiteSpace(userProfile) ? string.Empty : Path.GetDirectoryName(userProfile) ?? string.Empty
        };

        // 共享根本身不能当作残留；其子目录仍允许（真实应用残留多在 Program Files 下）。
        return !shared.Any(s =>
            !string.IsNullOrWhiteSpace(s) &&
            string.Equals(full, ProtectionEvaluator.Normalize(s), StringComparison.OrdinalIgnoreCase));
    }
}
