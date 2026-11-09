using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskSpaceGuard.Models;
using Microsoft.Win32;

namespace DiskSpaceGuard.Services;

/// <summary>
/// §15.1 已安装应用枚举：注册表 64/32 位视图 + 当前用户 + MSIX（Get-AppxPackage）。
/// 只读取清单字段；不修改注册表，也不删除任何文件。
/// </summary>
public sealed class InstalledAppService
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<InstalledAppSnapshot> LoadAsync(CancellationToken token)
        => Task.Run(() => Load(token), token);

    public InstalledAppSnapshot Load(CancellationToken token)
    {
        var apps = new List<InstalledApp>();
        EnumerateRegistry(AppSource.Registry64, RegistryHive.LocalMachine, RegistryView.Registry64, apps, token);
        EnumerateRegistry(AppSource.Registry32, RegistryHive.LocalMachine, RegistryView.Registry32, apps, token);
        EnumerateRegistry(AppSource.RegistryHKCU, RegistryHive.CurrentUser, RegistryView.Registry64, apps, token);

        token.ThrowIfCancellationRequested();

        string? appxError = null;
        try
        {
            apps.AddRange(EnumerateAppx(token));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or JsonException)
        {
            appxError = ex.Message;
        }
        catch (Exception ex)
        {
            appxError = ex.Message;
        }

        var deduped = Deduplicate(apps);
        var hidden = deduped.Count(a => a.HiddenByDefault);
        return new InstalledAppSnapshot(deduped, deduped.Count - hidden, hidden, appxError, DateTime.UtcNow);
    }

    /// <summary>MSI 同一条目可能同时出现在 64/32 位视图；64 位视图优先。</summary>
    internal static IReadOnlyList<InstalledApp> Deduplicate(IEnumerable<InstalledApp> apps)
    {
        var best = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.AppKey)) continue;
            if (!best.TryGetValue(app.AppKey, out var existing) ||
                SourceRank(app.Source) < SourceRank(existing.Source))
            {
                best[app.AppKey] = app;
            }
        }

        return best.Values
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.AppKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<InstalledApp> Filter(IEnumerable<InstalledApp> apps, bool includeHidden)
        => apps.Where(a => includeHidden || !a.HiddenByDefault).ToList();

    private static int SourceRank(AppSource source) => source switch
    {
        AppSource.Registry64 => 0,
        AppSource.Registry32 => 1,
        AppSource.RegistryHKCU => 2,
        _ => 3
    };

    internal static List<InstalledApp> EnumerateRegistry(CancellationToken token)
    {
        var apps = new List<InstalledApp>();
        EnumerateRegistry(AppSource.Registry64, RegistryHive.LocalMachine, RegistryView.Registry64, apps, token);
        EnumerateRegistry(AppSource.Registry32, RegistryHive.LocalMachine, RegistryView.Registry32, apps, token);
        EnumerateRegistry(AppSource.RegistryHKCU, RegistryHive.CurrentUser, RegistryView.Registry64, apps, token);
        return apps;
    }

    private static void EnumerateRegistry(AppSource source, RegistryHive hive, RegistryView view,
        List<InstalledApp> output, CancellationToken token)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null) return;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var key = uninstall.OpenSubKey(subKeyName);
                    if (key is null) continue;
                    var app = ReadRegistryApp(key, subKeyName, source);
                    if (app is not null) output.Add(app);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                    or IOException or ObjectDisposedException)
                {
                    Debug.WriteLine($"Uninstall key {subKeyName} skipped: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Debug.WriteLine($"Cannot enumerate uninstall keys ({source}): {ex.Message}");
        }
    }

    private static InstalledApp? ReadRegistryApp(RegistryKey key, string subKeyName, AppSource source)
    {
        var displayName = GetString(key, "DisplayName");
        var publisher = GetString(key, "Publisher");
        var version = GetString(key, "DisplayVersion");
        var releaseType = GetString(key, "ReleaseType");
        var parentKey = GetString(key, "ParentKeyName");
        var installLocation = GetString(key, "InstallLocation");
        var uninstallCommand = GetString(key, "UninstallString");
        var quietCommand = GetString(key, "QuietUninstallString");
        var windowsInstaller = GetInt(key, "WindowsInstaller") == 1;
        var systemComponent = GetInt(key, "SystemComponent") == 1;

        var identity = AppCatalog.BuildIdentity(subKeyName, windowsInstaller, uninstallCommand,
            displayName, publisher, version);

        if (identity.Kind == AppKind.Msi && identity.ProductCode is not null)
        {
            if (!Regex.IsMatch(uninstallCommand, @"/x\b", RegexOptions.IgnoreCase))
                uninstallCommand = $"MsiExec.exe /X{{{identity.ProductCode}}}";

            if (string.IsNullOrWhiteSpace(quietCommand) &&
                uninstallCommand.Contains("msiexec", StringComparison.OrdinalIgnoreCase) &&
                !uninstallCommand.Contains("/q", StringComparison.OrdinalIgnoreCase))
            {
                quietCommand = uninstallCommand + " /qn";
            }
        }

        var hiddenReason = systemComponent
            ? "Apps.Hidden.SystemComponent"
            : !string.IsNullOrWhiteSpace(parentKey)
                ? "Apps.Hidden.Parent"
                : IsUpdateRelease(releaseType)
                    ? "Apps.Hidden.Update"
                    : string.IsNullOrWhiteSpace(displayName)
                        ? "Apps.Hidden.NoName"
                        : string.Empty;

        var app = new InstalledApp
        {
            AppKey = identity.AppKey,
            DisplayName = displayName,
            DisplayVersion = version,
            Publisher = publisher,
            InstallDate = AppCatalog.ParseInstallDate(GetString(key, "InstallDate")),
            EstimatedSizeBytes = AppCatalog.EstimatedSizeToBytes(key.GetValue("EstimatedSize")),
            InstallLocation = installLocation,
            Source = source,
            Kind = identity.Kind,
            UninstallCommand = uninstallCommand,
            QuietUninstallCommand = quietCommand,
            RequiresElevation = source is AppSource.Registry64 or AppSource.Registry32,
            IsSystemComponent = systemComponent,
            HiddenReasonKey = hiddenReason
        };

        return AppCatalog.ApplyPolicy(app);
    }

    private static bool IsUpdateRelease(string releaseType)
    {
        if (string.IsNullOrWhiteSpace(releaseType)) return false;
        var text = releaseType.ToLowerInvariant();
        return text.Contains("update") || text.Contains("hotfix") || text.Contains("security");
    }

    private static string GetString(RegistryKey key, string name)
        => key.GetValue(name) as string ?? string.Empty;

    private static int GetInt(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 0
        };
    }

    /* ================= MSIX ================= */

    private static List<InstalledApp> EnumerateAppx(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var script = "Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,Publisher," +
                     "Version,InstallLocation,IsFramework,IsResourcePackage,NonRemovable,SignatureKind | " +
                     "ConvertTo-Json -Compress";
        var (output, error, exitCode) = RunPowerShell(script, 45_000, token);
        token.ThrowIfCancellationRequested();

        if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"exit {exitCode}" : error.Trim());
        return ParseAppxPackages(output, token);
    }

    /// <summary>解析 Get-AppxPackage 的 JSON；SignatureKind 是枚举，可能输出数字。</summary>
    internal static List<InstalledApp> ParseAppxPackages(string json, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<InstalledApp>();

        using var document = JsonDocument.Parse(json.Trim());
        var packages = new List<AppxPackageDto>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var package = JsonSerializer.Deserialize<AppxPackageDto>(element.GetRawText(), options);
                if (package is not null) packages.Add(package);
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var package = JsonSerializer.Deserialize<AppxPackageDto>(document.RootElement.GetRawText(), options);
            if (package is not null) packages.Add(package);
        }

        var apps = new List<InstalledApp>();
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(package.PackageFullName) ||
                string.IsNullOrWhiteSpace(package.PackageFamilyName)) continue;

            var hiddenReason = package.IsFramework
                ? "Apps.Hidden.Framework"
                : package.IsResourcePackage
                    ? "Apps.Hidden.Resource"
                    : string.Empty;
            var signatureKind = ReadSignatureKind(package.SignatureKind);

            var app = new InstalledApp
            {
                AppKey = "appx:" + package.PackageFamilyName,
                DisplayName = package.Name ?? string.Empty,
                DisplayVersion = package.Version ?? string.Empty,
                Publisher = AppCatalog.SimplifyPublisher(package.Publisher ?? string.Empty),
                InstallLocation = package.InstallLocation ?? string.Empty,
                Source = AppSource.Appx,
                Kind = AppKind.Msix,
                UninstallCommand = AppCatalog.BuildAppxCommand(package.PackageFullName),
                PackageFullName = package.PackageFullName,
                RequiresElevation = false,
                IsPreinstalled = package.NonRemovable ||
                                 string.Equals(signatureKind, "System", StringComparison.OrdinalIgnoreCase),
                HiddenReasonKey = hiddenReason,
                BlockReasonKey = package.NonRemovable ? "Apps.Blocked.NonRemovable" : string.Empty
            };
            apps.Add(AppCatalog.ApplyPolicy(app));
        }

        return apps;
    }

    private static string ReadSignatureKind(JsonElement? element)
    {
        if (element is not { } value) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            return string.Empty;

        return number switch
        {
            1 => "Developer",
            2 => "Enterprise",
            3 => "Store",
            4 => "System",
            _ => "None"
        };
    }

    /// <summary>执行 PowerShell 辅助进程；等待超时则终止该辅助进程（不是被卸载的应用）。</summary>
    internal static (string Output, string Error, int ExitCode) RunPowerShell(
        string script, int timeoutMilliseconds, CancellationToken token)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe not started");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        // 100ms 短等待循环：取消可及时响应，且不再长时间占用线程池线程。
        var start = Environment.TickCount64;
        while (!process.HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 - start >= timeoutMilliseconds)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException("PowerShell timed out");
            }
            process.WaitForExit(100);
        }
        token.ThrowIfCancellationRequested();

        Task.WaitAll(outputTask, errorTask);
        return (outputTask.Result, errorTask.Result, process.ExitCode);
    }

    private sealed class AppxPackageDto
    {
        public string? Name { get; set; }
        public string? PackageFullName { get; set; }
        public string? PackageFamilyName { get; set; }
        public string? Publisher { get; set; }
        public string? Version { get; set; }
        public string? InstallLocation { get; set; }
        public bool IsFramework { get; set; }
        public bool IsResourcePackage { get; set; }
        public bool NonRemovable { get; set; }
        public JsonElement? SignatureKind { get; set; }
    }
}
