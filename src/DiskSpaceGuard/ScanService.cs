using System.Diagnostics;
using System.IO;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

public sealed class ScanAbortedException : Exception
{
    public ScanAbortedException(ScanErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public ScanErrorKind Kind { get; }
}

/// <summary>
/// 扫描内核：完全递归 / 分层 / 先快后全。
/// 约定：不跟随目录重解析点；按目录隔离异常；Shallow 边界节点标记为未扫描（大小显示 —）。
/// </summary>
public sealed class ScanService
{
    private static string Text(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private const int ReportIntervalMs = 180;

    /// <summary>小于该值的文件不单独建节点，见 §2.6 内存剪枝。</summary>
    public long AggregateThresholdBytes { get; set; } = 1024 * 1024;

    private readonly ScanStateMachine _machine = new();
    private readonly ManualResetEventSlim _resumeGate = new(true);
    private readonly Stopwatch _clock = new();

    private CancellationTokenSource? _linkedCts;
    private CancellationToken _token = CancellationToken.None;
    private IProgress<ScanStats>? _progress;

    private int _pauseRequested;
    private long _files;
    private long _directories;
    private long _bytes;
    private long _denied;
    private long _reparse;
    private long _errors;
    private long _lastReportMs;
    private bool _approximate;
    private bool _changed;

    private string _rootPath = string.Empty;
    private string _phase = string.Empty;
    private ScanMode _mode;
    private int _shallowDepth = 1;
    private int _depthUsed;
    private ScanNode? _root;
    private ScanNode? _shallowRoot;
    private ScanStats? _shallowStats;

    public ScanTaskState State => _machine.State;

    public event Action<ScanTaskState>? StateChanged;

    public Task<ScanResult> ScanAsync(
        string rootPath,
        ScanMode mode,
        int shallowDepth,
        IProgress<ScanStats>? progress = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("扫描根不能为空。", nameof(rootPath));

        _machine.Transition(ScanTaskState.Preparing);
        _rootPath = rootPath;
        _mode = mode;
        _shallowDepth = Math.Clamp(shallowDepth, 1, 3);
        _depthUsed = mode == ScanMode.Shallow ? _shallowDepth : 0;
        _progress = progress;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _linkedCts.Token;
        _clock.Restart();
        ResetCounters();
        _shallowRoot = null;
        _shallowStats = null;
        NotifyState();

        return Task.Run(Run, CancellationToken.None);
    }

    public void Pause()
    {
        if (_machine.State != ScanTaskState.Running) return;
        Interlocked.Exchange(ref _pauseRequested, 1);
        _resumeGate.Reset();
        if (_machine.TryTransition(ScanTaskState.Pausing))
        {
            NotifyState();
            Report(force: true);
        }
    }

    public void Resume()
    {
        if (_machine.State == ScanTaskState.Paused)
        {
            if (_machine.TryTransition(ScanTaskState.Running))
            {
                _resumeGate.Set();
                NotifyState();
                Report(force: true);
            }
        }
        else if (_machine.State == ScanTaskState.Pausing)
        {
            Interlocked.Exchange(ref _pauseRequested, 0);
            if (_machine.TryTransition(ScanTaskState.Running))
            {
                _resumeGate.Set();
                NotifyState();
                Report(force: true);
            }
        }
    }

    /// <summary>完成后调用 Cancel 无效（T5 边界：忽略，不改变最终状态）。</summary>
    public void Cancel()
    {
        var state = _machine.State;
        if (state is ScanTaskState.Completed or ScanTaskState.Cancelled or ScanTaskState.Failed) return;

        if (_machine.TryTransition(ScanTaskState.Cancelling))
        {
            NotifyState();
            Report(force: true);
        }
        try { _linkedCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private ScanResult Run()
    {
        var displayName = GetDisplayName(_rootPath);
        _root = new ScanNode(displayName, _rootPath, NodeKind.Directory);

        try
        {
            VerifyRootAvailable();
            if (!_machine.TryTransition(ScanTaskState.Running))
            {
                // 在 Preparing 阶段就被取消
                _machine.TryTransition(ScanTaskState.Cancelled);
                NotifyState();
                return BuildResult(ScanCompleteness.Cancelled, Text("Scan.Cancelled.Partial"));
            }
            NotifyState();

            if (_mode == ScanMode.FastThenFull)
            {
                _phase = Text("Scan.Phase.Quick");
                ScanDirectory(_rootPath, 0, _shallowDepth, _root);
                Report(force: true);

                // 第二阶段开始前冻结浅层结果：取消时回退到这份“已完成”的数据，而不是空树。
                _shallowRoot = _root;
                _shallowStats = BuildStats(ScanCompleteness.Partial, null);

                ResetCounters();
                _phase = Text("Scan.Phase.Full");
                _root = new ScanNode(displayName, _rootPath, NodeKind.Directory);
                ScanDirectory(_rootPath, 0, int.MaxValue, _root);
            }
            else
            {
                _phase = _mode == ScanMode.Shallow
                    ? string.Format(Text("Scan.Phase.Shallow"), _shallowDepth)
                    : Text("Scan.Phase.FullRecursive");
                ScanDirectory(_rootPath, 0, _mode == ScanMode.Shallow ? _shallowDepth : int.MaxValue, _root);
            }

            _machine.Transition(ScanTaskState.Completed);
            NotifyState();
            Report(force: true);
            return BuildResult(_root.Completeness, null);
        }
        catch (OperationCanceledException)
        {
            if (_machine.TryTransition(ScanTaskState.Cancelling)) NotifyState();
            _machine.TryTransition(ScanTaskState.Cancelled);
            NotifyState();
            Report(force: true);

            if (_shallowRoot is not null && !ReferenceEquals(_shallowRoot, _root))
            {
                var stats = _shallowStats! with
                {
                    State = ScanTaskState.Cancelled,
                    Completeness = ScanCompleteness.Cancelled,
                    Elapsed = _clock.Elapsed,
                    Message = Text("Scan.Cancelled.Shallow")
                };
                return new ScanResult(_shallowRoot, stats);
            }

            return BuildResult(ScanCompleteness.Cancelled, Text("Scan.Cancelled.Partial"));
        }
        catch (ScanAbortedException ex)
        {
            if (_machine.TryTransition(ScanTaskState.Failed)) NotifyState();
            Report(force: true);
            return BuildResult(ScanCompleteness.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            if (_machine.TryTransition(ScanTaskState.Failed)) NotifyState();
            Report(force: true);
            return BuildResult(ScanCompleteness.Failed,
                string.Format(Text("Scan.FailedDetail"), ex.Message));
        }
        finally
        {
            _linkedCts?.Dispose();
            _linkedCts = null;
        }
    }

    private void VerifyRootAvailable()
    {
        var pathRoot = Path.GetPathRoot(_rootPath);
        if (!string.IsNullOrEmpty(pathRoot))
        {
            try
            {
                var drive = new DriveInfo(pathRoot);
                if (!drive.IsReady)
                    throw new ScanAbortedException(ScanErrorKind.DiskUnavailable,
                        string.Format(Text("Scan.Error.DeviceNotReady"), pathRoot));
            }
            catch (ScanAbortedException) { throw; }
            catch (Exception ex) when (ScanErrorClassifier.IsExpected(ex))
            {
                throw new ScanAbortedException(ScanErrorKind.DiskUnavailable,
                    string.Format(Text("Scan.Error.DeviceAccess"), pathRoot), ex);
            }
        }

        if (!Directory.Exists(_rootPath))
            throw new ScanAbortedException(ScanErrorKind.PathNotFound,
                string.Format(Text("Scan.Error.RootMissing"), _rootPath));
    }

    private sealed class SmallFileBucket
    {
        public int Count;
        public long Bytes;
        public DateTime LastWriteUtc;
    }

    private void ScanDirectory(string path, int depth, int maxDepth, ScanNode node)
    {
        Checkpoint();
        node.IsScanned = true;
        _directories++;
        var small = new SmallFileBucket();

        var info = new DirectoryInfo(path);
        try { node.LastWriteUtc = info.LastWriteTimeUtc; }
        catch (Exception ex) when (ScanErrorClassifier.IsExpected(ex)) { }

        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
                MatchType = MatchType.Simple
            };

            foreach (var entry in info.EnumerateFileSystemInfos("*", options))
            {
                Checkpoint();
                try
                {
                    ScanEntry(entry, depth, maxDepth, node, small);
                }
                catch (Exception ex) when (ScanErrorClassifier.IsExpected(ex))
                {
                    var kind = ScanErrorClassifier.Classify(ex);
                    if (kind == ScanErrorKind.DiskUnavailable)
                        throw new ScanAbortedException(kind, Text("Scan.Error.DeviceGone"), ex);
                    RecordError(node, kind);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _denied++;
            node.AccessDeniedCount++;
            node.Completeness = ScanCompleteness.Partial;
        }
        catch (DirectoryNotFoundException)
        {
            _errors++;
            _changed = true;
            node.ErrorCount++;
            node.Completeness = ScanCompleteness.Partial;
        }
        catch (IOException ex)
        {
            var kind = ScanErrorClassifier.Classify(ex);
            if (kind == ScanErrorKind.DiskUnavailable)
                throw new ScanAbortedException(kind, Text("Scan.Error.DeviceGone"), ex);
            RecordError(node, kind);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            RecordError(node, ScanErrorClassifier.Classify(ex));
        }
        finally
        {
            if (small.Count > 0)
            {
                node.AddChild(new ScanNode(string.Format(Text("Scan.AggregateName"), small.Count.ToString("N0")), string.Empty, NodeKind.File)
                {
                    LogicalSize = small.Bytes,
                    LastWriteUtc = small.LastWriteUtc,
                    IsAggregate = true,
                    AggregatedCount = small.Count,
                    Category = NodeCategory.Other
                });
            }
            node.LogicalSize = node.Children.Sum(c => c.LogicalSize);
        }
    }

    private void ScanEntry(FileSystemInfo entry, int depth, int maxDepth, ScanNode parent, SmallFileBucket small)
    {
        var attributes = entry.Attributes;

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var reparse = new ScanNode(entry.Name, entry.FullName, NodeKind.ReparsePoint)
            {
                LastWriteUtc = SafeLastWrite(entry),
                Category = NodeCategoryClassifier.Classify(entry.Name, entry.FullName)
            };
            parent.AddChild(reparse);
            _reparse++;
            parent.SkippedReparseCount++;
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (depth + 1 >= maxDepth)
            {
                // 浅层边界：记录目录但不算大小，UI 显示 —
                var boundary = new ScanNode(entry.Name, entry.FullName, NodeKind.Directory)
                {
                    IsScanned = false,
                    Completeness = ScanCompleteness.Partial,
                    LastWriteUtc = SafeLastWrite(entry),
                    Category = NodeCategoryClassifier.Classify(entry.Name, entry.FullName)
                };
                parent.AddChild(boundary);
                parent.Completeness = ScanCompleteness.Partial;
                return;
            }

            var child = new ScanNode(entry.Name, entry.FullName, NodeKind.Directory)
            {
                LastWriteUtc = SafeLastWrite(entry),
                Category = NodeCategoryClassifier.Classify(entry.Name, entry.FullName)
            };
            parent.AddChild(child);
            ScanDirectory(entry.FullName, depth + 1, maxDepth, child);

            parent.AccessDeniedCount += child.AccessDeniedCount;
            parent.ErrorCount += child.ErrorCount;
            parent.SkippedReparseCount += child.SkippedReparseCount;
            if (child.Completeness != ScanCompleteness.Complete)
                parent.Completeness = ScanCompleteness.Partial;
            return;
        }

        if (entry is FileInfo fileInfo)
        {
            var length = fileInfo.Length;
            var lastWrite = SafeLastWrite(fileInfo);
            _files++;
            _bytes += length;
            if ((attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
                _approximate = true;

            // 内存剪枝（§2.6）：小文件不单独建节点，按目录合并为「其他 N 个文件」。
            if (length < AggregateThresholdBytes)
            {
                small.Count++;
                small.Bytes += length;
                if (lastWrite > small.LastWriteUtc) small.LastWriteUtc = lastWrite;
                return;
            }

            parent.AddChild(new ScanNode(fileInfo.Name, fileInfo.FullName, NodeKind.File)
            {
                LogicalSize = length,
                LastWriteUtc = lastWrite,
                IsPlaceholder = (attributes & FileAttributes.Offline) != 0,
                Category = NodeCategoryClassifier.Classify(fileInfo.Name, fileInfo.FullName)
            });
        }
    }

    private void RecordError(ScanNode node, ScanErrorKind kind)
    {
        if (kind == ScanErrorKind.AccessDenied)
        {
            _denied++;
            node.AccessDeniedCount++;
        }
        else
        {
            _errors++;
            node.ErrorCount++;
        }

        if (kind is ScanErrorKind.PathNotFound or ScanErrorKind.SharingViolation)
            _changed = true;

        node.Completeness = ScanCompleteness.Partial;
    }

    private void Checkpoint()
    {
        _token.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _pauseRequested) == 1)
        {
            if (_machine.TryTransition(ScanTaskState.Paused))
            {
                NotifyState();
                Report(force: true);
            }
            _resumeGate.Wait(_token);
            _resumeGate.Reset();
            Interlocked.Exchange(ref _pauseRequested, 0);
            if (_machine.TryTransition(ScanTaskState.Running)) NotifyState();
            Report(force: true);
            return;
        }

        Report(force: false);
    }

    private void Report(bool force)
    {
        if (_progress is null) return;
        var now = _clock.ElapsedMilliseconds;
        if (!force && now - _lastReportMs < ReportIntervalMs) return;
        _lastReportMs = now;
        _progress.Report(BuildStats(ScanCompleteness.Partial, null));
    }

    private ScanStats BuildStats(ScanCompleteness completeness, string? message)
    {
        var root = _root;
        return new ScanStats(
            _rootPath,
            _depthUsed,
            _files,
            _directories,
            _bytes,
            (int)_denied,
            (int)_reparse,
            (int)_errors,
            _machine.State,
            completeness,
            _approximate ? SizePrecision.Approximate : SizePrecision.Exact,
            _changed ? ObjectChangeState.ChangedDuringScan : ObjectChangeState.Stable,
            _clock.Elapsed,
            _phase,
            message);
    }

    private ScanResult BuildResult(ScanCompleteness completeness, string? message)
    {
        var root = _root ?? new ScanNode(GetDisplayName(_rootPath), _rootPath, NodeKind.Directory);
        AssignDirectoryCategories(root);
        return new ScanResult(root, BuildStats(completeness, message));
    }

    /// <summary>目录按占用最大的子项归类，用于“按类型”配色；不改变任何大小口径。</summary>
    private static void AssignDirectoryCategories(ScanNode node)
    {
        if (node.Kind != NodeKind.Directory) return;

        var byCategory = new long[Enum.GetValues<NodeCategory>().Length];
        foreach (var child in node.Children)
        {
            AssignDirectoryCategories(child);
            byCategory[(int)child.Category] += Math.Max(0, child.LogicalSize);
        }

        if (node.Children.Count == 0) return;
        var best = 0;
        for (var i = 1; i < byCategory.Length; i++)
        {
            if (byCategory[i] > byCategory[best]) best = i;
        }
        node.Category = (NodeCategory)best;
    }

    private void ResetCounters()
    {
        _files = 0;
        _directories = 0;
        _bytes = 0;
        _denied = 0;
        _reparse = 0;
        _errors = 0;
        _lastReportMs = 0;
        _approximate = false;
        _changed = false;
    }

    private void NotifyState() => StateChanged?.Invoke(_machine.State);

    private static DateTime SafeLastWrite(FileSystemInfo info)
    {
        try { return info.LastWriteTimeUtc; }
        catch (Exception ex) when (ScanErrorClassifier.IsExpected(ex)) { return default; }
    }

    private static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
