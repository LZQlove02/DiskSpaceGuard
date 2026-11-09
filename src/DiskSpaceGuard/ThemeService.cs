using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskSpaceGuard.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// 主题 = 一份调色板 ResourceDictionary 的整体替换。
/// <para>
/// 颜色值只存在于 <c>Themes/Light.xaml</c> 与 <c>Themes/Dark.xaml</c>，本类不持有任何色值。
/// V2 的阻断事故根因就是"同一批颜色在 App.xaml 和这里各写一遍"：两份漂移后，
/// <see cref="Apply"/> 用旧值覆盖了新建的 token，品牌名掉到 1.09:1、选中导航掉到 1.37:1。
/// 把色值搬进 XAML 之后，这类漂移在结构上不再可能发生。
/// </para>
/// <para>
/// XAML 侧 237 处画刷引用已全部改为 DynamicResource，替换合并字典会就地刷新，
/// 所以 <em>纯 XAML 的部分</em>具备运行时换肤能力。但仍有两类不会跟随：
/// (1) <c>Window.Foreground</c> 之外的 C# 本地值 —— 导航选中态、FileInfo 判定色，
///     都是用户在交互时以本地值写入的，替换字典不会重算；
/// (2) 图表控件在渲染时用 <see cref="ThemeBrush"/> 取一次快照的地方。
/// 因此仍约定：<strong>只在第一个窗口创建前调用一次</strong>，换主题需重启。
/// </para>
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // 前导斜杠 = 应用程序根相对，解析为 pack://application:,,,/Themes/*.xaml。
    private static readonly Uri LightPalette = new("/Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("/Themes/Dark.xaml", UriKind.Relative);

    /// <summary>当前生效的主题。未经 <see cref="Apply"/> 时为 App.xaml 预置的浅色。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

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

    /// <summary>
    /// 切换到指定主题：定位已合并的调色板条目并整体替换。
    /// 必须在第一个窗口创建之前调用，否则已创建的窗口仍是旧主题
    /// （StaticResource 不会因为字典被替换而重新求值）。
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;

        if (Application.Current is not { } app) return;

        var palette = new ResourceDictionary { Source = theme == AppTheme.Dark ? DarkPalette : LightPalette };
        var merged = app.Resources.MergedDictionaries;

        var index = IndexOfPalette(merged);
        if (index >= 0) merged[index] = palette;
        else merged.Insert(0, palette); // App.xaml 里没有预置调色板时的兜底，保证键一定存在
    }

    /// <summary>
    /// 识别调色板字典：含 <c>AccentBrush</c>、不含字体键（字体只写在 App.xaml 根字典里）。
    /// 不用 Source 字符串比对 —— Source 可能被规范化成 pack URI，比对不可靠。
    /// </summary>
    private static int IndexOfPalette(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains("AccentBrush") && !merged[i].Contains("UiFontFamily")) return i;
        }
        return -1;
    }

    /// <summary>
    /// 按资源键取画刷，供 .cs 里的动态着色使用（XAML 走 StaticResource，.cs 走这里）。
    /// 用 TryFindResource 而非索引器：键缺失时返回 null 而不是抛异常。
    /// 会穿透到合并字典，因此换主题后取到的就是新调色板的值。
    /// </summary>
    public static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
