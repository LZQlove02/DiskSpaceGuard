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

    /// <summary>只有回收站方式的记录才能还原；按路径去重。</summary>
    public static IReadOnlyList<string> RestorablePaths(CleanupLedgerEntry entry)
    {
        if (entry.Mode == "Permanent") return Array.Empty<string>();
        return entry.Items
            .Where(i => i.Outcome == "MovedToRecycleBin" && !string.IsNullOrWhiteSpace(i.Path))
            .Select(i => i.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        foreach (var path in originalPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (File.Exists(path) || Directory.Exists(path))
            {
                results.Add(new RestoreItemResult(path, false, Text("Restore.AlreadyExists")));
                continue;
            }

            try
            {
                dynamic? item = FindItem(recycleBin, path);
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
