using System.Security.Cryptography;
using System.Text;

namespace DiskSpaceGuard.Models;

public enum DeleteMode
{
    RecycleFirst,
    Permanent
}

public enum ProtectionLevel
{
    L0,   // 硬禁止：任何情况下不提供删除路径
    L1,   // 重解析点：不穿透
    L2,   // 需强确认（输入目标文件夹名）
    L3,   // 常规确认
    L4    // 自定义保护
}

public enum VerifyState
{
    NotChecked,
    Matched,
    Changed,
    Missing,
    Blocked,
    Unverifiable
}

public enum ItemOutcome
{
    NotStarted,
    Deleted,
    MovedToRecycleBin,
    Skipped,
    Failed
}

public enum DeleteItemKind
{
    File,
    Directory
}

/// <summary>计划条目：创建后不可修改，执行阶段的状态单独存放。</summary>
public sealed record DeleteItem(
    int ItemId,
    string DisplayPath,
    string NormalizedPath,
    long SnapshotSize,
    DateTime SnapshotLastWriteUtc,
    ulong VolumeSerial,
    string FileId,
    ProtectionLevel Protection,
    string ReasonKey,
    DeleteItemKind Kind = DeleteItemKind.File);

/// <summary>删除计划：不可变；PlanHash 覆盖全部影响执行的字段。</summary>
public sealed record DeletePlan(
    Guid PlanId,
    DateTime CreatedUtc,
    string SchemaVersion,
    string PlanVersion,
    string RootHint,
    DeleteMode Mode,
    IReadOnlyList<DeleteItem> Items,
    long EstimatedLogicalBytes,
    string PlanHash)
{
    public int EstimatedItemCount => Items.Count;
    public int StrongConfirmCount => Items.Count(i => i.Protection == ProtectionLevel.L2);
    public int BlockedCount => Items.Count(i => i.Protection == ProtectionLevel.L0);
}

/// <summary>执行期状态：不写回计划。</summary>
public sealed class DeleteRunState
{
    public required Guid PlanId { get; init; }
    public string PlanHashAtStart { get; init; } = string.Empty;
    public bool PlanHashVerified { get; set; }
    public Dictionary<int, VerifyState> Verify { get; } = new();
    public Dictionary<int, ItemOutcome> Outcome { get; } = new();
    public Dictionary<int, string> Messages { get; } = new();
}

public sealed record DeleteItemResult(
    int ItemId,
    string Path,
    ItemOutcome Outcome,
    VerifyState Verify,
    string Message = "");

public sealed record DeleteResult(
    Guid PlanId,
    DeleteMode Mode,
    int Total,
    int Deleted,
    int RecyclePending,
    int Skipped,
    int Failed,
    long EstimatedBytes,
    long ActualBytes,
    TimeSpan Elapsed,
    string? FatalMessage = null,
    long? FreeBytesBefore = null,
    long? FreeBytesAfter = null)
{
    public bool AnySuccess => Deleted + RecyclePending > 0;

    /// <summary>执行前后卷可用空间差值；无法读取卷信息时为 null。</summary>
    public long? FreeSpaceDelta => FreeBytesBefore.HasValue && FreeBytesAfter.HasValue
        ? FreeBytesAfter.Value - FreeBytesBefore.Value
        : null;
}

public static class PlanHasher
{
    public const string SchemaVersion = "1.0";
    // 1.1 起计划条目区分文件/目录（T18 目录递归）。
    public const string PlanVersion = "1.1";

    public static string Compute(
        string schemaVersion,
        string planVersion,
        string rootHint,
        DeleteMode mode,
        IEnumerable<DeleteItem> items)
    {
        var sb = new StringBuilder();
        sb.Append(schemaVersion).Append('|')
          .Append(planVersion).Append('|')
          .Append(rootHint).Append('|')
          .Append((int)mode).Append('\n');

        foreach (var item in items.OrderBy(i => i.ItemId))
        {
            sb.Append(item.ItemId).Append('|')
              .Append(item.NormalizedPath).Append('|')
              .Append(item.SnapshotSize).Append('|')
              .Append(item.SnapshotLastWriteUtc.Ticks).Append('|')
              .Append(item.VolumeSerial).Append('|')
              .Append(item.FileId).Append('|')
              .Append((int)item.Protection).Append('|')
              .Append((int)item.Kind).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
