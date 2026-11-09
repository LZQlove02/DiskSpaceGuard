using DiskSpaceGuard.Services;

namespace DiskSpaceGuard.Models;

public sealed record DriveVolume(
    string Name,
    string Label,
    string Format,
    string DriveType,
    long TotalBytes,
    long FreeBytes,
    long VolumeFreeBytes,
    string Status,
    string? Error = null)
{
    public bool HasSize => TotalBytes > 0;

    /// <summary>已用 = 总容量 - 卷真实空闲；不受当前用户配额影响。</summary>
    public long UsedBytes => HasSize ? Math.Clamp(TotalBytes - VolumeFreeBytes, 0, TotalBytes) : 0;
    public double UsedRatio => TotalBytes <= 0 ? 0 : Math.Clamp((double)UsedBytes / TotalBytes, 0, 1);

    public string TotalDisplay => HasSize ? FormatBytes(TotalBytes) : "—";
    public string UsedDisplay => HasSize ? FormatBytes(UsedBytes) : "—";
    public string FreeDisplay => HasSize ? FormatBytes(FreeBytes) : "—";
    public string VolumeFreeDisplay => HasSize ? FormatBytes(VolumeFreeBytes) : "—";

    public string StatusDisplay => Status switch
    {
        "正常" => Text("Volume.StatusOk"),
        "读取失败" => Text("Volume.ReadFailed"),
        "未就绪" => Text("Volume.NotReady"),
        _ => Status
    };

    public string LabelDisplay => Label switch
    {
        "未命名卷" => Text("Volume.Unnamed"),
        "卷信息不可用" => Text("Volume.Unavailable"),
        "无法枚举卷" => Text("Volume.EnumerateFailed"),
        _ => Label
    };

    public bool HasQuotaDifference =>
        HasSize && Math.Abs(VolumeFreeBytes - FreeBytes) >= 1024 * 1024;

    public string QuotaNote => HasQuotaDifference
        ? string.Format(Text("Card.Quota"), FormatBytes(Math.Abs(VolumeFreeBytes - FreeBytes)))
        : string.Empty;

    private static string Text(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return Text("Common.Unknown");
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N1} {units[unit]}";
    }
}
