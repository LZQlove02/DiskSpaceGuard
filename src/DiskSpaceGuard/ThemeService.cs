using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskSpaceGuard.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>启动时按系统设置选择浅色/深色调色板；默认浅色。</summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppTheme SystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int value) return value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 读不到系统偏好时保持浅色，不阻断启动。
        }
        return AppTheme.Light;
    }

    public static void Apply(AppTheme theme)
    {
        // 浅色值须与 App.xaml 保持一致；App.xaml 是浅色主题的唯一真源。
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var dark = theme == AppTheme.Dark;
        Set(resources, "WindowBrush", dark ? "#111318" : "#FAFCFC");
        Set(resources, "CardBrush", dark ? "#171A20" : "#FFFFFF");
        Set(resources, "SubtleBrush", dark ? "#1B1F27" : "#F5F9F9");
        Set(resources, "InputBrush", dark ? "#12151B" : "#FFFFFF");
        Set(resources, "PrimaryTextBrush", dark ? "#E6E8EE" : "#0F1F2E");
        Set(resources, "SecondaryTextBrush", dark ? "#9AA1AE" : "#5C7480");
        Set(resources, "BorderBrush", dark ? "#282C35" : "#E0EBEA");
        Set(resources, "SoftBrush", dark ? "#1D2740" : "#EFF5F4");
        Set(resources, "AccentBrush", dark ? "#6D93FF" : "#2C7A7B");
        Set(resources, "NavBrush", dark ? "#0B0E14" : "#F5F9F9");
        Set(resources, "NavSelectedBrush", dark ? "#1E2A44" : "#DFF4F1");
        Set(resources, "NavTextBrush", dark ? "#C9CED6" : "#5C7480");
        Set(resources, "InfoBgBrush", dark ? "#17233D" : "#EAF7F5");
        Set(resources, "SuccessBgBrush", dark ? "#10261C" : "#EAF7F5");
        Set(resources, "SuccessTextBrush", dark ? "#6EE7B7" : "#1B4F51");
        Set(resources, "WarningBgBrush", dark ? "#2A2110" : "#FDF6E3");
        Set(resources, "WarningTextBrush", dark ? "#FCD34D" : "#7A5406");
        Set(resources, "DangerTextBrush", dark ? "#FCA5A5" : "#C0392B");
        Set(resources, "NavSelectedTextBrush", dark ? "#7FE0D6" : "#1B4F51");
        Set(resources, "DangerBgBrush", dark ? "#2A1414" : "#FDECEC");
        // OnAccentBrush 必须随 AccentBrush 一起翻转：暗色 Accent 是浅蓝 #6D93FF，
        // 白字只有 2.89:1，改深墨 #0F1A1F 后 6.12:1。浅色分支保持 #FFFFFF（5.03:1）。
        Set(resources, "OnAccentBrush", dark ? "#0F1A1F" : "#FFFFFF");
    }

    private static void Set(ResourceDictionary resources, string key, string color)
        => resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    /// <summary>
    /// 按资源键取画刷，供 .cs 里的动态着色使用（XAML 走 StaticResource，.cs 走这里）。
    /// 用 TryFindResource 而非索引器：键缺失时返回 null 而不是抛异常。
    /// </summary>
    public static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
