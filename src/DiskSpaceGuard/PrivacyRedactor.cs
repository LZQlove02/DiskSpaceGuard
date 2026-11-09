using System.IO;
using System.Text.RegularExpressions;

namespace DiskSpaceGuard.Services;

/// <summary>§9 / §14.11：诊断导出默认脱敏用户名、用户目录、卷 GUID 与命令中的凭据参数。</summary>
public static class PrivacyRedactor
{
    // \\?\Volume{GUID} 属于设备命名空间，脱敏成 <volume> 而不是泄露卷标识。
    private static readonly Regex VolumeGuidPath = new(
        @"\\\\\?\\Volume\{[^}]+\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 卸载命令 / 日志中可能带 /PASSWORD=、-p、--token= 等凭据参数。
    private static readonly Regex CredentialArgument = new(
        @"(/(?:PASSWORD|PASS|PWD|TOKEN|SECRET|KEY|APIKEY)=|--?(?:password|passwd|token|secret|api[-_]?key)\s*[= ])\s*([^\s""',;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        result = VolumeGuidPath.Replace(result, "<volume>");
        result = CredentialArgument.Replace(result, "$1<redacted>");

        return result;
    }

    public static string RedactDefault(string text)
        => Redact(text, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
