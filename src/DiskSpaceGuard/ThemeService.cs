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
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var dark = theme == AppTheme.Dark;
        Set(resources, "WindowBrush", dark ? "#111318" : "#F5F7FB");
        Set(resources, "CardBrush", dark ? "#171A20" : "#FFFFFF");
        Set(resources, "SubtleBrush", dark ? "#1B1F27" : "#FAFBFD");
        Set(resources, "InputBrush", dark ? "#12151B" : "#FFFFFF");
        Set(resources, "PrimaryTextBrush", dark ? "#E6E8EE" : "#172033");
        Set(resources, "SecondaryTextBrush", dark ? "#9AA1AE" : "#64748B");
        Set(resources, "BorderBrush", dark ? "#282C35" : "#E5E7EB");
        Set(resources, "SoftBrush", dark ? "#1D2740" : "#EEF2FF");
        Set(resources, "AccentBrush", dark ? "#6D93FF" : "#2563EB");
        Set(resources, "NavBrush", dark ? "#0B0E14" : "#111827");
        Set(resources, "NavSelectedBrush", dark ? "#1E2A44" : "#263449");
        Set(resources, "NavTextBrush", dark ? "#C9CED6" : "#D1D5DB");
        Set(resources, "InfoBgBrush", dark ? "#17233D" : "#E8F0FF");
        Set(resources, "SuccessBgBrush", dark ? "#10261C" : "#ECFDF5");
        Set(resources, "SuccessTextBrush", dark ? "#6EE7B7" : "#15803D");
        Set(resources, "WarningBgBrush", dark ? "#2A2110" : "#FEF3C7");
        Set(resources, "WarningTextBrush", dark ? "#FCD34D" : "#B45309");
        Set(resources, "DangerTextBrush", dark ? "#FCA5A5" : "#B91C1C");
    }

    private static void Set(ResourceDictionary resources, string key, string color)
        => resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
