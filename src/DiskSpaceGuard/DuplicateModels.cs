namespace DiskSpaceGuard.Models;

public sealed record DuplicateFile(
    string Path,
    long Size,
    DateTime LastWriteUtc,
    string Volume,
    string FileId,
    bool ReadOnly,
    bool System,
    string FullHash,
    bool Unstable);

public sealed record DuplicateGroup(
    long Size,
    string FullHash,
    IReadOnlyList<DuplicateFile> Files,
    string? RecommendedPath);

public sealed record DuplicateScanStats(
    int Files,
    int Groups,
    long WastedBytes,
    int ExcludedSmall,
    int ExcludedCloud,
    int ExcludedProtected,
    int HardLinks,
    int ReadErrors,
    int Unstable,
    TimeSpan Elapsed);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    DuplicateScanStats Stats);

public sealed record DuplicateScanProgress(
    string Phase,
    int Hashed,
    int Total,
    string CurrentPath);

public sealed record DuplicateVerificationResult(
    IReadOnlyList<DuplicateFile> Verified,
    IReadOnlyList<string> Rejected,
    IReadOnlyList<string> Messages);
