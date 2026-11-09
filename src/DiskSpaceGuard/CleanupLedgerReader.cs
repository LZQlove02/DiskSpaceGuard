using System.IO;
using System.Text;
using System.Text.Json;

namespace DiskSpaceGuard.Services;

public sealed record CleanupLedgerItem(
    int ItemId,
    string Path,
    string Outcome,
    string Verify,
    string Message,
    string FileId = "",
    ulong VolumeSerial = 0);

public sealed record CleanupLedgerEntry(
    Guid PlanId,
    DateTime CreatedUtc,
    DateTime ExecutedUtc,
    string Mode,
    string PlanHash,
    int Total,
    int Deleted,
    int RecyclePending,
    int Skipped,
    int Failed,
    long EstimatedBytes,
    long ActualBytes,
    string Fatal,
    IReadOnlyList<CleanupLedgerItem> Items);

public sealed record CleanupLedgerReadResult(
    IReadOnlyList<CleanupLedgerEntry> Entries,
    int SkippedLines,
    string? Error);

/// <summary>把台账导出为 CSV：每条执行的每个条目一行，便于在表格软件中筛选。</summary>
public static class CleanupLedgerCsv
{
    private const string Header =
        "executedUtc,mode,planId,planHash,total,deleted,recyclePending,skipped,failed," +
        "estimatedBytes,actualBytes,fatal,itemId,itemPath,itemOutcome,itemVerify,itemMessage";

    public static string Build(IEnumerable<CleanupLedgerEntry> entries)
    {
        var text = new StringBuilder();
        text.AppendLine(Header);

        foreach (var entry in entries.OrderByDescending(e => e.ExecutedUtc))
        {
            if (entry.Items.Count == 0)
            {
                AppendRow(text, entry, null);
                continue;
            }

            foreach (var item in entry.Items) AppendRow(text, entry, item);
        }

        return text.ToString();
    }

    private static void AppendRow(StringBuilder text, CleanupLedgerEntry entry, CleanupLedgerItem? item)
    {
        text.Append(Field(entry.ExecutedUtc == default ? string.Empty : entry.ExecutedUtc.ToString("o")))
            .Append(',').Append(Field(entry.Mode))
            .Append(',').Append(Field(entry.PlanId.ToString()))
            .Append(',').Append(Field(entry.PlanHash))
            .Append(',').Append(entry.Total)
            .Append(',').Append(entry.Deleted)
            .Append(',').Append(entry.RecyclePending)
            .Append(',').Append(entry.Skipped)
            .Append(',').Append(entry.Failed)
            .Append(',').Append(entry.EstimatedBytes)
            .Append(',').Append(entry.ActualBytes)
            .Append(',').Append(Field(entry.Fatal))
            .Append(',').Append(item is null ? string.Empty : item.ItemId.ToString())
            .Append(',').Append(Field(item?.Path ?? string.Empty))
            .Append(',').Append(Field(item?.Outcome ?? string.Empty))
            .Append(',').Append(Field(item?.Verify ?? string.Empty))
            .Append(',').Append(Field(item?.Message ?? string.Empty))
            .AppendLine();
    }

    /// <summary>CSV 字段转义；公式字符开头时加单引号，防止 Excel/WPS 打开文件时执行公式。</summary>
    private static string Field(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        if (escaped.Length > 0 && escaped[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            escaped = "'" + escaped;
        return "\"" + escaped + "\"";
    }
}

/// <summary>读取 JSONL 清理台账；损坏行跳过并计数，不影响其余记录。</summary>
public static class CleanupLedgerReader
{
    public static CleanupLedgerReadResult Read(string path, int maxEntries = 5000)
    {
        if (!File.Exists(path))
            return new CleanupLedgerReadResult(Array.Empty<CleanupLedgerEntry>(), 0, null);
        if (maxEntries <= 0) maxEntries = 1;

        try
        {
            // 流式环形队列：只保留尾部 maxEntries 行，避免整文件 List + Skip().ToList() 的双份内存。
            // 台账位于 %LOCALAPPDATA%，可被同用户进程塞入超大内容，因此同时限制读取窗口与保留字符数。
            const int MaxLineChars = 8 * 1024 * 1024;
            const long MaxReadBytes = 64L * 1024 * 1024;
            const long MaxRetainedChars = 64L * 1024 * 1024;

            var window = new Queue<string>();
            var skipped = 0;
            long retained = 0;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var tailTruncated = stream.Length > MaxReadBytes;
                if (tailTruncated) stream.Seek(stream.Length - MaxReadBytes, SeekOrigin.Begin);

                using var reader = new StreamReader(stream, Encoding.UTF8);
                // 从文件中间开始时首行大概率是半行，丢弃。
                if (tailTruncated) reader.ReadLine();

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Length > MaxLineChars) { skipped++; continue; }
                    // 逐条结果行（recordType=item）只用于崩溃后审计，不进入台账列表。
                    if (line.Contains("\"recordType\":\"item\"", StringComparison.Ordinal)) continue;

                    if (window.Count == maxEntries)
                        retained -= window.Dequeue().Length;

                    window.Enqueue(line);
                    retained += line.Length;

                    while (retained > MaxRetainedChars && window.Count > 1)
                        retained -= window.Dequeue().Length;
                }
            }

            var entries = new List<CleanupLedgerEntry>();
            foreach (var line in window)
            {
                try
                {
                    var entry = Parse(line);
                    if (entry is null) skipped++;
                    else entries.Add(entry);
                }
                catch (JsonException)
                {
                    skipped++;
                }
            }

            return new CleanupLedgerReadResult(entries, skipped, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CleanupLedgerReadResult(Array.Empty<CleanupLedgerEntry>(), 0, ex.Message);
        }
    }

    private static CleanupLedgerEntry? Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var planIdText = GetString(root, "planId");
        if (!Guid.TryParse(planIdText, out var planId)) return null;

        var items = new List<CleanupLedgerItem>();
        if (root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                items.Add(new CleanupLedgerItem(
                    GetInt(item, "ItemId"),
                    GetString(item, "path"),
                    GetString(item, "outcome"),
                    GetString(item, "verify"),
                    GetString(item, "Message"),
                    GetString(item, "fileId"),
                    GetULong(item, "volumeSerial")));
            }
        }

        return new CleanupLedgerEntry(
            planId,
            GetDate(root, "createdUtc"),
            GetDate(root, "executedUtc"),
            GetString(root, "mode"),
            GetString(root, "planHash"),
            GetInt(root, "total"),
            GetInt(root, "deleted"),
            GetInt(root, "recyclePending"),
            GetInt(root, "skipped"),
            GetInt(root, "failed"),
            GetLong(root, "estimatedBytes"),
            GetLong(root, "actualBytes"),
            GetString(root, "fatal"),
            items);
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static ulong GetULong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetUInt64(out var number) ? number : 0;

    private static DateTime GetDate(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTime.TryParse(text, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : default;
    }
}
