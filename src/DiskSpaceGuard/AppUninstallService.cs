using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskSpaceGuard.Models;

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
                File.AppendAllText(Path, JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
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
        var plan = BuildLaunchPlan(app, silent);
        if (plan is null)
        {
            Log(app, silent, silent ? app.QuietUninstallCommand : app.UninstallCommand,
                null, null, AppUninstallOutcome.NotUninstallable);
            return new AppUninstallResult(AppUninstallOutcome.NotUninstallable, null, null,
                app.UninstallCommand, null, null);
        }

        var psi = new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = WindowsCommandLine.Join(plan.Arguments),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (plan.RequiresElevation && !IsElevated()) psi.Verb = "runas";

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log(app, silent, plan.Command, null, null, AppUninstallOutcome.DeclinedElevation);
            return new AppUninstallResult(AppUninstallOutcome.DeclinedElevation, null, null,
                plan.Command, null, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
            or FileNotFoundException)
        {
            RunLog.WriteException("Uninstall launch", ex);
            Log(app, silent, plan.Command, null, null, AppUninstallOutcome.LaunchFailed);
            return new AppUninstallResult(AppUninstallOutcome.LaunchFailed, null, null,
                plan.Command, ex.Message, null);
        }

        if (process is null)
        {
            Log(app, silent, plan.Command, null, null, AppUninstallOutcome.LaunchFailed);
            return new AppUninstallResult(AppUninstallOutcome.LaunchFailed, null, null,
                plan.Command, null, null);
        }

        using (process)
        {
            if (!await WaitForExitAsync(process, UninstallTimeoutMilliseconds, token).ConfigureAwait(false))
            {
                Log(app, silent, plan.Command, null, null, AppUninstallOutcome.StillRunning);
                return new AppUninstallResult(AppUninstallOutcome.StillRunning, null, null,
                    plan.Command, null, null);
            }

            var exitCode = process.ExitCode;
            var stillInstalled = await Task.Run(() => CheckStillInstalled(app, token), token)
                .ConfigureAwait(false);

            var outcome = DecideOutcome(exitCode, stillInstalled);

            string? residual = null;
            if (stillInstalled == false && AppLeftoverStore.RecordIfValid(app))
                residual = app.InstallLocation;

            Log(app, silent, plan.Command, exitCode, stillInstalled, outcome);
            return new AppUninstallResult(outcome, exitCode, stillInstalled, plan.Command, null, residual);
        }
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
