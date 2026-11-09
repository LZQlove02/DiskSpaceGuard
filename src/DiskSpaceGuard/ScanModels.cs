using System.Globalization;

namespace DiskSpaceGuard.Models;

public enum ScanMode
{
    FullRecursive,
    Shallow,
    FastThenFull
}

/// <summary>任务生命周期状态，与结果完整性分开建模。</summary>
public enum ScanTaskState
{
    Idle,
    Preparing,
    Running,
    Pausing,
    Paused,
    Cancelling,
    Cancelled,
    Completed,
    Failed
}

/// <summary>结果完整性：是否遍历完所有可访问范围。</summary>
public enum ScanCompleteness
{
    Complete,
    Partial,
    Cancelled,
    Failed
}

/// <summary>数值可信度：稀疏/压缩文件会让逻辑大小与物理占用产生差异。</summary>
public enum SizePrecision
{
    Exact,
    Approximate,
    Unknown
}

public enum ObjectChangeState
{
    Stable,
    ChangedDuringScan,
    Unverified
}

public enum NodeKind
{
    Directory,
    File,
    ReparsePoint
}

public enum ScanErrorKind
{
    AccessDenied,
    PathNotFound,
    DiskUnavailable,
    SharingViolation,
    PathTooLong,
    OtherIo
}

/// <summary>可视化配色用的粗分类，按扩展名推导。</summary>
public enum NodeCategory
{
    System,
    Program,
    Media,
    Document,
    Archive,
    Other
}

public static class NodeCategoryClassifier
{
    private static readonly HashSet<string> SystemExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "sys", "dll", "drv", "ocx", "cpl", "exe", "mui", "msi", "wim", "cab", "inf" };
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "mp3", "wav", "flac", "aac", "m4a", "jpg", "jpeg", "png", "gif", "bmp", "webp", "raw", "heic", "psd" };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "md", "rtf", "csv", "epub" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "jar" };

    public static NodeCategory Classify(string name, string fullPath)
    {
        var ext = FileInfoCatalog.ExtensionOf(name);
        if (SystemExtensions.Contains(ext)) return NodeCategory.System;
        if (MediaExtensions.Contains(ext)) return NodeCategory.Media;
        if (DocumentExtensions.Contains(ext)) return NodeCategory.Document;
        if (ArchiveExtensions.Contains(ext)) return NodeCategory.Archive;

        var lower = fullPath.ToLowerInvariant();
        if (lower.Contains("\\program files", StringComparison.Ordinal) ||
            lower.Contains("\\programdata", StringComparison.Ordinal))
            return NodeCategory.Program;

        return NodeCategory.Other;
    }
}

public sealed class ScanNode
{
    public ScanNode(string name, string fullPath, NodeKind kind)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
    }

    public string Name { get; }
    public string FullPath { get; }
    public NodeKind Kind { get; internal set; }

    /// <summary>路径逻辑大小：每个路径都计入（硬链接在多个目录中各计一次）。</summary>
    public long LogicalSize { get; internal set; }

    /// <summary>物理占用；M2 尚未读取，保持 null。</summary>
    public long? AllocatedSize { get; internal set; }

    public DateTime LastWriteUtc { get; internal set; }

    /// <summary>false 表示浅层扫描边界上的“未扫描目录”，大小必须显示为 —。</summary>
    public bool IsScanned { get; internal set; } = true;

    public bool IsPlaceholder { get; internal set; }

    /// <summary>小于阈值的小文件被合并为一个节点，不提供单文件操作。</summary>
    public bool IsAggregate { get; internal set; }

    public int AggregatedCount { get; internal set; }

    public NodeCategory Category { get; internal set; } = NodeCategory.Other;

    public int AccessDeniedCount { get; internal set; }
    public int ErrorCount { get; internal set; }
    public int SkippedReparseCount { get; internal set; }

    public ScanCompleteness Completeness { get; internal set; } = ScanCompleteness.Complete;

    public List<ScanNode> Children { get; } = new();

    public bool IsDirectory => Kind == NodeKind.Directory;

    public string KindText => IsAggregate
        ? string.Format(Text("Node.Aggregate"), AggregatedCount)
        : Kind switch
        {
            NodeKind.Directory => Text(IsScanned ? "Node.Directory" : "Node.NotScanned"),
            NodeKind.ReparsePoint => Text("Node.Reparse"),
            _ => Text("Node.File")
        };

    public string SizeDisplay => IsScanned ? FormatBytes(LogicalSize) : "—";

    public string TimeDisplay => LastWriteUtc == default
        ? "—"
        : LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string StateBadge => IsAggregate
        ? Text("Node.Merged")
        : !IsScanned
            ? Text("Node.NotScanned")
            : AccessDeniedCount > 0 || ErrorCount > 0
                ? Text("Node.Partial")
                : string.Empty;

    private static string Text(string key)
        => Services.LocalizationService.Text(key, Services.Loc.Instance.Language);

    public override string ToString() => Name;

    internal void AddChild(ScanNode child) => Children.Add(child);

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:N1} {units[unit]}";
    }
}

public sealed record ScanStats(
    string RootPath,
    int Depth,
    long Files,
    long Directories,
    long LogicalBytes,
    int AccessDenied,
    int ReparseSkipped,
    int OtherErrors,
    ScanTaskState State,
    ScanCompleteness Completeness,
    SizePrecision Precision,
    ObjectChangeState Change,
    TimeSpan Elapsed,
    string Phase,
    string? Message = null)
{
    public static ScanStats Empty => new(
        string.Empty, 0, 0, 0, 0, 0, 0, 0,
        ScanTaskState.Idle, ScanCompleteness.Partial,
        SizePrecision.Unknown, ObjectChangeState.Unverified,
        TimeSpan.Zero, string.Empty);

    public string SizeDisplay => ScanNode.FormatBytes(LogicalBytes);
    public string ElapsedDisplay => Elapsed.TotalSeconds < 1
        ? $"{Elapsed.TotalMilliseconds:0} ms"
        : Elapsed.TotalSeconds < 60
            ? $"{Elapsed.TotalSeconds:0.0} s"
            : $"{(int)Elapsed.TotalMinutes} m {Elapsed.Seconds} s";
}

public sealed class ScanResult
{
    public ScanResult(ScanNode root, ScanStats stats)
    {
        Root = root;
        Stats = stats;
    }

    public ScanNode Root { get; }
    public ScanStats Stats { get; }
    public bool HasData => Root.Children.Count > 0 || Root.LogicalSize > 0;
}
