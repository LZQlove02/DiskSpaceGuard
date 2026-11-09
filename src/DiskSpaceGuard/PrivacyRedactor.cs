using System.IO;
using System.Text.RegularExpressions;

namespace DiskSpaceGuard.Services;

/// <summary>§9 / §14.11：诊断导出默认脱敏用户名与用户目录，绝不包含文件内容。</summary>
public static class PrivacyRedactor
{
    public static string Redact(string text, string userName, string profilePath)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            var profile = profilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (profile.Length > 0)
            {
                result = Regex.Replace(result, Regex.Escape(profile), "<user>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        // 单字符用户名直接全局替换会破坏文本；短用户名只依赖目录替换。
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
        {
            result = Regex.Replace(result, Regex.Escape(userName), "<user>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return result;
    }

    public static string RedactDefault(string text)
        => Redact(text, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
