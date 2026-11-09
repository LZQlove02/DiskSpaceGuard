using System.IO;
using System.Security;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

public sealed class DriveInventoryService
{
    public IReadOnlyList<DriveVolume> GetVolumes()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (IsExpectedDriveException(ex))
        {
            return [new DriveVolume("—", "无法枚举卷", "—", "—", 0, 0, 0, "读取失败", ex.Message)];
        }

        var results = new List<DriveVolume>(drives.Length);
        foreach (var drive in drives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var driveType = drive.DriveType.ToString();
                var label = SafeLabel(drive);
                if (!drive.IsReady)
                {
                    results.Add(new DriveVolume(drive.Name, label, "—", driveType, 0, 0, 0, "未就绪"));
                    continue;
                }

                var total = Math.Max(0, drive.TotalSize);
                var available = Math.Clamp(drive.AvailableFreeSpace, 0, total);
                var volumeFree = Math.Clamp(drive.TotalFreeSpace, 0, total);
                results.Add(new DriveVolume(
                    drive.Name, label, SafeFormat(drive), driveType, total,
                    available, volumeFree, "正常"));
            }
            catch (Exception ex) when (IsExpectedDriveException(ex))
            {
                results.Add(new DriveVolume(drive.Name, SafeLabel(drive), "—", SafeDriveType(drive),
                    0, 0, 0, "读取失败", ex.Message));
            }
        }
        return results;
    }

    private static bool IsExpectedDriveException(Exception ex) => ex is IOException or
        UnauthorizedAccessException or SecurityException or ArgumentException or
        NotSupportedException or System.ComponentModel.Win32Exception;

    private static string SafeLabel(DriveInfo drive)
    {
        try { return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "未命名卷" : drive.VolumeLabel; }
        catch (Exception ex) when (IsExpectedDriveException(ex)) { return "卷信息不可用"; }
    }

    private static string SafeFormat(DriveInfo drive)
    {
        try { return drive.DriveFormat; }
        catch (Exception ex) when (IsExpectedDriveException(ex)) { return "未知"; }
    }

    private static string SafeDriveType(DriveInfo drive)
    {
        try { return drive.DriveType.ToString(); }
        catch (Exception ex) when (IsExpectedDriveException(ex)) { return "未知"; }
    }
}
