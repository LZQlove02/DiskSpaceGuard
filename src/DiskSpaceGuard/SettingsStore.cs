using System.IO;
using System.Text;
using System.Text.Json;

namespace DiskSpaceGuard.Services;

/// <summary>用户设置：L4 保护目录、托盘与主题。</summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceGuard");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>settings.json 由同用户进程可写，读取前限长，避免超大文件拖垮启动。</summary>
    private const long MaxBytes = 1024 * 1024;

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
            if (new FileInfo(FilePath).Length > MaxBytes) return AppSettings.Empty;

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json,
                new JsonSerializerOptions { MaxDepth = 32 });
            if (loaded is null) return AppSettings.Empty;

            // 手工编辑/损坏的 JSON 可能把 ProtectedRoots 写成 null；
            // 直接传给 SetCustomRoots 会在启动阶段抛 NRE。
            return loaded.ProtectedRoots is null
                ? loaded with { ProtectedRoots = new List<string>() }
                : loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return AppSettings.Empty;
        }
    }

    public static bool Save(AppSettings settings)
    {
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            // 随机临时名 + CreateNew：避免固定 .tmp 被同用户进程预置链接/文件。
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
