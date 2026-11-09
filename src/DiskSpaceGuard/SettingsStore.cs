using System.IO;
using System.Text.Json;

namespace DiskSpaceGuard.Services;

/// <summary>用户设置：L4 保护目录、托盘与主题。</summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceGuard");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public sealed record AppSettings(
        List<string> ProtectedRoots,
        bool MinimizeToTray = true,
        bool FollowSystemTheme = true,
        string Language = "zh-CN",
        bool ShowSystemComponents = false)
    {
        public static AppSettings Empty => new(new List<string>());
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return AppSettings.Empty;
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? AppSettings.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return AppSettings.Empty;
        }
    }

    public static bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
