using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>
/// 共享的进度节流包装（查重与删除链路复用）：把逐项 Report 合并为固定间隔的批量上报，
/// 任务结束时 Flush 补发最后一次，保证进度条不会停在中间值。
/// </summary>
internal sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _inner;
    private readonly long _intervalMs;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastReportMs;
    private bool _hasReported;
    private bool _hasPending;
    private T? _latest;

    public ThrottledProgress(IProgress<T> inner, int intervalMs)
    {
        _inner = inner;
        _intervalMs = Math.Max(1, intervalMs);
    }

    public void Report(T value)
    {
        bool shouldReport;
        lock (_gate)
        {
            _latest = value;
            _hasPending = true;
            var now = _clock.ElapsedMilliseconds;
            shouldReport = !_hasReported || now - _lastReportMs >= _intervalMs;
            if (shouldReport)
            {
                _hasReported = true;
                _lastReportMs = now;
                _hasPending = false;
            }
        }
        if (shouldReport) _inner.Report(value);
    }

    public void Flush()
    {
        T? value = default;
        lock (_gate)
        {
            if (!_hasPending) return;
            _hasPending = false;
            value = _latest;
        }
        if (value is not null) _inner.Report(value);
    }
}

/// <summary>
/// §4 重复文件：大小分组 → 采样哈希筛除 → SHA-256 全量哈希。
/// 删除前必须对候选项重新计算 SHA-256 并复核 FileId / 大小 / 时间戳。
/// </summary>
public sealed class DuplicateFinderService
{
    private const int SampleBytes = 64 * 1024;
    private const int BufferBytes = 1024 * 1024;
    private const uint FileAttributeOffline = 0x1000;
    private const uint FileAttributeRecallOnDataAccess = 0x400000;

    /// <summary>递归目录树的深度上限；防止极深目录导致 StackOverflow（不可捕获）。</summary>
    private const int MaxTreeDepth = 256;

    private readonly ProtectionEvaluator _protection;

    public DuplicateFinderService(ProtectionEvaluator? protection = null)
        => _protection = protection ?? new ProtectionEvaluator();

    public Task<DuplicateScanResult> ScanAsync(
        string root,
        long minSize,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken token)
        => Task.Run(() =>
        {
            // 逐文件进度合并为 180ms 批量上报，避免大批量候选淹没 Dispatcher 队列。
            var throttled = progress is null ? null
                : new ThrottledProgress<DuplicateScanProgress>(progress, 180);
            try { return ScanCore(root, minSize, throttled, token); }
            finally { throttled?.Flush(); }
        }, token);

    private DuplicateScanResult ScanCore(
        string root,
        long minSize,
        IProgress<DuplicateScanProgress>? progress,
        CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var excludedSmall = 0;
        var excludedCloud = 0;
        var excludedProtected = 0;
        var hardLinks = 0;
        var readErrors = 0;
        var unstable = 0;

        var candidates = new List<Candidate>();
        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > MaxTreeDepth)
            {
                readErrors++;
                return;
            }

            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                readErrors++;
                return;
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    readErrors++;
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Walk(entry.FullName, depth + 1);
                    continue;
                }

                if ((attributes & (FileAttributes)FileAttributeOffline) != 0 ||
                    ((uint)attributes & FileAttributeRecallOnDataAccess) != 0)
                {
                    excludedCloud++;
                    continue;
                }

                long size;
                DateTime lastWrite;
                try
                {
                    var info = (FileInfo)entry;
                    size = info.Length;
                    lastWrite = info.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    readErrors++;
                    continue;
                }

                if (size <= 0) { excludedSmall++; continue; }
                if (size < minSize) { excludedSmall++; continue; }

                // L0 与用户自定义 L4 目录不参与查重（L4 一律不可删除）。
                var level = _protection.Evaluate(entry.FullName, attributes, out _);
                if (level is ProtectionLevel.L0 or ProtectionLevel.L4)
                {
                    excludedProtected++;
                    continue;
                }

                if (!FileIdentityService.TryReadIdentity(entry.FullName, out var identity, out _))
                {
                    readErrors++;
                    continue;
                }

                var identityKey = $"{identity.VolumeSerial:X16}:{identity.ToPlanString()}";
                if (!seenIdentities.Add(identityKey))
                {
                    hardLinks++;
                    continue;
                }

                candidates.Add(new Candidate(entry.FullName, size, lastWrite, identity, attributes));
            }
        }

        Walk(root, 0);

        // 阶段 1：按精确大小分组；组内不足 2 个直接淘汰。
        var bySize = candidates.GroupBy(c => c.Size).Where(g => g.Count() > 1).ToList();
        var totalToHash = bySize.Sum(g => g.Count());
        var hashed = 0;

        // 阶段 2：采样哈希仅用于筛除，不能作为重复结论。
        var afterSample = new List<Candidate>();
        foreach (var sizeGroup in bySize)
        {
            var sampled = new List<Candidate>();
            foreach (var candidate in sizeGroup)
            {
                token.ThrowIfCancellationRequested();
                var sample = TryHashSample(candidate.Path, candidate.Size, token);
                if (sample is null)
                {
                    readErrors++;
                    continue;
                }

                sampled.Add(candidate with { SampleHash = sample });
                hashed++;
                Report(progress, "sample", hashed, totalToHash, candidate.Path);
            }

            afterSample.AddRange(sampled.GroupBy(c => c.SampleHash)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g));
        }

        // 阶段 3：SHA-256 全量哈希。
        var byFullHash = new Dictionary<string, List<DuplicateFile>>(StringComparer.OrdinalIgnoreCase);
        hashed = 0;
        var totalFull = afterSample.Count;

        foreach (var candidate in afterSample)
        {
            token.ThrowIfCancellationRequested();
            var before = Snapshot(candidate.Path);
            var hash = TryHashFull(candidate.Path, token);
            if (hash is null)
            {
                readErrors++;
                hashed++;
                continue;
            }
            var after = Snapshot(candidate.Path);

            var isUnstable = before is null || after is null ||
                             before.Size != after.Size ||
                             before.LastWriteUtc != after.LastWriteUtc ||
                             !string.Equals(before.FileId, after.FileId, StringComparison.OrdinalIgnoreCase);
            if (isUnstable) unstable++;

            var key = $"{candidate.Size}:{hash}";
            if (!byFullHash.TryGetValue(key, out var list))
            {
                list = new List<DuplicateFile>();
                byFullHash[key] = list;
            }

            list.Add(new DuplicateFile(
                candidate.Path,
                candidate.Size,
                candidate.LastWriteUtc,
                Path.GetPathRoot(candidate.Path) ?? string.Empty,
                candidate.Identity.ToPlanString(),
                (candidate.Attributes & FileAttributes.ReadOnly) != 0,
                (candidate.Attributes & FileAttributes.System) != 0,
                hash,
                isUnstable));

            hashed++;
            Report(progress, "full", hashed, totalFull, candidate.Path);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var entry in byFullHash)
        {
            var files = entry.Value;
            // 不稳定项不进入可删集合；但同组内仍保留展示。
            if (files.Count(f => !f.Unstable) < 2) continue;

            var recommended = files.Where(f => !f.Unstable)
                .OrderByDescending(f => IsUnderUserProfile(f.Path))
                .ThenBy(f => f.Path.Length)
                .ThenBy(f => f.ReadOnly)
                .First().Path;

            groups.Add(new DuplicateGroup(files[0].Size, files[0].FullHash, files, recommended));
        }

        // 只有稳定副本才计入可释放空间；不稳定项仅展示。
        static int StableCopies(DuplicateGroup g) => g.Files.Count(f => !f.Unstable);
        groups = groups.OrderByDescending(g => g.Size * Math.Max(0, StableCopies(g) - 1)).ToList();
        var wasted = groups.Sum(g => g.Size * Math.Max(0, StableCopies(g) - 1));
        clock.Stop();

        return new DuplicateScanResult(groups, new DuplicateScanStats(
            candidates.Count,
            groups.Count,
            wasted,
            excludedSmall,
            excludedCloud,
            excludedProtected,
            hardLinks,
            readErrors,
            unstable,
            clock.Elapsed));
    }

    /// <summary>生成删除计划前的严格校验：重新计算 SHA-256，并复核身份与时间戳。</summary>
    public DuplicateVerificationResult VerifyForDelete(
        DuplicateGroup group, IReadOnlyList<string> paths, CancellationToken token = default)
    {
        var verified = new List<DuplicateFile>();
        var rejected = new List<string>();
        var messages = new List<string>();

        foreach (var path in paths)
        {
            // 大文件校验放在后台线程执行，必须能被查重页的「取消」中止。
            token.ThrowIfCancellationRequested();
            var file = group.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                rejected.Add(path);
                messages.Add(path + ": not in group");
                continue;
            }

            if (file.Unstable)
            {
                rejected.Add(path);
                messages.Add(path + ": unstable during scan");
                continue;
            }

            var snapshot = Snapshot(path);
            if (snapshot is null || snapshot.Size != file.Size ||
                snapshot.LastWriteUtc != file.LastWriteUtc ||
                !string.Equals(snapshot.FileId, file.FileId, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add(path);
                messages.Add(path + ": identity or timestamp changed");
                continue;
            }

            var hash = TryHashFull(path, token);
            if (hash is null || !string.Equals(hash, group.FullHash, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add(path);
                messages.Add(path + ": content changed");
                continue;
            }

            verified.Add(file);
        }

        return new DuplicateVerificationResult(verified, rejected, messages);
    }

    private static void Report(IProgress<DuplicateScanProgress>? progress, string phase, int hashed, int total, string path)
        => progress?.Report(new DuplicateScanProgress(phase, hashed, total, path));

    private static bool IsUnderUserProfile(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Candidate(
        string Path,
        long Size,
        DateTime LastWriteUtc,
        FileIdentity Identity,
        FileAttributes Attributes,
        string SampleHash = "");

    private sealed record FileSnapshot(long Size, DateTime LastWriteUtc, string FileId);

    private static FileSnapshot? Snapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (!FileIdentityService.TryReadIdentity(path, out var identity, out _)) return null;
            return new FileSnapshot(info.Length, info.LastWriteTimeUtc, identity.ToPlanString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryHashSample(string path, long size, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            using var sha = SHA256.Create();

            var buffer = new byte[SampleBytes];
            var chunk = Math.Min(SampleBytes, size);
            ReadAt(stream, 0, buffer, chunk);
            sha.TransformBlock(buffer, 0, (int)chunk, null, 0);

            var middleStart = Math.Max(0, size / 2 - SampleBytes / 2);
            ReadAt(stream, middleStart, buffer, chunk);
            sha.TransformBlock(buffer, 0, (int)chunk, null, 0);

            var tailStart = Math.Max(0, size - SampleBytes);
            ReadAt(stream, tailStart, buffer, chunk);
            sha.TransformBlock(buffer, 0, (int)chunk, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ReadAt(FileStream stream, long offset, byte[] buffer, long count)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, (int)(count - read));
            if (n <= 0) break;
            read += n;
        }
        if (read < count) Array.Clear(buffer, read, (int)(count - read));
    }

    private static string? TryHashFull(string path, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            var buffer = new byte[BufferBytes];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
