namespace DiskSpaceGuard.Models;

public sealed record HardwarePartition(
    int DiskNumber,
    int PartitionNumber,
    string DriveLetter,
    ulong Size,
    ulong Offset);

/// <summary>设备直接报告的可靠性字段；null 表示该字段不可用，绝不补 0。</summary>
public sealed record HardwareReliability(
    double? TemperatureCelsius,
    double? PowerOnHours,
    double? PowerCycleCount,
    double? WearPercent,
    double? ReadErrors,
    double? WriteErrors);

public sealed record PhysicalDiskInfo(
    string DeviceId,
    string Model,
    string Serial,
    string Firmware,
    ushort MediaType,
    ushort BusType,
    ushort HealthStatus,
    ulong Size,
    string Location,
    IReadOnlyList<HardwarePartition> Partitions,
    HardwareReliability? Reliability,
    string ReliabilityUnavailableReason);

public sealed record HardwareSnapshot(
    IReadOnlyList<PhysicalDiskInfo> Disks,
    bool IsElevated,
    string? Error);

/// <summary>硬件字段映射：只做枚举翻译与格式化，不推算寿命。</summary>
public static class HardwareCatalog
{
    public static string MediaTypeKey(ushort mediaType) => mediaType switch
    {
        3 => "HW.Media.HDD",
        4 => "HW.Media.SSD",
        5 => "HW.Media.SCM",
        _ => "HW.Media.Unknown"
    };

    public static string BusTypeKey(ushort busType) => busType switch
    {
        1 or 2 or 10 => "HW.Bus.SCSI",
        3 => "HW.Bus.ATA",
        7 => "HW.Bus.USB",
        8 => "HW.Bus.RAID",
        9 => "HW.Bus.iSCSI",
        11 => "HW.Bus.SATA",
        12 => "HW.Bus.SD",
        13 => "HW.Bus.MMC",
        14 or 15 => "HW.Bus.Virtual",
        16 => "HW.Bus.StorageSpaces",
        17 => "HW.Bus.NVMe",
        18 => "HW.Bus.SCM",
        19 => "HW.Bus.UFS",
        _ => "HW.Bus.Unknown"
    };

    public static string HealthKey(ushort health) => health switch
    {
        0 => "HW.Health.Healthy",
        1 => "HW.Health.Warning",
        2 => "HW.Health.Unhealthy",
        _ => "HW.Health.Unknown"
    };

    /// <summary>设备报告值可能超过 100%；原样展示，不截断、不转换。</summary>
    public static string? WearDisplay(double? wearPercent)
        => wearPercent is null ? null : $"{wearPercent:0.#}%";

    public static bool WearExceedsHundred(double? wearPercent)
        => wearPercent is > 100;
}
