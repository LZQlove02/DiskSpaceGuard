using System.IO;
using System.Runtime.InteropServices;

namespace DiskSpaceGuard.Services;

public sealed record RestoreItemResult(string Path, bool Restored, string Message);

public sealed record RestoreResult(int Restored, int Failed, IReadOnlyList<RestoreItemResult> Items)
{
    public bool AnyFailure => Failed > 0;
}

/// <summary>
/// §14.1 还原：通过 Shell 命名空间（回收站）按原路径定位对象并调用系统“还原”动词。
/// 只能还原回收站中的对象；永久删除的对象无法还原。
/// </summary>
public static class RecycleBinRestoreService
{
    private const int RecycleBinNamespace = 10;

    /// <summary>只有回收站方式、且路径为本地可信路径的记录才能还原；按路径去重。</summary>
    public static IReadOnlyList<CleanupLedgerItem> RestorableItems(CleanupLedgerEntry entry)
    {
        if (entry.Mode == "Permanent") return Array.Empty<CleanupLedgerItem>();
        return entry.Items
            .Where(i => i.Outcome == "MovedToRecycleBin" && IsSafeRestorePath(i.Path))
            .GroupBy(i => ProtectionEvaluator.Normalize(i.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public static IReadOnlyList<string> RestorablePaths(CleanupLedgerEntry entry)
        => RestorableItems(entry)
            .Select(i => ProtectionEvaluator.Normalize(i.Path))
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>
    /// 台账位于 %LOCALAPPDATA%，可被同用户其他进程篡改。还原前只接受本地盘符的规范化绝对路径，
    /// 拒绝 UNC（File.Exists 会触发出站 SMB / NetNTLMv2 泄露）、设备前缀、卷 GUID 与备用数据流。
    /// </summary>
    internal static bool IsSafeRestorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = ProtectionEvaluator.Normalize(path);
        if (normalized.Length == 0) return false;
        var root = Path.GetPathRoot(normalized);
        return !string.IsNullOrEmpty(root) && root.Length >= 2 &&
               char.IsLetter(root[0]) && root[1] == ':';
    }

    /// <summary>
    /// 批量核对“原位置是否真的出现在回收站”。一次遍历回收站，避免逐项 O(P×R)。
    /// 返回 null 表示无法枚举回收站（不能据此断言成功或失败）。
    /// </summary>
    public static HashSet<string>? TryCollectRecycledPaths(IEnumerable<string> originalPaths,
        CancellationToken token = default)
    {
        var wanted = new HashSet<string>(
            originalPaths.Select(ProtectionEvaluator.Normalize).Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? recycleBin = shell.NameSpace(RecycleBinNamespace);
            if (recycleBin is null) return null;

            dynamic items = recycleBin.Items();
            var count = (int)items.Count;
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                dynamic item = items.Item(i);
                string? deletedFrom = null;
                string? name = null;

                try
                {
                    deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom") as string;
                    name = item.Name as string;
                }
                catch (COMException) { }

                if (string.IsNullOrEmpty(deletedFrom) || string.IsNullOrEmpty(name)) continue;

                var key = ProtectionEvaluator.Normalize(
                    Path.Combine(deletedFrom, name).TrimEnd(Path.DirectorySeparatorChar));
                if (key.Length > 0 && wanted.Contains(key)) found.Add(key);
            }

            return found;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 复核失败绝不能影响已完成的删除结果；返回 null 由调用方如实标注“无法复核”。
            return null;
        }
    }

    public static RestoreResult Restore(IReadOnlyList<string> originalPaths)
        => Restore(originalPaths
            .Select(p => new CleanupLedgerItem(0, p, "MovedToRecycleBin", string.Empty, string.Empty))
            .ToList());

    public static RestoreResult Restore(IReadOnlyList<CleanupLedgerItem> items, DateTime? preferredUtc = null)
    {
        var results = new List<RestoreItemResult>();
        dynamic? recycleBin = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return AllFailed(items, Text("Restore.ShellUnavailable"));

            dynamic shell = Activator.CreateInstance(shellType)!;
            recycleBin = shell.NameSpace(RecycleBinNamespace);
            if (recycleBin is null)
                return AllFailed(items, Text("Restore.ShellUnavailable"));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return AllFailed(items, ex.Message);
        }

        // 一次性建立回收站条目索引，把 O(P×R) 的 COM 全扫降为 O(R+P)。
        // 同名多条时，优先选择删除时间最接近台账执行时间的条目。
        Dictionary<string, dynamic>? index = null;
        try { index = BuildIndex(recycleBin, preferredUtc); }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            System.Diagnostics.Debug.WriteLine($"Recycle index failed: {ex.Message}");
        }

        foreach (var ledgerItem in items)
        {
            var path = ProtectionEvaluator.Normalize(ledgerItem.Path);
            if (path.Length == 0) continue;

            // 台账可被篡改：非本地 / 不可信路径一律不触发任何文件系统或 Shell 访问。
            if (!IsSafeRestorePath(path))
            {
                results.Add(new RestoreItemResult(ledgerItem.Path, false, Text("Restore.NotFound")));
                continue;
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                results.Add(new RestoreItemResult(ledgerItem.Path, false, Text("Restore.AlreadyExists")));
                continue;
            }

            try
            {
                dynamic? item;
                if (index is not null)
                {
                    var key = path.TrimEnd(Path.DirectorySeparatorChar);
                    item = index.TryGetValue(key, out var found) ? found : null;
                }
                else
                {
                    item = FindItem(recycleBin, path);
                }
                if (item is null)
                {
                    results.Add(new RestoreItemResult(ledgerItem.Path, false, Text("Restore.NotFound")));
                    continue;
                }

                var invoked = InvokeRestoreVerb(item);
                if (!invoked)
                {
                    results.Add(new RestoreItemResult(ledgerItem.Path, false, Text("Restore.NoVerb")));
                    continue;
                }

                if (!WaitForPath(path, TimeSpan.FromSeconds(3)))
                {
                    results.Add(new RestoreItemResult(ledgerItem.Path, false,
                        Text("Restore.NotReappeared")));
                    continue;
                }

                // 还原后复核对象身份：同名对象可能被还原错，或回收站条目被替换。
                if (!MatchesLedgerIdentity(path, ledgerItem))
                {
                    results.Add(new RestoreItemResult(ledgerItem.Path, false,
                        Text("Restore.IdentityMismatch")));
                    continue;
                }

                results.Add(new RestoreItemResult(ledgerItem.Path, true, Text("Restore.Done")));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
            {
                results.Add(new RestoreItemResult(ledgerItem.Path, false, ex.Message));
            }
        }

        return new RestoreResult(
            results.Count(r => r.Restored),
            results.Count(r => !r.Restored),
            results);
    }

    /// <summary>旧台账没有 FileId 时不做阻断；有身份且能读取时必须一致。</summary>
    private static bool MatchesLedgerIdentity(string restoredPath, CleanupLedgerItem item)
    {
        if (string.IsNullOrEmpty(item.FileId)) return true;
        if (!FileIdentityService.TryReadIdentity(restoredPath, out var identity, out _)) return true;
        return string.Equals(identity.ToPlanString(), item.FileId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一次遍历回收站，按「原位置」建索引；同名多条时取删除时间最接近台账执行时间者。</summary>
    private static Dictionary<string, dynamic> BuildIndex(dynamic recycleBin, DateTime? preferredUtc)
    {
        var index = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
        var bestDelta = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        dynamic items = recycleBin.Items();
        var count = (int)items.Count;

        for (var i = 0; i < count; i++)
        {
            dynamic item = items.Item(i);
            string? itemPath = null;
            string? deletedFrom = null;
            string? name = null;

            try { itemPath = item.Path as string; } catch (COMException) { }
            try
            {
                deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom") as string;
                name = item.Name as string;
            }
            catch (COMException) { }

            var deletedUtc = ReadDeletedUtc(item);
            var delta = DeltaSeconds(deletedUtc, preferredUtc);

            if (!string.IsNullOrEmpty(deletedFrom) && !string.IsNullOrEmpty(name))
                Consider(Path.Combine(deletedFrom, name).TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(itemPath))
                Consider(itemPath.TrimEnd(Path.DirectorySeparatorChar));

            void Consider(string key)
            {
                if (!index.ContainsKey(key))
                {
                    index[key] = item;
                    bestDelta[key] = delta;
                    return;
                }

                if (preferredUtc is null || deletedUtc is null) return;
                if (delta < bestDelta[key])
                {
                    index[key] = item;
                    bestDelta[key] = delta;
                }
            }
        }

        return index;
    }

    private static DateTime? ReadDeletedUtc(dynamic item)
    {
        try
        {
            object? value = item.ExtendedProperty("System.Recycle.DateDeleted");
            return value switch
            {
                DateTime date => date,
                string text when DateTime.TryParse(text, out var parsed) => parsed,
                _ => null
            };
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static double DeltaSeconds(DateTime? deletedUtc, DateTime? preferredUtc)
    {
        if (deletedUtc is null || preferredUtc is null) return double.MaxValue;
        var deleted = deletedUtc.Value.Kind == DateTimeKind.Utc
            ? deletedUtc.Value
            : deletedUtc.Value.ToUniversalTime();
        var preferred = preferredUtc.Value.Kind == DateTimeKind.Utc
            ? preferredUtc.Value
            : preferredUtc.Value.ToUniversalTime();
        return Math.Abs((deleted - preferred).TotalSeconds);
    }

    private static dynamic? FindItem(dynamic recycleBin, string originalPath)
    {
        dynamic items = recycleBin.Items();
        var count = (int)items.Count;

        var expected = originalPath.TrimEnd(Path.DirectorySeparatorChar);

        for (var i = 0; i < count; i++)
        {
            dynamic item = items.Item(i);
            string? itemPath = null;
            string? deletedFrom = null;
            string? name = null;

            try { itemPath = item.Path as string; } catch (COMException) { }
            try
            {
                deletedFrom = item.ExtendedProperty("System.Recycle.DeletedFrom") as string;
                name = item.Name as string;
            }
            catch (COMException) { }

            // 回收站条目的 Path 是 $Recycle.Bin 内的存储路径；
            // 原位置必须用 DeletedFrom + 名称重建，不能直接比较 Path。
            if (!string.IsNullOrEmpty(deletedFrom) && !string.IsNullOrEmpty(name))
            {
                var reconstructed = Path.Combine(deletedFrom, name)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(reconstructed, expected, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            if (!string.IsNullOrEmpty(itemPath) &&
                string.Equals(itemPath.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    private static bool InvokeRestoreVerb(dynamic item)
    {
        try
        {
            dynamic verbs = item.Verbs();
            var count = (int)verbs.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic verb = verbs.Item(i);
                var name = (verb.Name as string ?? string.Empty).Replace("&", string.Empty);
                if (name.Contains("还原", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Restore", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("撤消删除", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Undo", StringComparison.OrdinalIgnoreCase))
                {
                    verb.DoIt();
                    return true;
                }
            }
        }
        catch (COMException)
        {
            // 回退到系统默认动词名。
        }

        try { item.InvokeVerb("Restore"); return true; }
        catch (COMException) { }
        try { item.InvokeVerb("还原"); return true; }
        catch (COMException) { }

        return false;
    }

    private static bool WaitForPath(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) || Directory.Exists(path)) return true;
            Thread.Sleep(120);
        }
        return false;
    }

    private static RestoreResult AllFailed(IReadOnlyList<CleanupLedgerItem> items, string message)
        => new(0, items.Count, items.Select(i => new RestoreItemResult(i.Path, false, message)).ToList());

    private static string Text(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);
}
