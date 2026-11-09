using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>
/// 清理安全链路：冻结计划 → 执行前校验哈希 → 逐项身份校验 → 回收站 / 永久删除。
/// 回收站失败时只报告失败，绝不静默改为永久删除。
/// </summary>
public sealed class DeleteService
{
    private readonly ProtectionEvaluator _protection = new();
    private readonly HashSet<Guid> _executedPlans = new();
    private readonly string _ledgerPath;

    public DeleteService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerFile)!);
        _ledgerPath = LedgerFile;
        RepairLedgerTail(_ledgerPath);
    }

    /// <summary>清理台账路径；诊断导出与构造实例共用。</summary>
    public static string LedgerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSpaceGuard", "cleanup-log.jsonl");

    /// <summary>崩溃后可能留下半行 JSON：启动时截断到最后一个换行，避免台账无法解析。</summary>
    internal static int RepairLedgerTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length == 0) return 0;

            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() == '\n') return 0;

            var removed = 0;
            long cut = 0;
            for (var pos = stream.Length - 1; pos >= 0; pos--)
            {
                stream.Seek(pos, SeekOrigin.Begin);
                if (stream.ReadByte() == '\n') { cut = pos + 1; break; }
                removed++;
            }
            stream.SetLength(cut);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Ledger repair failed: {ex}");
            return 0;
        }
    }

    public ProtectionEvaluator Protection => _protection;

    public string LedgerPath => _ledgerPath;

    private static string Text(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private const int MaxPlanItems = 50_000;

    /// <summary>递归目录树的深度上限；防止极深目录导致 StackOverflow（不可捕获）。</summary>
    private const int MaxTreeDepth = 256;

    /// <summary>默认只接受文件；目录递归需显式开启（T18）。</summary>
    public DeletePlan BuildPlan(IReadOnlyList<ScanNode> nodes, DeleteMode mode, string rootHint)
        => BuildPlan(nodes, mode, rootHint, recursive: false);

    /// <summary>生成计划；recursive=true 时把选中目录按磁盘现状自底向上展开。</summary>
    public DeletePlan BuildPlan(IReadOnlyList<ScanNode> nodes, DeleteMode mode, string rootHint, bool recursive)
    {
        var plan = TryBuildPlan(nodes, mode, rootHint, recursive, out _, out var error);
        if (plan is null) throw new InvalidOperationException(error);
        return plan;
    }

    /// <summary>
    /// §3.2.1：递归清理时按磁盘现状枚举计划内子项；计划外新增项不会被删除。
    /// 返回 null 表示未能生成计划（超限 / 被取消）。
    /// </summary>
    public DeletePlan? TryBuildPlan(
        IReadOnlyList<ScanNode> nodes,
        DeleteMode mode,
        string rootHint,
        bool recursive,
        out PlanBuildReport report,
        out string error,
        CancellationToken token = default)
    {
        var items = new List<DeleteItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedRoots = new List<string>();
        var id = 0;
        var files = 0;
        var dirs = 0;
        var reparse = 0;
        var unreadable = 0;
        error = string.Empty;

        try
        {
            foreach (var node in nodes)
            {
                if (string.IsNullOrEmpty(node.FullPath) || node.IsAggregate) continue;

                if (node.Kind == NodeKind.File)
                {
                    if (TryAddFile(node.FullPath, items, seen, ref id)) files++;
                    continue;
                }

                if (!recursive || node.Kind != NodeKind.Directory || !node.IsScanned) continue;

                var normalized = ProtectionEvaluator.Normalize(node.FullPath);
                // UNC / 设备前缀等不可信路径不得进入递归展开（会触发网络访问）。
                if (normalized.Length == 0) continue;
                // 父目录已被选中时不再重复展开子目录。
                if (selectedRoots.Any(r => ProtectionEvaluator.IsUnder(normalized, r))) continue;

                var info = new DirectoryInfo(node.FullPath);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reparse++;
                    continue;
                }

                selectedRoots.Add(normalized);
                WalkDirectory(node.FullPath, items, seen, ref id, ref files, ref dirs,
                    ref reparse, ref unreadable, 0, token);

                var dirItem = CreateDirectoryItem(node.FullPath, ref id);
                if (dirItem is not null && seen.Add(dirItem.NormalizedPath))
                {
                    items.Add(dirItem);
                    dirs++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            report = new PlanBuildReport(files, dirs, reparse, unreadable);
            error = LocalizationService.Text("Plan.Cancelled", Loc.Instance.Language);
            return null;
        }
        catch (PlanTooLargeException)
        {
            report = new PlanBuildReport(files, dirs, reparse, unreadable);
            error = string.Format(
                LocalizationService.Text("Plan.TooLarge", Loc.Instance.Language),
                MaxPlanItems.ToString("N0"));
            return null;
        }

        report = new PlanBuildReport(files, dirs, reparse, unreadable);

        var hash = PlanHasher.Compute(PlanHasher.SchemaVersion, PlanHasher.PlanVersion, rootHint, mode, items);
        return new DeletePlan(
            Guid.NewGuid(),
            DateTime.UtcNow,
            PlanHasher.SchemaVersion,
            PlanHasher.PlanVersion,
            rootHint,
            mode,
            items,
            items.Sum(i => i.SnapshotSize),
            hash);
    }

    private void WalkDirectory(
        string dir,
        List<DeleteItem> items,
        HashSet<string> seen,
        ref int id,
        ref int files,
        ref int dirs,
        ref int reparse,
        ref int unreadable,
        int depth,
        CancellationToken token)
    {
        if (depth > MaxTreeDepth)
        {
            unreadable++;
            return;
        }

        token.ThrowIfCancellationRequested();

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(dir).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable++;
            return;
        }

        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (items.Count >= MaxPlanItems) throw new PlanTooLargeException();

            FileAttributes attributes;
            try { attributes = entry.Attributes; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }

            // 重解析点不穿透、不入计划；父目录因此非空时会在执行阶段如实失败。
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                reparse++;
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                WalkDirectory(entry.FullName, items, seen, ref id, ref files, ref dirs,
                    ref reparse, ref unreadable, depth + 1, token);
                var dirItem = CreateDirectoryItem(entry.FullName, ref id);
                if (dirItem is not null && seen.Add(dirItem.NormalizedPath))
                {
                    items.Add(dirItem);
                    dirs++;
                }
            }
            else if (TryAddFile(entry.FullName, items, seen, ref id))
            {
                files++;
            }
        }
    }

    private bool TryAddFile(string path, List<DeleteItem> items, HashSet<string> seen, ref int id)
    {
        var normalized = ProtectionEvaluator.Normalize(path);
        // 无法规范化的路径（UNC / 设备前缀 / ADS）一律不进入删除计划。
        if (normalized.Length == 0 || seen.Contains(normalized)) return false;

        FileAttributes attributes;
        long size;
        DateTime lastWrite;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            attributes = info.Attributes;
            size = info.Length;
            lastWrite = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // 删除计划同时核对身份与最终落点：字面路径经 junction 祖先可达 L0 系统目录。
        var hasIdentity = FileIdentityService.TryReadIdentityWithFinalPath(
            path, out var identity, out var finalPath, out _);
        ProtectionLevel level;
        string reasonKey;
        if (hasIdentity)
            level = _protection.EvaluateWithFinalPath(path, attributes, finalPath, out reasonKey);
        else
            level = _protection.Evaluate(path, attributes, out reasonKey);

        seen.Add(normalized);
        items.Add(new DeleteItem(
            ++id,
            path,
            normalized,
            size,
            lastWrite,
            hasIdentity ? identity.VolumeSerial : 0,
            hasIdentity ? identity.ToPlanString() : string.Empty,
            level,
            reasonKey));
        return true;
    }

    private DeleteItem? CreateDirectoryItem(string path, ref int id)
    {
        var normalized = ProtectionEvaluator.Normalize(path);
        if (normalized.Length == 0) return null;

        FileAttributes attributes;
        DateTime lastWrite;
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return null;
            attributes = info.Attributes;
            lastWrite = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hasIdentity = FileIdentityService.TryReadIdentityWithFinalPath(
            path, out var identity, out var finalPath, out _);
        ProtectionLevel level;
        string reasonKey;
        if (hasIdentity)
            level = _protection.EvaluateWithFinalPath(path, attributes, finalPath, out reasonKey);
        else
            level = _protection.Evaluate(path, attributes, out reasonKey);

        return new DeleteItem(
            ++id,
            path,
            normalized,
            0,
            lastWrite,
            hasIdentity ? identity.VolumeSerial : 0,
            hasIdentity ? identity.ToPlanString() : string.Empty,
            level,
            reasonKey,
            DeleteItemKind.Directory);
    }

    /// <summary>计划条目过多时中止生成，避免把不可审视的巨量计划交给用户。</summary>
    private sealed class PlanTooLargeException : Exception
    {
    }

    /// <summary>预演：只统计，不删除。</summary>
    public DeletePreview Preview(DeletePlan plan)
    {
        var blocked = plan.Items.Count(i => i.Protection == ProtectionLevel.L0);
        var reparse = plan.Items.Count(i => i.Protection == ProtectionLevel.L1);
        var strong = plan.Items.Count(i => i.Protection == ProtectionLevel.L2);
        var custom = plan.Items.Count(i => i.Protection == ProtectionLevel.L4);
        var deletable = plan.Items
            .Where(i => i.Protection is ProtectionLevel.L3 or ProtectionLevel.L2
                        && !string.IsNullOrEmpty(i.FileId))
            .ToList();

        var missing = 0;
        var changed = 0;
        var unverifiable = plan.Items.Count(i => string.IsNullOrEmpty(i.FileId));
        foreach (var item in deletable)
        {
            try
            {
                if (item.Kind == DeleteItemKind.Directory)
                {
                    if (!Directory.Exists(item.NormalizedPath)) missing++;
                    continue;
                }

                var info = new FileInfo(item.NormalizedPath);
                if (!info.Exists) { missing++; continue; }
                if (info.Length != item.SnapshotSize) changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missing++;
            }
        }

        return new DeletePreview(
            plan.Items.Count,
            deletable.Count,
            plan.EstimatedLogicalBytes,
            deletable.Sum(i => i.SnapshotSize),
            blocked,
            reparse,
            custom,
            strong,
            missing,
            changed,
            unverifiable,
            plan.Items.Count(i => i.Kind == DeleteItemKind.Directory));
    }

    public Task<DeleteResult> ExecuteAsync(
        DeletePlan plan,
        DeleteRunState state,
        IProgress<DeleteItemResult>? progress,
        CancellationToken token)
    {
        return Task.Run(() =>
        {
            // 大计划逐项 Report 会淹没 UI；合并为 100ms 批量上报，收尾补发终值。
            var throttled = progress is null ? null
                : new ThrottledProgress<DeleteItemResult>(progress, 100);
            // 崩溃 / 断电时保留「进行中计划」，重启后可提示哪些项结果未知（不改主台账格式）。
            ActivePlanFile.Write(plan);
            try { return ExecuteCore(plan, state, throttled, token); }
            finally
            {
                throttled?.Flush();
                ActivePlanFile.Clear(plan.PlanId);
            }
        }, CancellationToken.None);
    }

    private DeleteResult ExecuteCore(
        DeletePlan plan,
        DeleteRunState state,
        IProgress<DeleteItemResult>? progress,
        CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<DeleteItemResult>();
        string? fatal = null;
        var freeBefore = ReadFreeBytes(plan);

        // 版本兼容（T21）：不读取计划里的版本号就等于放弃校验。
        if (!string.Equals(plan.SchemaVersion, PlanHasher.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(plan.PlanVersion, PlanHasher.PlanVersion, StringComparison.Ordinal))
        {
            fatal = string.Format(Text("Exec.Fatal.Version"), plan.SchemaVersion, plan.PlanVersion);
            return new DeleteResult(plan.PlanId, plan.Mode, plan.Items.Count, 0, 0, 0,
                plan.Items.Count, plan.EstimatedLogicalBytes, 0, clock.Elapsed, fatal);
        }

        // 计划完整性：用计划自身的版本字段重算哈希，必须与计划自带哈希一致（T20）。
        var recomputed = PlanHasher.Compute(plan.SchemaVersion, plan.PlanVersion,
            plan.RootHint, plan.Mode, plan.Items);
        state.PlanHashVerified = string.Equals(recomputed, plan.PlanHash, StringComparison.OrdinalIgnoreCase);
        if (!state.PlanHashVerified)
        {
            fatal = Text("Exec.Fatal.Hash");
            return new DeleteResult(plan.PlanId, plan.Mode, plan.Items.Count, 0, 0, 0,
                plan.Items.Count, plan.EstimatedLogicalBytes, 0, clock.Elapsed, fatal);
        }

        // 重复提交（T22）
        if (_executedPlans.Contains(plan.PlanId))
        {
            fatal = Text("Exec.Fatal.Duplicate");
            return new DeleteResult(plan.PlanId, plan.Mode, plan.Items.Count, 0, 0, 0,
                plan.Items.Count, plan.EstimatedLogicalBytes, 0, clock.Elapsed, fatal);
        }
        _executedPlans.Add(plan.PlanId);

        long deletedBytes = 0;
        var deleted = 0;
        var recycled = 0;
        var skipped = 0;
        var failed = 0;
        var volumeUnavailable = false;
        var plannedPaths = new HashSet<string>(
            plan.Items.Select(i => i.NormalizedPath), StringComparer.OrdinalIgnoreCase);
        // 已成功交给 Shell 的条目，执行结束后统一到回收站复核一次。
        var recycleCandidates = new List<(DeleteItemResult Result, string Path)>();
        // 逐条落盘：崩溃/断电时已完成的条目仍有台账可查。
        using var ledgerWriter = TryOpenLedgerWriter();

        // 统一出口：逐项结果（含各类跳过）都立即上报，保证“已处理 X/Y”进度不缺计数。
        void Emit(DeleteItemResult r)
        {
            results.Add(r);
            progress?.Report(r);
            WriteItemRecord(ledgerWriter, plan.PlanId, r);
        }

        try
        {
            foreach (var item in plan.Items)
            {
                token.ThrowIfCancellationRequested();

                if (item.Protection == ProtectionLevel.L0)
                {
                    Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                        ItemOutcome.Skipped, VerifyState.Blocked, Text("Exec.Skip.L0")));
                    skipped++;
                    state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                    continue;
                }

                if (item.Protection is ProtectionLevel.L1 or ProtectionLevel.L4)
                {
                    Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                        ItemOutcome.Skipped, VerifyState.Blocked,
                        Text(item.Protection == ProtectionLevel.L1 ? "Exec.Skip.Reparse" : "Exec.Skip.Custom")));
                    skipped++;
                    state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                    continue;
                }

                // 身份不可读的条目无法证明对象未变，一律跳过而不是按路径删除。
                if (string.IsNullOrEmpty(item.FileId))
                {
                    Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                        ItemOutcome.Skipped, VerifyState.Unverifiable,
                        Text("Exec.Skip.Unverifiable")));
                    skipped++;
                    state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                    state.Verify[item.ItemId] = VerifyState.Unverifiable;
                    continue;
                }

                // §3.2.1：目录只能在计划内子项全部删除后删除；计划外项一律保留。
                if (item.Kind == DeleteItemKind.Directory)
                {
                    var (unexpected, remaining, missingDir, inspectError) =
                        InspectDirectory(item.NormalizedPath, plannedPaths);

                    if (missingDir)
                    {
                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Skipped, VerifyState.Missing, Text("Exec.Skip.DirMissing")));
                        skipped++;
                        state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                        continue;
                    }

                    if (!string.IsNullOrEmpty(inspectError))
                    {
                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Failed, VerifyState.Unverifiable,
                            string.Format(Text("Exec.Fail.DirInspect"), inspectError)));
                        failed++;
                        state.Outcome[item.ItemId] = ItemOutcome.Failed;
                        continue;
                    }

                    if (unexpected > 0)
                    {
                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Skipped, VerifyState.Blocked,
                            string.Format(Text("Exec.Skip.DirUnexpected"), unexpected)));
                        skipped++;
                        state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                        continue;
                    }

                    if (remaining > 0)
                    {
                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Failed, VerifyState.Changed,
                            string.Format(Text("Exec.Fail.DirRemaining"), remaining)));
                        failed++;
                        state.Outcome[item.ItemId] = ItemOutcome.Failed;
                        continue;
                    }
                }

                DeleteItemResult result;
                if (plan.Mode == DeleteMode.Permanent)
                {
                    var (outcome, message) = FileIdentityService.VerifyAndDelete(
                        item.NormalizedPath, item.SnapshotSize, item.VolumeSerial, item.FileId,
                        ignoreReadOnly: false, isDirectory: item.Kind == DeleteItemKind.Directory,
                        expectedLastWriteUtc: item.SnapshotLastWriteUtc);

                    switch (outcome)
                    {
                        case FileIdentityService.VerifyOutcome.Matched:
                            result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                                ItemOutcome.Deleted, VerifyState.Matched);
                            deleted++;
                            deletedBytes += item.SnapshotSize;
                            break;
                        case FileIdentityService.VerifyOutcome.Changed:
                            result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                                ItemOutcome.Skipped, VerifyState.Changed, message);
                            skipped++;
                            break;
                        case FileIdentityService.VerifyOutcome.Missing:
                            result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                                ItemOutcome.Skipped, VerifyState.Missing, message);
                            skipped++;
                            break;
                        case FileIdentityService.VerifyOutcome.VolumeUnavailable:
                            // 卷已断开/重新挂载：中止整份计划，避免在失效路径上继续操作。
                            result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                                ItemOutcome.Failed, VerifyState.Unverifiable, message);
                            failed++;
                            volumeUnavailable = true;
                            break;
                        default:
                            result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                                ItemOutcome.Failed, VerifyState.Unverifiable, message);
                            failed++;
                            break;
                    }
                }
                else
                {
                    // 回收站只能按路径操作：先校验身份，再交给 Shell；失败不降级（T24）。
                    FileIdentityService.TryReadIdentity(item.NormalizedPath, out var current, out var identityError);
                    if (!current.IsValid)
                    {
                        if (!IsVolumeReady(item.NormalizedPath))
                        {
                            Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                                ItemOutcome.Failed, VerifyState.Unverifiable,
                                Text("Exec.Fatal.VolumeChanged")));
                            failed++;
                            fatal = Text("Exec.Fatal.VolumeChanged");
                            break;
                        }

                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Skipped, VerifyState.Missing, identityError));
                        skipped++;
                        state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                        continue;
                    }

                    if (!string.Equals(current.ToPlanString(), item.FileId, StringComparison.OrdinalIgnoreCase))
                    {
                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Skipped, VerifyState.Changed, Text("Exec.Skip.Changed")));
                        skipped++;
                        state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                        continue;
                    }

                    // 回收站模式按路径操作，无法绑定句柄；至少复核时间戳，
                    // 避免预演后内容被就地覆写（同 FileId、同尺寸）仍被移入回收站。
                    var currentWrite = File.GetLastWriteTimeUtc(item.NormalizedPath);
                    if (Math.Abs((currentWrite - item.SnapshotLastWriteUtc).TotalSeconds) > 2)
                    {
                        Emit(new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Skipped, VerifyState.Changed, Text("Exec.Skip.Changed")));
                        skipped++;
                        state.Outcome[item.ItemId] = ItemOutcome.Skipped;
                        continue;
                    }

                    if (RecycleBinService.TryMoveToRecycleBin(item.NormalizedPath, out var recycleError))
                    {
                        result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.MovedToRecycleBin, VerifyState.Matched, Text("Exec.Recycled"));
                        recycled++;
                        recycleCandidates.Add((result, item.NormalizedPath));
                    }
                    else
                    {
                        result = new DeleteItemResult(item.ItemId, item.DisplayPath,
                            ItemOutcome.Failed, VerifyState.Unverifiable,
                            recycleError + Text("Exec.RecycleFailedSuffix"));
                        failed++;
                    }
                }

                state.Verify[item.ItemId] = result.Verify;
                state.Outcome[item.ItemId] = result.Outcome;
                if (!string.IsNullOrEmpty(result.Message)) state.Messages[item.ItemId] = result.Message;
                Emit(result);

                if (volumeUnavailable)
                {
                    fatal = Text("Exec.Fatal.VolumeChanged");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            fatal = Text("Exec.Fatal.Cancelled");
        }
        catch (Exception ex)
        {
            fatal = string.Format(Text("Exec.Fatal.Interrupted"), ex.Message);
        }

        // 回收站模式只按路径操作：移动成功后必须到回收站复核。
        // 无法在回收站中找到 → 不得记成功（可能被 Shell 永久删除或移动了错误对象）。
        VerifyRecycleCandidates(plan, results, recycleCandidates, state, progress, ledgerWriter,
            ref recycled, ref failed);

        clock.Stop();
        var summary = new DeleteResult(plan.PlanId, plan.Mode, plan.Items.Count, deleted, recycled,
            skipped, failed, plan.EstimatedLogicalBytes, deletedBytes, clock.Elapsed, fatal,
            freeBefore, ReadFreeBytes(plan));

        WriteLedger(plan, summary, results, ledgerWriter);
        return summary;
    }

    private static void VerifyRecycleCandidates(
        DeletePlan plan,
        List<DeleteItemResult> results,
        IReadOnlyList<(DeleteItemResult Result, string Path)> candidates,
        DeleteRunState state,
        IProgress<DeleteItemResult>? progress,
        StreamWriter? ledgerWriter,
        ref int recycled,
        ref int failed)
    {
        if (plan.Mode != DeleteMode.RecycleFirst || candidates.Count == 0) return;

        // 复核失败（COM 不可用）不能让已完成的删除结果失真，但必须标注“无法复核”。
        // Shell 对回收站条目的可见性可能略滞后，最多重试 3 次再下结论。
        HashSet<string>? verified = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                verified = RecycleBinRestoreService.TryCollectRecycledPaths(
                    candidates.Select(c => c.Path), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Recycle verification failed: {ex.Message}");
                verified = null;
            }

            if (verified is null) break;
            if (candidates.All(c => verified.Contains(c.Path))) break;
            if (attempt < 2) Thread.Sleep(150);
        }

        foreach (var (original, path) in candidates)
        {
            var index = results.FindIndex(r => r.ItemId == original.ItemId);
            if (index < 0) continue;

            if (verified is null)
            {
                var unverified = original with
                {
                    Verify = VerifyState.Unverifiable,
                    Message = Text("Exec.RecycleVerifyUnavailable")
                };
                results[index] = unverified;
                state.Verify[unverified.ItemId] = VerifyState.Unverifiable;
                state.Messages[unverified.ItemId] = unverified.Message;
                WriteItemRecord(ledgerWriter, plan.PlanId, unverified);
                continue;
            }

            if (verified.Contains(path)) continue;

            var corrected = original with
            {
                Outcome = ItemOutcome.Failed,
                Verify = VerifyState.Unverifiable,
                Message = Text("Exec.RecycleUnverified")
            };
            results[index] = corrected;
            state.Outcome[corrected.ItemId] = ItemOutcome.Failed;
            state.Verify[corrected.ItemId] = VerifyState.Unverifiable;
            state.Messages[corrected.ItemId] = corrected.Message;
            recycled--;
            failed++;
            WriteItemRecord(ledgerWriter, plan.PlanId, corrected);
            progress?.Report(corrected);
        }
    }

    /// <summary>卷是否仍可用；无法判定时返回 true，交给逐项校验处理。</summary>
    private static bool IsVolumeReady(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) || new DriveInfo(root).IsReady;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>目录删除前检查直接子项：是否都是计划内对象、是否都已删除。</summary>
    private static (int Unexpected, int Remaining, bool Missing, string Error) InspectDirectory(
        string path, HashSet<string> plannedPaths)
    {
        if (!Directory.Exists(path)) return (0, 0, true, string.Empty);

        var unexpected = 0;
        var remaining = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                var normalized = ProtectionEvaluator.Normalize(entry.FullName);
                if (plannedPaths.Contains(normalized)) remaining++;
                else unexpected++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(path)) return (0, 0, true, string.Empty);
            return (0, 0, false, ex.Message);
        }

        return (unexpected, remaining, false, string.Empty);
    }

    /// <summary>§14.3 空间复核：读取目标卷的真实可用空间（TotalFreeSpace，不受配额影响）。</summary>
    private static long? ReadFreeBytes(DeletePlan plan)
    {
        try
        {
            var sample = plan.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.DisplayPath))?.DisplayPath;
            var root = string.IsNullOrEmpty(sample) ? null : Path.GetPathRoot(sample);
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).TotalFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>以追加方式打开台账，供执行期逐条写入；失败时返回 null，不影响删除本身。</summary>
    private StreamWriter? TryOpenLedgerWriter()
    {
        try
        {
            var dir = Path.GetDirectoryName(_ledgerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return new StreamWriter(
                new FileStream(_ledgerPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Ledger open failed: {ex}");
            return null;
        }
    }

    /// <summary>逐条结果先落盘；崩溃后即使没有汇总行，也能还原已处理的条目。</summary>
    private static void WriteItemRecord(StreamWriter? writer, Guid planId, DeleteItemResult result)
    {
        if (writer is null) return;
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                schema = PlanHasher.SchemaVersion,
                recordType = "item",
                planId,
                executedUtc = DateTime.UtcNow,
                itemId = result.ItemId,
                path = result.Path,
                outcome = result.Outcome.ToString(),
                verify = result.Verify.ToString(),
                message = result.Message
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Debug.WriteLine($"Ledger item write failed: {ex}");
        }
    }

    private void WriteLedger(DeletePlan plan, DeleteResult summary, IReadOnlyList<DeleteItemResult> results,
        StreamWriter? writer)
    {
        try
        {
            // 台账保存对象身份，供还原时核对“还原的是不是同一个对象”。
            var identities = plan.Items.ToDictionary(i => i.ItemId, i => (i.FileId, i.VolumeSerial));
            var record = new
            {
                schema = PlanHasher.SchemaVersion,
                planId = plan.PlanId,
                createdUtc = plan.CreatedUtc,
                executedUtc = DateTime.UtcNow,
                mode = plan.Mode.ToString(),
                planHash = plan.PlanHash,
                total = summary.Total,
                deleted = summary.Deleted,
                recyclePending = summary.RecyclePending,
                skipped = summary.Skipped,
                failed = summary.Failed,
                estimatedBytes = summary.EstimatedBytes,
                actualBytes = summary.ActualBytes,
                fatal = summary.FatalMessage,
                items = results.Select(r =>
                {
                    identities.TryGetValue(r.ItemId, out var identity);
                    return new
                    {
                        r.ItemId,
                        path = r.Path,
                        outcome = r.Outcome.ToString(),
                        verify = r.Verify.ToString(),
                        r.Message,
                        fileId = identity.FileId ?? string.Empty,
                        volumeSerial = identity.VolumeSerial
                    };
                }).ToArray()
            };

            var line = JsonSerializer.Serialize(record);
            if (writer is not null)
            {
                try
                {
                    writer.WriteLine(line);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    Debug.WriteLine($"Ledger summary write failed: {ex}");
                }
            }

            File.AppendAllText(_ledgerPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Ledger write failed: {ex}");
        }
    }
}

public sealed record DeletePreview(
    int TotalItems,
    int DeletableItems,
    long PlannedBytes,
    long DeletableBytes,
    int BlockedCount,
    int ReparseCount,
    int CustomProtectedCount,
    int StrongConfirmCount,
    int MissingCount,
    int ChangedCount,
    int UnverifiableCount,
    int DirectoryCount = 0);

/// <summary>计划生成时的展开统计，用于预演提示。</summary>
public sealed record PlanBuildReport(
    int FileCount,
    int DirectoryCount,
    int ReparseSkipped,
    int UnreadableDirectories);

/// <summary>
/// T25 / §14.7：删除开始前留一份「进行中计划」；崩溃或断电后重启仍能提示
/// 「上次执行未完成，以下 N 项结果未知」。正常收尾即删除，主台账 JSONL 格式不变。
/// </summary>
public static class ActivePlanFile
{
    internal static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSpaceGuard", "cleanup-plan-active.json");

    public sealed record Snapshot(Guid PlanId, DateTime StartedUtc, IReadOnlyList<string> Paths);

    /// <summary>进行中快照是同用户可写文件，读写都限长，避免异常文件拖垮内存。</summary>
    private const long MaxSnapshotBytes = 16L * 1024 * 1024;

    public static void Write(DeletePlan plan)
    {
        try
        {
            var snapshot = new Snapshot(plan.PlanId, DateTime.UtcNow,
                plan.Items.Select(i => i.DisplayPath).ToList());
            var json = JsonSerializer.Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes)
            {
                Debug.WriteLine("Active plan snapshot too large; skipped.");
                return;
            }
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cannot write active plan marker: {ex.Message}");
        }
    }

    public static Snapshot? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > MaxSnapshotBytes) return null;
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath),
                new JsonSerializerOptions { MaxDepth = 32 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Clear(Guid planId)
    {
        try
        {
            var current = Read();
            if (current is null || current.PlanId != planId) return;
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cannot clear active plan marker: {ex.Message}");
        }
    }
}
