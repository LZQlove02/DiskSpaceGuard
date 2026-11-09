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
    public static IReadOnlyList<string> RestorablePaths(CleanupLedgerEntry entry)
    {
        if (entry.Mode == "Permanent") return Array.Empty<string>();
        return entry.Items
            .Where(i => i.Outcome == "MovedToRecycleBin" && IsSafeRestorePath(i.Path))
            .Select(i => ProtectionEvaluator.Normalize(i.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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

    public static RestoreResult Restore(IReadOnlyList<string> originalPaths)
    {
        var results = new List<RestoreItemResult>();
        dynamic? recycleBin = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return AllFailed(originalPaths, Text("Restore.ShellUnavailable"));

            dynamic shell = Activator.CreateInstance(shellType)!;
            recycleBin = shell.NameSpace(RecycleBinNamespace);
            if (recycleBin is null)
                return AllFailed(originalPaths, Text("Restore.ShellUnavailable"));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return AllFailed(originalPaths, ex.Message);
        }

        // 一次性建立回收站条目索引，把 O(P×R) 的 COM 全扫降为 O(R+P)。
        Dictionary<string, dynamic>? index = null;
        try { index = BuildIndex(recycleBin); }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            System.Diagnostics.Debug.WriteLine($"Recycle index failed: {ex.Message}");
        }

        foreach (var path in originalPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            // 台账可被篡改：非本地 / 不可信路径一律不触发任何文件系统或 Shell 访问。
            if (!IsSafeRestorePath(path))
            {
                results.Add(new RestoreItemResult(path, false, Text("Restore.NotFound")));
                continue;
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                results.Add(new RestoreItemResult(path, false, Text("Restore.AlreadyExists")));
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
                    results.Add(new RestoreItemResult(path, false, Text("Restore.NotFound")));
                    continue;
                }

                var invoked = InvokeRestoreVerb(item);
                if (!invoked)
                {
                    results.Add(new RestoreItemResult(path, false, Text("Restore.NoVerb")));
                    continue;
                }

                if (WaitForPath(path, TimeSpan.FromSeconds(3)))
                    results.Add(new RestoreItemResult(path, true, Text("Restore.Done")));
                else
                    results.Add(new RestoreItemResult(path, false, Text("Restore.NotReappeared")));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
            {
                results.Add(new RestoreItemResult(path, false, ex.Message));
            }
        }

        return new RestoreResult(
            results.Count(r => r.Restored),
            results.Count(r => !r.Restored),
            results);
    }

    /// <summary>一次遍历回收站，按「原位置」建索引；同名多条保留第一条，与 FindItem 语义一致。</summary>
    private static Dictionary<string, dynamic> BuildIndex(dynamic recycleBin)
    {
        var index = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
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

            if (!string.IsNullOrEmpty(deletedFrom) && !string.IsNullOrEmpty(name))
            {
                var key = Path.Combine(deletedFrom, name)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (!index.ContainsKey(key)) index[key] = item;
            }
            else if (!string.IsNullOrEmpty(itemPath))
            {
                var key = itemPath.TrimEnd(Path.DirectorySeparatorChar);
                if (!index.ContainsKey(key)) index[key] = item;
            }
        }

        return index;
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

    private static RestoreResult AllFailed(IReadOnlyList<string> paths, string message)
        => new(0, paths.Count, paths.Select(p => new RestoreItemResult(p, false, message)).ToList());

    private static string Text(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);
}
