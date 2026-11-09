using System.IO;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>
/// 显式状态机：按钮可用性与状态转换都由这里驱动，不靠分散的布尔标记。
/// 非法转换返回 false（T5）。
/// </summary>
public sealed class ScanStateMachine
{
    private static readonly IReadOnlyDictionary<ScanTaskState, ScanTaskState[]> Allowed =
        new Dictionary<ScanTaskState, ScanTaskState[]>
        {
            [ScanTaskState.Idle] = [ScanTaskState.Preparing],
            [ScanTaskState.Preparing] = [ScanTaskState.Running, ScanTaskState.Cancelling, ScanTaskState.Failed],
            [ScanTaskState.Running] = [ScanTaskState.Pausing, ScanTaskState.Cancelling, ScanTaskState.Completed, ScanTaskState.Failed],
            [ScanTaskState.Pausing] = [ScanTaskState.Paused, ScanTaskState.Running, ScanTaskState.Cancelling, ScanTaskState.Completed, ScanTaskState.Failed],
            [ScanTaskState.Paused] = [ScanTaskState.Running, ScanTaskState.Cancelling],
            [ScanTaskState.Cancelling] = [ScanTaskState.Cancelled, ScanTaskState.Failed],
            [ScanTaskState.Cancelled] = [],
            [ScanTaskState.Completed] = [],
            [ScanTaskState.Failed] = []
        };

    private readonly object _lock = new();
    private ScanTaskState _state = ScanTaskState.Idle;

    public ScanTaskState State
    {
        get { lock (_lock) return _state; }
    }

    public bool IsTerminal => State is ScanTaskState.Cancelled or ScanTaskState.Completed or ScanTaskState.Failed;

    public bool TryTransition(ScanTaskState next)
    {
        lock (_lock)
        {
            if (!Allowed[_state].Contains(next)) return false;
            _state = next;
            return true;
        }
    }

    public void Transition(ScanTaskState next)
    {
        if (!TryTransition(next))
            throw new InvalidOperationException($"非法状态转换：{State} → {next}");
    }
}

public static class ScanErrorClassifier
{
    public static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;

    public static ScanErrorKind Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ScanErrorKind.AccessDenied,
        DirectoryNotFoundException or FileNotFoundException => ScanErrorKind.PathNotFound,
        PathTooLongException => ScanErrorKind.PathTooLong,
        IOException io => ClassifyIo(io),
        _ => ScanErrorKind.OtherIo
    };

    private static ScanErrorKind ClassifyIo(IOException io)
    {
        var code = io.HResult & 0xFFFF;
        return code switch
        {
            5 => ScanErrorKind.AccessDenied,          // ERROR_ACCESS_DENIED
            2 or 3 => ScanErrorKind.PathNotFound,     // FILE_NOT_FOUND / PATH_NOT_FOUND
            32 or 33 => ScanErrorKind.SharingViolation,
            206 => ScanErrorKind.PathTooLong,         // ERROR_FILENAME_EXCED_RANGE
            21 or 53 or 55 or 64 or 1167 => ScanErrorKind.DiskUnavailable,
            _ => ScanErrorKind.OtherIo
        };
    }
}
