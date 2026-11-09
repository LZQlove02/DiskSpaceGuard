using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

public sealed record DiagnosticsPreview(string Text, IReadOnlyList<string> IncludedFiles, bool Redacted);

public sealed record DiagnosticsExportResult(string Path, int Entries, string? HardwareError, bool Redacted);

/// <summary>
/// §9 / §14.11 诊断报告：系统摘要、卷、设置快照、硬件能力矩阵与日志尾部。
/// 默认脱敏；只打包元数据与日志，绝不包含文件内容。
/// </summary>
public sealed class DiagnosticsService
{
    public const int MaxLogLines = 200;

    private readonly HardwareService _hardwareService = new();

    public DiagnosticsPreview BuildPreview(bool redact)
    {
        var text = new StringBuilder();
        text.AppendLine("DiskSpaceGuard 诊断报告 · 预览");
        text.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"脱敏：{(redact ? "开启（用户名与用户目录替换为 <user>）" : "关闭（将显示原始路径）")}");
        text.AppendLine();
        AppendSystemSummary(text);
        AppendVolumes(text);
        AppendSettings(text);
        AppendLogSummary(text);
        AppendLogPreview(text);

        var rendered = redact ? PrivacyRedactor.RedactDefault(text.ToString()) : text.ToString();
        return new DiagnosticsPreview(rendered, BuildFileList(), redact);
    }

    public async Task<DiagnosticsExportResult> ExportAsync(string zipPath, bool redact,
        CancellationToken token)
    {
        HardwareSnapshot? hardware = null;
        string? hardwareError = null;
        try
        {
            hardware = await Task.Run(() => _hardwareService.Load(token), token).ConfigureAwait(false);
            hardwareError = hardware.Error;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            hardwareError = ex.Message;
            RunLog.WriteException("Diagnostics hardware", ex);
        }

        if (redact && hardware is not null)
            hardware = PrivacyRedactor.RedactHardware(hardware);

        token.ThrowIfCancellationRequested();

        var systemText = BuildSystemText();
        var settingsJson = JsonSerializer.Serialize(SettingsStore.Load(),
            new JsonSerializerOptions { WriteIndented = true });
        var hardwareJson = hardware is null
            ? $"{{\"error\":{JsonSerializer.Serialize(hardwareError ?? "hardware unavailable")}}}"
            : JsonSerializer.Serialize(hardware, new JsonSerializerOptions { WriteIndented = true });

        var cleanupTail = ReadTail(DeleteService.LedgerFile, MaxLogLines);
        var appOpsTail = ReadTail(AppOpLedger.FilePath, MaxLogLines);
        var runTail = ReadTail(RunLog.FilePath, MaxLogLines);
        var lastError = ReadTail(RunLog.LastErrorPath, MaxLogLines);

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? ".");

        // ZipFile.Open(Create) 内部使用 FileMode.CreateNew：用户确认覆盖后文件仍存在，
        // 会直接抛“文件已存在”。这里用 FileMode.Create 兑现覆盖语义。
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var entries = 0;
        entries += AddText(zip, "README.txt", BuildReadme(redact), redact);
        entries += AddText(zip, "system.txt", systemText, redact);
        entries += AddText(zip, "settings.json", settingsJson, redact);
        entries += AddText(zip, "hardware.json", hardwareJson, redact);
        entries += AddText(zip, "cleanup-log.tail.jsonl", cleanupTail, redact);
        entries += AddText(zip, "app-ops.tail.jsonl", appOpsTail, redact);
        entries += AddText(zip, "run-log.tail.txt", runTail, redact);
        if (!string.IsNullOrWhiteSpace(lastError))
            entries += AddText(zip, "last-error.txt", lastError, redact);

        return new DiagnosticsExportResult(zipPath, entries, hardwareError, redact);
    }

    /// <summary>读取文本文件末尾 N 行；不存在返回空，读取失败返回可见的说明。</summary>
    internal static string ReadTail(string path, int maxLines)
    {
        if (maxLines <= 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;

        try
        {
            // 日志也是同用户可写文件：限制读取窗口与单行长度，避免超长行造成大分配。
            const int MaxLineChars = 256 * 1024;
            const long MaxTailBytes = 8L * 1024 * 1024;

            var queue = new Queue<string>(maxLines);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tailTruncated = stream.Length > MaxTailBytes;
            if (tailTruncated) stream.Seek(stream.Length - MaxTailBytes, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            // 从文件中间开始时首行大概率是半行，丢弃。
            if (tailTruncated) reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length > MaxLineChars) line = line[..MaxLineChars] + "…";
                if (queue.Count == maxLines) queue.Dequeue();
                queue.Enqueue(line);
            }

            return string.Join(Environment.NewLine, queue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[无法读取 {Path.GetFileName(path)}：{ex.GetType().Name}]";
        }
    }

    private static IReadOnlyList<string> BuildFileList()
    {
        var files = new List<string>
        {
            "README.txt",
            "system.txt",
            "settings.json",
            "hardware.json",
            "cleanup-log.tail.jsonl",
            "app-ops.tail.jsonl",
            "run-log.tail.txt"
        };
        if (File.Exists(RunLog.LastErrorPath)) files.Add("last-error.txt");
        return files;
    }

    private static string BuildReadme(bool redact)
    {
        var text = new StringBuilder();
        text.AppendLine("DiskSpaceGuard 诊断报告");
        text.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"脱敏：{(redact ? "开启" : "关闭")}");
        text.AppendLine();
        text.AppendLine("包含内容：系统版本、应用版本、卷列表、设置快照、硬件能力矩阵、清理/应用台账尾部、运行日志尾部。");
        text.AppendLine("隐私：默认把用户名与用户目录替换为 <user>，硬盘序列号/固件/物理位置替换为 <serial>/<firmware>/<location>；不包含任何被扫描或清理文件的内容。");
        text.AppendLine("说明：日志尾部最多各 200 行；硬件能力由 WMI/SMART 尽力读取，失败时 hardware.json 只含 error 字段。");
        return text.ToString();
    }

    private static void AppendSystemSummary(StringBuilder text)
    {
        var assembly = Assembly.GetEntryAssembly()?.GetName();
        text.AppendLine("== 系统与版本 ==");
        text.AppendLine($"应用版本：{assembly?.Version?.ToString() ?? "unknown"}");
        text.AppendLine($".NET：{RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"OS：{RuntimeInformation.OSDescription}");
        text.AppendLine($"OS 版本：{Environment.OSVersion.VersionString}");
        text.AppendLine($"进程架构：{RuntimeInformation.ProcessArchitecture} / OS 架构：{RuntimeInformation.OSArchitecture}");
        text.AppendLine($"管理员：{(IsElevated() ? "是" : "否")}");
        text.AppendLine($"时区：{TimeZoneInfo.Local.Id}");
        text.AppendLine();
    }

    private static string BuildSystemText()
    {
        var text = new StringBuilder();
        AppendSystemSummary(text);
        AppendVolumes(text);
        text.AppendLine();
        AppendLogSummary(text);
        return text.ToString();
    }

    private static void AppendVolumes(StringBuilder text)
    {
        text.AppendLine("== 卷 ==");
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine($"无法枚举卷：{ex.GetType().Name}");
            text.AppendLine();
            return;
        }

        foreach (var drive in drives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!drive.IsReady)
                {
                    text.AppendLine($"{drive.Name} | {drive.DriveType} | 未就绪 | GUID 未知");
                    continue;
                }

                var guid = VolumeGuid(drive.Name) ?? "未知";
                text.AppendLine($"{drive.Name} | {drive.DriveType} | {drive.DriveFormat} | " +
                                $"总 {FormatBytes(drive.TotalSize)} | 可用 {FormatBytes(drive.AvailableFreeSpace)} | {guid}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException)
            {
                text.AppendLine($"{drive.Name} | 读取失败 | {ex.GetType().Name}");
            }
        }
        text.AppendLine();
    }

    private static void AppendSettings(StringBuilder text)
    {
        text.AppendLine("== 设置快照 ==");
        text.AppendLine(JsonSerializer.Serialize(SettingsStore.Load(),
            new JsonSerializerOptions { WriteIndented = true }));
        text.AppendLine();
    }

    private static void AppendLogSummary(StringBuilder text)
    {
        text.AppendLine("== 日志尾部 ==");
        text.AppendLine($"cleanup-log.jsonl：{LineCount(ReadTail(DeleteService.LedgerFile, MaxLogLines))} 行");
        text.AppendLine($"app-ops.jsonl：{LineCount(ReadTail(AppOpLedger.FilePath, MaxLogLines))} 行");
        text.AppendLine($"run.log：{LineCount(ReadTail(RunLog.FilePath, MaxLogLines))} 行");
        text.AppendLine($"last-error.txt：{(File.Exists(RunLog.LastErrorPath) ? "存在" : "无")}");
        text.AppendLine();
    }

    private static void AppendLogPreview(StringBuilder text)
    {
        text.AppendLine("== 日志尾部内容（最多各 200 行） ==");
        AppendTail(text, "cleanup-log.jsonl", DeleteService.LedgerFile);
        AppendTail(text, "app-ops.jsonl", AppOpLedger.FilePath);
        AppendTail(text, "run.log", RunLog.FilePath);
        AppendTail(text, "last-error.txt", RunLog.LastErrorPath);
    }

    private static void AppendTail(StringBuilder text, string name, string path)
    {
        text.AppendLine($"--- {name} ---");
        var tail = ReadTail(path, MaxLogLines);
        text.AppendLine(tail.Length == 0 ? "（空）" : tail);
        text.AppendLine();
    }

    private static int LineCount(string text)
        => text.Length == 0 ? 0 : text.Split('\n').Count(l => l.TrimEnd('\r').Length > 0);

    private static string FormatBytes(long bytes)
        => ScanNode.FormatBytes(bytes);

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

    private static string? VolumeGuid(string mountPoint)
    {
        try
        {
            var buffer = new StringBuilder(64);
            return GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Capacity)
                ? buffer.ToString()
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException)
        {
            return null;
        }
    }

    private static int AddText(ZipArchive zip, string name, string content, bool redact)
    {
        var text = redact ? PrivacyRedactor.RedactDefault(content) : content;
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
        return 1;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);
}
