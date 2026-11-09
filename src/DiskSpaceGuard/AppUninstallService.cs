using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskSpaceGuard.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceGuard.Services;

public enum AppUninstallOutcome
{
    Succeeded,
    ExitCodeFailed,
    NotRemoved,
    VerifyUnavailable,
    DeclinedElevation,
    StillRunning,
    Busy,
    NotUninstallable,
    LaunchFailed
}

public sealed record AppUninstallResult(
    AppUninstallOutcome Outcome,
    int? ExitCode,
    bool? StillInstalled,
    string Command,
    string? Detail,
    string? ResidualPath);

public sealed record AppLaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string Command,
    bool IsSilent,
    bool RequiresElevation);

public sealed record AppOpEntry(
    DateTime ExecutedUtc,
    string AppKey,
    string DisplayName,
    string Command,
    bool Silent,
    int? ExitCode,
    bool? StillInstalled,
    string Outcome);

/// <summary>应用操作台账：与文件清理台账分开写入，互不混用。</summary>
public static class AppOpLedger
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSpaceGuard", "app-ops.jsonl");

    public static string FilePath => Path;

    public static bool Append(AppOpEntry entry)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            lock (Gate)
            {
                DeleteService.RepairLedgerTail(Path);
                // 卸载命令可能带许可证/密码参数：落盘前先脱敏。
                var redacted = entry with { Command = PrivacyRedactor.RedactDefault(entry.Command) };
                File.AppendAllText(Path, JsonSerializer.Serialize(redacted) + Environment.NewLine, Encoding.UTF8);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cannot write app ledger: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// §15.2 卸载执行：只启动应用自带的官方卸载入口，绝不删除安装目录或注册表键。
/// 等待卸载程序退出（不隐藏窗口、不自动 kill）；退出后重新枚举并如实报告。
/// </summary>
public sealed class AppUninstallService
{
    private const int UninstallTimeoutMilliseconds = 10 * 60 * 1000;
    private const int VerifyTimeoutMilliseconds = 20_000;

    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>可测试的命令选择与参数解析；不可卸载或缺少静默入口时返回 null。</summary>
    internal static AppLaunchPlan? BuildLaunchPlan(InstalledApp app, bool silent)
    {
        if (!app.IsUninstallable) return null;

        var command = silent ? app.QuietUninstallCommand : app.UninstallCommand;
        if (string.IsNullOrWhiteSpace(command)) return null;

        var tokens = WindowsCommandLine.Split(command);
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0])) return null;

        return new AppLaunchPlan(tokens[0], tokens.Skip(1).ToList(), command, silent, app.RequiresElevation);
    }

    private static readonly HashSet<string> ForbiddenLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "mshta.exe", "wscript.exe", "cscript.exe",
        "rundll32.exe", "regsvr32.exe", "certutil.exe", "msbuild.exe", "schtasks.exe",
        "reg.exe", "net.exe", "sc.exe", "bash.exe", "wsl.exe", "java.exe", "python.exe"
    };

    private static readonly HashSet<string> AllowedBareSystemCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe"
    };

    /// <summary>
    /// §15.2 执行阶段信任边界：注册表 UninstallString / QuietUninstallString 属第三方可控输入。
    /// 只接受本地绝对路径的 .exe（或解析到 System32 的 msiexec），拒绝 UNC、设备前缀、
    /// 备用数据流、脚本解释器与 PATH 相对解析。BuildLaunchPlan 保持可测试，校验放在启动前。
    /// </summary>
    internal static bool TryResolveTrustedLaunch(string fileName, out string resolved, out string reason)
    {
        resolved = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            reason = "卸载命令为空";
            return false;
        }

        var candidate = fileName.Trim().Trim('"');
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "拒绝 UNC / 网络路径作为卸载目标";
            return false;
        }

        if (!Path.IsPathRooted(candidate))
        {
            // 仅允许系统自带的 msiexec，且固定解析到 System32，避免当前目录 / PATH 劫持。
            if (!AllowedBareSystemCommands.Contains(Path.GetFileName(candidate)))
            {
                reason = "拒绝相对路径 / PATH 解析的卸载目标";
                return false;
            }
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.IsNullOrWhiteSpace(systemDir))
            {
                reason = "无法解析系统目录";
                return false;
            }
            candidate = Path.Combine(systemDir, Path.GetFileName(candidate));
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "卸载目标路径无法规范化";
            return false;
        }

        if (full.Length > 2 && full.IndexOf(':', 2) >= 0)
        {
            reason = "拒绝备用数据流路径";
            return false;
        }
        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "卸载目标不是可执行文件";
            return false;
        }
        var leaf = Path.GetFileName(full);
        if (ForbiddenLaunchers.Contains(leaf))
        {
            reason = "拒绝脚本 / 解释器类卸载目标：" + leaf;
            return false;
        }
        if (!File.Exists(full))
        {
            reason = "卸载目标文件不存在：" + full;
            return false;
        }

        // 重解析点会把“看起来无害的路径”指向任意目标；系统目录本身就存在大量硬链接，
        // 因此只对非 Windows 目录额外拒绝多链接文件。
        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = "拒绝重解析点作为卸载目标";
                return false;
            }

            using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(handle, out var info) &&
                info.NumberOfLinks > 1 && !AppCatalog.IsUnderWindows(full))
            {
                reason = "拒绝硬链接作为卸载目标";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "无法读取卸载目标属性：" + ex.Message;
            return false;
        }

        resolved = full;
        return true;
    }

    // 原生 FILETIME 按 4 字节对齐；必须 Pack=4，否则 long 字段会按 8 字节对齐导致布局错位。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    public async Task<AppUninstallResult> UninstallAsync(InstalledApp app, bool silent, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new AppUninstallResult(AppUninstallOutcome.Busy, null, null, app.UninstallCommand, null, null);

        try
        {
            return await UninstallCoreAsync(app, silent, token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task<AppUninstallResult> UninstallCoreAsync(
        InstalledApp app, bool silent, CancellationToken token)
    {
        ProcessStartInfo psi;
        string command;

        if (app.Source == AppSource.Appx)
        {
            // MSIX 由本进程直接调用 System32 的 PowerShell，不走注册表命令与 PATH 解析。
            if (!TryBuildAppxLaunch(app, out psi, out command, out var reason))
            {
                RunLog.WriteException("Uninstall appx target", new InvalidOperationException(reason));
                Log(app, silent, command, null, null, AppUninstallOutcome.LaunchFailed);
                return new AppUninstallResult(AppUninstallOutcome.LaunchFailed, null, null,
                    command, reason, null);
            }
        }
        else
        {
            var plan = BuildLaunchPlan(app, silent);
            if (plan is null)
            {
                Log(app, silent, silent ? app.QuietUninstallCommand : app.UninstallCommand,
                    null, null, AppUninstallOutcome.NotUninstallable);
                return new AppUninstallResult(AppUninstallOutcome.NotUninstallable, null, null,
                    app.UninstallCommand, null, null);
            }

            if (!TryResolveTrustedLaunch(plan.FileName, out var resolved, out var trustReason))
            {
                RunLog.WriteException("Uninstall untrusted target", new InvalidOperationException(trustReason));
                Log(app, silent, plan.Command, null, null, AppUninstallOutcome.LaunchFailed);
                return new AppUninstallResult(AppUninstallOutcome.LaunchFailed, null, null,
                    plan.Command, trustReason, null);
            }

            command = plan.Command;

            // 仅提权分支需要 ShellExecute；非提权用 ArgumentList 逐项传参，不再拼接命令字符串。
            var elevated = plan.RequiresElevation && !IsElevated();
            psi = new ProcessStartInfo
            {
                FileName = resolved,
                UseShellExecute = elevated,
                WindowStyle = ProcessWindowStyle.Normal
            };
            if (elevated)
            {
                psi.Verb = "runas";
                psi.Arguments = WindowsCommandLine.Join(plan.Arguments);
            }
            else
            {
                foreach (var argument in plan.Arguments) psi.ArgumentList.Add(argument);
            }
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log(app, silent, command, null, null, AppUninstallOutcome.DeclinedElevation);
            return new AppUninstallResult(AppUninstallOutcome.DeclinedElevation, null, null,
                command, null, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
            or FileNotFoundException)
        {
            RunLog.WriteException("Uninstall launch", ex);
            Log(app, silent, command, null, null, AppUninstallOutcome.LaunchFailed);
            return new AppUninstallResult(AppUninstallOutcome.LaunchFailed, null, null,
                command, ex.Message, null);
        }

        if (process is null)
        {
            Log(app, silent, command, null, null, AppUninstallOutcome.LaunchFailed);
            return new AppUninstallResult(AppUninstallOutcome.LaunchFailed, null, null,
                command, null, null);
        }

        using (process)
        {
            if (!await WaitForExitAsync(process, UninstallTimeoutMilliseconds, token).ConfigureAwait(false))
            {
                Log(app, silent, command, null, null, AppUninstallOutcome.StillRunning);
                return new AppUninstallResult(AppUninstallOutcome.StillRunning, null, null,
                    command, null, null);
            }

            var exitCode = process.ExitCode;
            var stillInstalled = await Task.Run(() => CheckStillInstalled(app, token), token)
                .ConfigureAwait(false);

            var outcome = DecideOutcome(exitCode, stillInstalled);

            string? residual = null;
            // MSIX 的 InstallLocation 位于系统管理的 WindowsApps，不能登记为“卸载残留”。
            if (stillInstalled == false && app.Source != AppSource.Appx &&
                AppLeftoverStore.RecordIfValid(app))
                residual = app.InstallLocation;

            Log(app, silent, command, exitCode, stillInstalled, outcome);
            return new AppUninstallResult(outcome, exitCode, stillInstalled, command, null, residual);
        }
    }

    /// <summary>
    /// MSIX 不走注册表命令：只接受 Get-AppxPackage 枚举出的合法包名，
    /// 由本进程直接启动 System32 下的 PowerShell，脚本按参数列表传入，不经过 PATH 解析。
    /// </summary>
    private static bool TryBuildAppxLaunch(InstalledApp app, out ProcessStartInfo psi,
        out string command, out string reason)
    {
        psi = new ProcessStartInfo();
        command = app.UninstallCommand;
        reason = string.Empty;

        if (!AppCatalog.IsValidPackageFullName(app.PackageFullName))
        {
            reason = "MSIX 包名不合法";
            return false;
        }

        var powershell = InstalledAppService.ResolveSystemPowerShell();
        if (powershell.Length == 0)
        {
            reason = "找不到系统 PowerShell（System32\\powershell.exe）";
            return false;
        }

        psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(powershell)!
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("Remove-AppxPackage -Package '" + app.PackageFullName + "'");

        command = powershell + " -NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '" +
                  app.PackageFullName + "'\"";
        return true;
    }

    /// <summary>
    /// §15.2 / T32：退出码非 0 一律不报告成功；重新检测仍存在时报告“未完成”。
    /// 重新检测不可用时单独报告，绝不假定成功。
    /// </summary>
    internal static AppUninstallOutcome DecideOutcome(int exitCode, bool? stillInstalled)
        => stillInstalled switch
        {
            null => AppUninstallOutcome.VerifyUnavailable,
            true => exitCode == 0 ? AppUninstallOutcome.NotRemoved : AppUninstallOutcome.ExitCodeFailed,
            false => exitCode == 0 ? AppUninstallOutcome.Succeeded : AppUninstallOutcome.ExitCodeFailed
        };

    /// <summary>等待卸载程序；超时返回 false，且绝不终止该进程。</summary>
    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMilliseconds,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMilliseconds);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>重新枚举同一去重键；null 表示无法完成验证，不当作“已卸载”。</summary>
    private static bool? CheckStillInstalled(InstalledApp app, CancellationToken token)
    {
        if (app.Source == AppSource.Appx)
        {
            if (!AppCatalog.IsValidPackageFullName(app.PackageFullName)) return null;
            try
            {
                var script = "Get-AppxPackage -Package '" + app.PackageFullName +
                             "' | Select-Object -ExpandProperty PackageFullName";
                var (output, _, exitCode) = InstalledAppService.RunPowerShell(
                    script, VerifyTimeoutMilliseconds, token);
                if (exitCode != 0 && string.IsNullOrWhiteSpace(output)) return null;
                return output.Trim().Length > 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"Appx verification failed: {ex.Message}");
                return null;
            }
        }

        try
        {
            return InstalledAppService.EnumerateRegistry(token).Any(a =>
                string.Equals(a.AppKey, app.AppKey, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"Registry verification failed: {ex.Message}");
            return null;
        }
    }

    private static void Log(InstalledApp app, bool silent, string command, int? exitCode,
        bool? stillInstalled, AppUninstallOutcome outcome)
        => AppOpLedger.Append(new AppOpEntry(DateTime.UtcNow, app.AppKey, app.DisplayName,
            command, silent, exitCode, stillInstalled, outcome.ToString()));

    internal static bool IsElevated()
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
}
