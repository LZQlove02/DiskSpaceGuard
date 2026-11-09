using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>
/// §5 硬件信息：优先读取存储 WMI（MSFT_PhysicalDisk / MSFT_StorageReliabilityCounter），
/// 失败时回退到 Win32_DiskDrive。拿不到就标不可用，绝不填 0。
/// </summary>
public sealed class HardwareService
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public Task<HardwareSnapshot> LoadAsync(CancellationToken token)
        => Task.Run(() => Load(token), token);

    public HardwareSnapshot Load(CancellationToken token)
    {
        var elevated = IsElevated();
        try
        {
            var disks = QueryPhysicalDisks(token, elevated);
            return new HardwareSnapshot(disks, elevated, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            try
            {
                return new HardwareSnapshot(QueryFallbackDisks(), elevated, null);
            }
            catch (Exception fallbackEx) when (IsWmiFailure(fallbackEx))
            {
                return new HardwareSnapshot(Array.Empty<PhysicalDiskInfo>(), elevated, fallbackEx.Message);
            }
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<PhysicalDiskInfo> QueryPhysicalDisks(CancellationToken token, bool elevated)
    {
        var partitions = QueryPartitions(token);
        var reliability = QueryReliability(token);
        var disks = new List<PhysicalDiskInfo>();

        using var searcher = new ManagementObjectSearcher(StorageNamespace,
            "SELECT DeviceId, FriendlyName, Model, SerialNumber, FirmwareVersion, MediaType, BusType, " +
            "HealthStatus, Size, PhysicalLocation FROM MSFT_PhysicalDisk");

        foreach (ManagementBaseObject item in searcher.Get())
        {
            token.ThrowIfCancellationRequested();
            using var obj = item;

            var deviceId = Text(obj["DeviceId"]);
            var bus = ToUShort(obj["BusType"]);
            var diskPartitions = partitions.Where(p => p.DiskNumber.ToString() == deviceId).ToList();
            var rel = reliability.TryGetValue(deviceId, out var value) ? value : null;

            var reason = rel is null
                ? (elevated
                    ? "HW.Reason.NotSupported"
                    : "HW.Reason.RequiresAdmin")
                : string.Empty;

            disks.Add(new PhysicalDiskInfo(
                deviceId,
                FirstNonEmpty(Text(obj["FriendlyName"]), Text(obj["Model"])),
                Text(obj["SerialNumber"]),
                Text(obj["FirmwareVersion"]),
                ToUShort(obj["MediaType"]),
                bus,
                ToUShort(obj["HealthStatus"]),
                ToULong(obj["Size"]),
                Text(obj["PhysicalLocation"]),
                diskPartitions,
                rel,
                reason));
        }

        return disks.OrderBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<HardwarePartition> QueryPartitions(CancellationToken token)
    {
        var list = new List<HardwarePartition>();
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNamespace,
                "SELECT DiskNumber, PartitionNumber, DriveLetter, Size, Offset FROM MSFT_Partition");

            foreach (ManagementBaseObject item in searcher.Get())
            {
                token.ThrowIfCancellationRequested();
                using var obj = item;
                list.Add(new HardwarePartition(
                    ToInt(obj["DiskNumber"]),
                    ToInt(obj["PartitionNumber"]),
                    Text(obj["DriveLetter"]),
                    ToULong(obj["Size"]),
                    ToULong(obj["Offset"])));
            }
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            // 分区信息缺失不影响磁盘列表。
        }

        return list;
    }

    private static Dictionary<string, HardwareReliability> QueryReliability(CancellationToken token)
    {
        var map = new Dictionary<string, HardwareReliability>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNamespace,
                "SELECT DeviceId, Temperature, PowerOnHours, PowerCycleCount, Wear, ReadErrorsTotal, " +
                "WriteErrorsTotal FROM MSFT_StorageReliabilityCounter");

            foreach (ManagementBaseObject item in searcher.Get())
            {
                token.ThrowIfCancellationRequested();
                using var obj = item;
                var deviceId = Text(obj["DeviceId"]);
                if (string.IsNullOrEmpty(deviceId)) continue;

                map[deviceId] = new HardwareReliability(
                    ToDouble(obj["Temperature"]),
                    ToDouble(obj["PowerOnHours"]),
                    ToDouble(obj["PowerCycleCount"]),
                    ToDouble(obj["Wear"]),
                    ToDouble(obj["ReadErrorsTotal"]),
                    ToDouble(obj["WriteErrorsTotal"]));
            }
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            // 可靠性计数器通常需要管理员权限或设备不支持；保持空表。
        }

        return map;
    }

    private static IReadOnlyList<PhysicalDiskInfo> QueryFallbackDisks()
    {
        var disks = new List<PhysicalDiskInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Index, Model, SerialNumber, FirmwareRevision, InterfaceType, MediaType, Size, PNPDeviceID " +
            "FROM Win32_DiskDrive");

        foreach (ManagementBaseObject item in searcher.Get())
        {
            using var obj = item;
            var media = Text(obj["MediaType"]);
            disks.Add(new PhysicalDiskInfo(
                Text(obj["Index"]),
                Text(obj["Model"]),
                Text(obj["SerialNumber"]),
                Text(obj["FirmwareRevision"]),
                GuessMediaType(media),
                GuessBusType(Text(obj["InterfaceType"])),
                ushort.MaxValue,
                ToULong(obj["Size"]),
                Text(obj["PNPDeviceID"]),
                Array.Empty<HardwarePartition>(),
                null,
                "HW.Reason.Fallback"));
        }

        return disks;
    }

    private static ushort GuessMediaType(string mediaType)
        => mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? (ushort)4
            : mediaType.Contains("Fixed", StringComparison.OrdinalIgnoreCase) ? (ushort)3
            : ushort.MaxValue;

    private static ushort GuessBusType(string interfaceType) => interfaceType.ToUpperInvariant() switch
    {
        "NVME" => 17,
        "IDE" or "SATA" => 11,
        "USB" => 7,
        "SCSI" => 1,
        _ => ushort.MaxValue
    };

    private static bool IsWmiFailure(Exception ex)
        => ex is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException;

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;

    private static string FirstNonEmpty(string first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : first;

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static ushort ToUShort(object? value)
    {
        try { return value is null ? ushort.MaxValue : Convert.ToUInt16(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return ushort.MaxValue; }
    }

    private static ulong ToULong(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt64(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    private static double? ToDouble(object? value)
    {
        try
        {
            if (value is null) return null;
            var number = Convert.ToDouble(value);
            return double.IsNaN(number) || double.IsInfinity(number) ? null : number;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
