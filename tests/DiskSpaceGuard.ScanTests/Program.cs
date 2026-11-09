using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using DiskSpaceGuard.Controls;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.ScanTests;
using DiskSpaceGuard.Services;

if (args.Contains("--bench"))
{
    Bench.Run();
    return 0;
}

var previewIndex = Array.IndexOf(args, "--preview");
if (previewIndex >= 0)
{
    if (args.Length < previewIndex + 3)
    {
        Console.WriteLine("用法：--preview <扫描目录> <输出目录>");
        return 2;
    }
    await PreviewRenderer.RunAsync(args[previewIndex + 1], args[previewIndex + 2]);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("T0  基础递归与统计", BasicScan),
    ("T2  重解析点不跟随（junction）", ReparsePoint),
    ("T19 硬链接路径口径", HardLink),
    ("T4  取消后保留部分结果", CancelScan),
    ("T5  状态机非法转换", StateMachine),
    ("T3  错误分类映射", ErrorClassification),
    ("T6  分层边界不伪装 0", ShallowBoundary),
    ("T7  小文件合并后大小不变", AggregateSmallFiles),
    ("T8  树图节点预算与降级", TreemapBudget),
    ("T34 用途查询只发送文件名", FileInfoPrivacy),
    ("T35 保护判定与未知类型", FileInfoVerdict),
    ("T1  路径边界比较", PathBoundary),
    ("T20 计划篡改被拒绝", PlanTamper),
    ("T6  对象变化则跳过", ChangedObject),
    ("T22 重复提交被拒绝", DuplicateSubmit),
    ("T7  重解析点不进入计划", ReparseNotPlanned),
    ("T24 回收站失败不改永久", RecycleFailureNoFallback),
    ("T21 计划版本不兼容被拒绝", PlanVersionMismatch),
    ("T36 文件说明路径边界", FileInfoPathBoundary),
    ("T17 同尺寸替换被跳过", SameSizeReplacement),
    ("T25 台账半行自动修复", LedgerTailRepair),
    ("T23 父目录被替换后子项跳过", ParentDirectoryReplaced),
    ("T38 L4 自定义保护目录生效", CustomProtectedRoot),
    ("T18 目录递归自底向上删除", RecursiveDirectoryDelete),
    ("T18 计划外新增项不删除", RecursiveOutOfPlanKept),
    ("T18 递归不穿透重解析点", RecursiveReparseNotFollowed),
    ("T39 清理规则只统计具名目标", CleanupRuleEvaluation),
    ("T40 台账读取跳过损坏行", LedgerRead),
    ("T41 台账 CSV 导出与转义", LedgerCsvExport),
    ("T42 中英文案完整且可切换", LocalizationCompleteness),
    ("T43 还原只选择回收站条目", RestorePathSelection),
    ("T44 硬件字段映射不推算寿命", HardwareFieldRules),
    ("T45 重复文件分组与硬链接排除", DuplicateDetection),
    ("T46 采样碰撞但内容不同不判重", DuplicateSampleCollision),
    ("T47 删除前重新校验内容变化", DuplicateVerifyBeforeDelete),
    ("T48 L4 保护目录不参与查重", DuplicateL4Excluded),
    ("T49 MSI 去重与系统组件默认隐藏", AppDedupeAndFilter),
    ("T50 卸载命令行解析与注入边界", AppCommandParsing),
    ("T51 硬禁止与无入口条目不提供执行", AppBlockPolicy),
    ("T52 静默入口选择与结果判定", AppQuietAndOutcome),
    ("T53 卸载残留不登记共享根", AppLeftoverSafety),
    ("T54 MSIX JSON 解析与框架/预装过滤", AppxParsing),
    ("T55 诊断脱敏与日志尾部", DiagnosticsPrivacy),
    ("T56 诊断 ZIP 导出结构", DiagnosticsExport),
    ("T57 身份布局与读取稳定性", FileIdentityLayout),
    ("T58 黄金分割布局面积与边界", GoldenLayoutInvariants)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
    }
}

Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string NewTempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "dsg-scan-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

static void SafeDelete(string dir)
{
    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    catch { }
}

static async Task BasicScan()
{
    var root = NewTempDir();
    try
    {
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1024 * 1024]);
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[2 * 1024 * 1024]);

        var service = new ScanService();
        var result = await service.ScanAsync(root, ScanMode.FullRecursive, 1);

        Assert(result.Stats.State == ScanTaskState.Completed, $"状态应为 Completed，实际 {result.Stats.State}");
        Assert(result.Stats.Completeness == ScanCompleteness.Complete, $"完整性应为 Complete，实际 {result.Stats.Completeness}");
        Assert(result.Stats.Files == 2, $"文件数应为 2，实际 {result.Stats.Files}");
        Assert(result.Stats.LogicalBytes == 3L * 1024 * 1024, $"大小应为 3 MB，实际 {result.Stats.LogicalBytes}");
        Assert(result.Root.Children.Count == 2, $"根下应有 2 项，实际 {result.Root.Children.Count}");
    }
    finally { SafeDelete(root); }
}

static async Task ReparsePoint()
{
    var root = NewTempDir();
    var link = string.Empty;
    try
    {
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "inside.bin"), new byte[2 * 1024 * 1024]);

        var scanRoot = Path.Combine(root, "scan");
        Directory.CreateDirectory(scanRoot);
        File.WriteAllBytes(Path.Combine(scanRoot, "real.bin"), new byte[1024 * 1024]);

        link = Path.Combine(scanRoot, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert(Directory.Exists(link), "junction 创建失败：" + proc.StandardError.ReadToEnd());

        var service = new ScanService();
        var result = await service.ScanAsync(scanRoot, ScanMode.FullRecursive, 1);

        Assert(result.Stats.ReparseSkipped == 1, $"重解析点计数应为 1，实际 {result.Stats.ReparseSkipped}");
        Assert(result.Stats.LogicalBytes == 1024 * 1024, $"不应统计链接目标，实际 {result.Stats.LogicalBytes}");

        var linkNode = result.Root.Children.FirstOrDefault(c => c.Name == "link");
        Assert(linkNode is not null, "缺少 link 节点");
        Assert(linkNode!.Kind == NodeKind.ReparsePoint, "link 应标记为 ReparsePoint");
        Assert(linkNode.LogicalSize == 0, "重解析点大小应为 0");
    }
    finally
    {
        if (link.Length > 0) { try { Directory.Delete(link); } catch { } }
        SafeDelete(root);
    }
}

static async Task HardLink()
{
    var root = NewTempDir();
    try
    {
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var fileA = Path.Combine(dirA, "shared.bin");
        File.WriteAllBytes(fileA, new byte[3 * 1024 * 1024]);
        var fileB = Path.Combine(dirB, "shared.bin");

        Assert(Native.CreateHardLink(fileB, fileA, IntPtr.Zero), "CreateHardLink 失败，错误码 " + Marshal.GetLastWin32Error());

        var service = new ScanService();
        var result = await service.ScanAsync(root, ScanMode.FullRecursive, 1);

        Assert(result.Stats.LogicalBytes == 6L * 1024 * 1024, $"两个路径都应计入，实际 {result.Stats.LogicalBytes}");
        var a = result.Root.Children.First(c => c.Name == "a");
        var b = result.Root.Children.First(c => c.Name == "b");
        Assert(a.LogicalSize == 3L * 1024 * 1024, $"a 应计入 3 MB，实际 {a.LogicalSize}");
        Assert(b.LogicalSize == 3L * 1024 * 1024, $"b 应计入 3 MB，实际 {b.LogicalSize}");
    }
    finally { SafeDelete(root); }
}

static async Task CancelScan()
{
    var root = NewTempDir();
    try
    {
        for (var d = 0; d < 20; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"d{d}"));
            for (var f = 0; f < 100; f++)
                File.WriteAllBytes(Path.Combine(dir.FullName, $"f{f}.bin"), new byte[64]);
        }

        var service = new ScanService();
        using var cts = new CancellationTokenSource();
        var task = service.ScanAsync(root, ScanMode.FullRecursive, 1, null, cts.Token);
        cts.Cancel();
        var result = await task;

        Assert(result.Stats.State == ScanTaskState.Cancelled, $"状态应为 Cancelled，实际 {result.Stats.State}");
        Assert(result.Stats.Completeness == ScanCompleteness.Cancelled, "完整性应为 Cancelled");
        Assert(result.Root.FullPath == root, "取消后仍应返回扫描根与部分结果");

        service.Cancel();
        Assert(service.State == ScanTaskState.Cancelled, "完成后 Cancel 不应改变终态");
    }
    finally { SafeDelete(root); }
}

static Task StateMachine()
{
    var machine = new ScanStateMachine();
    Assert(machine.State == ScanTaskState.Idle, "初始状态应为 Idle");
    Assert(machine.TryTransition(ScanTaskState.Preparing), "Idle → Preparing 应允许");
    Assert(!machine.TryTransition(ScanTaskState.Completed), "Preparing → Completed 应拒绝");
    Assert(machine.TryTransition(ScanTaskState.Running), "Preparing → Running 应允许");
    Assert(machine.TryTransition(ScanTaskState.Pausing), "Running → Pausing 应允许");
    Assert(machine.TryTransition(ScanTaskState.Paused), "Pausing → Paused 应允许");
    Assert(!machine.TryTransition(ScanTaskState.Completed), "Paused → Completed 应拒绝");
    Assert(machine.TryTransition(ScanTaskState.Running), "Paused → Running 应允许");
    Assert(machine.TryTransition(ScanTaskState.Completed), "Running → Completed 应允许");
    Assert(!machine.TryTransition(ScanTaskState.Running), "终态不可再转换");
    return Task.CompletedTask;
}

static Task ErrorClassification()
{
    Assert(ScanErrorClassifier.Classify(new UnauthorizedAccessException()) == ScanErrorKind.AccessDenied, "权限错误分类");
    Assert(ScanErrorClassifier.Classify(new DirectoryNotFoundException()) == ScanErrorKind.PathNotFound, "目录不存在分类");
    Assert(ScanErrorClassifier.Classify(new IOException("x", unchecked((int)0x80070020))) == ScanErrorKind.SharingViolation, "共享冲突分类");
    Assert(ScanErrorClassifier.Classify(new IOException("x", unchecked((int)0x800700CE))) == ScanErrorKind.PathTooLong, "路径过长分类");
    Assert(ScanErrorClassifier.Classify(new IOException("x", unchecked((int)0x80070015))) == ScanErrorKind.DiskUnavailable, "设备未就绪分类");
    Assert(ScanErrorClassifier.Classify(new IOException("x", unchecked((int)0x80070005))) == ScanErrorKind.AccessDenied, "IO 权限错误分类");
    return Task.CompletedTask;
}

static async Task ShallowBoundary()
{
    var root = NewTempDir();
    try
    {
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[2048]);

        var service = new ScanService();
        var result = await service.ScanAsync(root, ScanMode.Shallow, 1);

        var boundary = result.Root.Children.FirstOrDefault(c => c.Name == "sub");
        Assert(boundary is not null, "应记录边界目录");
        Assert(!boundary!.IsScanned, "边界目录应为未扫描");
        Assert(boundary.SizeDisplay == "—", "未扫描目录必须显示 —，不能显示 0");
        Assert(result.Stats.Completeness == ScanCompleteness.Partial, "分层结果应为 Partial");
        Assert(result.Stats.Files == 1, $"只应扫到根下文件，实际 {result.Stats.Files}");
    }
    finally { SafeDelete(root); }
}

static async Task AggregateSmallFiles()
{
    var root = NewTempDir();
    try
    {
        for (var i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(root, $"small{i}.bin"), new byte[100 * 1024]);
        File.WriteAllBytes(Path.Combine(root, "big.bin"), new byte[2 * 1024 * 1024]);

        var service = new ScanService();
        var result = await service.ScanAsync(root, ScanMode.FullRecursive, 1);

        Assert(result.Stats.Files == 6, $"文件计数不受剪枝影响，应仍为 6，实际 {result.Stats.Files}");
        Assert(result.Stats.LogicalBytes == 2L * 1024 * 1024 + 5 * 100 * 1024,
            $"总大小应保持全量，实际 {result.Stats.LogicalBytes}");
        Assert(result.Root.LogicalSize == result.Stats.LogicalBytes,
            "父目录大小必须等于全量之和，不能因合并而变小");

        var aggregate = result.Root.Children.Where(c => c.IsAggregate).ToList();
        Assert(aggregate.Count == 1, $"应只有 1 个合并节点，实际 {aggregate.Count}");
        Assert(aggregate[0].AggregatedCount == 5, $"合并计数应为 5，实际 {aggregate[0].AggregatedCount}");
        Assert(aggregate[0].LogicalSize == 5 * 100 * 1024, "合并节点大小应等于小文件之和");
        Assert(result.Root.Children.Any(c => c.Name == "big.bin"), "大于阈值的文件应保留为独立节点");
        Assert(result.Root.Children.Count == 2, $"根下应为 big.bin + 1 个合并节点，实际 {result.Root.Children.Count}");
    }
    finally { SafeDelete(root); }
}

static async Task TreemapBudget()
{
    var root = NewTempDir();
    try
    {
        for (var d = 0; d < 40; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"dir{d}"));
            for (var f = 0; f < 40; f++)
                File.WriteAllBytes(Path.Combine(dir.FullName, $"f{f}.bin"), new byte[2 * 1024 * 1024]);
        }

        var service = new ScanService();
        var result = await service.ScanAsync(root, ScanMode.FullRecursive, 1);

        var total = CountNodes(result.Root);
        Assert(total == 1 + 40 + 40 * 40, $"树节点总数应为 1641，实际 {total}");
        Assert(total <= 5000, "108 万文件场景须依赖剪枝把节点数控制在预算内");
    }
    finally { SafeDelete(root); }

    static int CountNodes(ScanNode node) => 1 + node.Children.Sum(CountNodes);
}

static Task PathBoundary()
{
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    Assert(ProtectionEvaluator.IsUnder(Path.Combine(windows, "System32", "a.dll"), windows),
        "子路径应判定为在根下");
    Assert(ProtectionEvaluator.IsUnder(windows, windows), "根自身应判定为在根下");
    Assert(!ProtectionEvaluator.IsUnder(windows + "Old", windows), "C:\\WindowsOld 不得命中 C:\\Windows");
    Assert(!ProtectionEvaluator.IsUnder(Path.Combine(windows, "..", "Other"), windows), "上级路径不得命中");
    return Task.CompletedTask;
}

static Task PlanTamper()
{
    var root = NewTempDir();
    var file = Path.Combine(root, "a.bin");
    try
    {
        File.WriteAllBytes(file, new byte[4096]);
        var node = ScanOne(file, root);
        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { node }, DeleteMode.Permanent, root);

        Assert(plan.Items.Count == 1, $"计划应有 1 项，实际 {plan.Items.Count}");
        var tampered = plan.Items[0] with { SnapshotSize = 1 };
        var badPlan = plan with { Items = new[] { tampered } };

        var result = service.ExecuteAsync(badPlan,
            new DeleteRunState { PlanId = badPlan.PlanId }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert(result.FatalMessage is not null && result.FatalMessage.Contains("哈希"),
            "修改计划字段后必须因哈希不符被拒绝");
        Assert(File.Exists(file), "被拒绝的计划不得删除文件");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task ChangedObject()
{
    var root = NewTempDir();
    var file = Path.Combine(root, "b.bin");
    try
    {
        File.WriteAllBytes(file, new byte[8192]);
        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { ScanOne(file, root) }, DeleteMode.Permanent, root);

        File.WriteAllBytes(file, new byte[16384]);

        var result = service.ExecuteAsync(plan,
            new DeleteRunState { PlanId = plan.PlanId }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert(result.Skipped == 1, $"对象变化应跳过 1 项，实际 skipped={result.Skipped} deleted={result.Deleted}");
        Assert(File.Exists(file), "被替换的文件不得被删除");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task DuplicateSubmit()
{
    var root = NewTempDir();
    var file = Path.Combine(root, "c.bin");
    try
    {
        File.WriteAllBytes(file, new byte[2048]);
        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { ScanOne(file, root) }, DeleteMode.Permanent, root);

        var first = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();
        var second = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(first.Deleted == 1, $"首次应删除 1 项，实际 {first.Deleted}");
        Assert(second.FatalMessage is not null && second.FatalMessage.Contains("重复"),
            "同一计划不得重复执行");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task ReparseNotPlanned()
{
    var node = new ScanNode("link", @"C:\link", NodeKind.ReparsePoint);
    var service = new DeleteService();
    var plan = service.BuildPlan(new[] { node }, DeleteMode.Permanent, @"C:\");
    Assert(plan.Items.Count == 0, "重解析点不得进入删除计划");
    return Task.CompletedTask;
}

static Task RecycleFailureNoFallback()
{
    var missing = Path.Combine(Path.GetTempPath(), "dsg-missing-" + Guid.NewGuid().ToString("N") + ".bin");
    var ok = RecycleBinService.TryMoveToRecycleBin(missing, out var error);
    Assert(!ok, "不存在的文件不应报告回收站成功");
    Assert(!File.Exists(missing), "回收站失败不得产生任何删除副作用");

    var root = NewTempDir();
    try
    {
        var file = Path.Combine(root, "d.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { ScanOne(file, root) }, DeleteMode.RecycleFirst, root);
        var result = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        // 无论回收站成功还是失败：失败项不得被永久删除；成功项必须离开原路径。
        if (result.Failed > 0) Assert(File.Exists(file), "回收站失败时文件必须保留，禁止降级为永久删除");
        if (result.RecyclePending > 0) Assert(!File.Exists(file), "回收站成功后文件应离开原位置");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task PlanVersionMismatch()
{
    var root = NewTempDir();
    var file = Path.Combine(root, "e.bin");
    try
    {
        File.WriteAllBytes(file, new byte[1024]);
        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { ScanOne(file, root) }, DeleteMode.Permanent, root)
            with { PlanVersion = "9.9" };

        var result = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.FatalMessage is not null && result.FatalMessage.Contains("版本"),
            $"版本不兼容必须拒绝执行，实际：{result.FatalMessage}");
        Assert(File.Exists(file), "版本不兼容的计划不得删除文件");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task FileInfoPathBoundary()
{
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var sibling = Path.Combine(Path.GetDirectoryName(windows)!, "WindowsOld", "a.dll");

    var verdict = FileInfoCatalog.Evaluate(sibling, "a.dll");
    Assert(verdict.Level != FileSafety.Blocked,
        $"C:\\WindowsOld 不得被判为 L0，实际 {verdict.Level}");

    var inside = FileInfoCatalog.Evaluate(Path.Combine(windows, "System32", "a.dll"), "a.dll");
    Assert(inside.Level == FileSafety.Blocked, "Windows 子目录应判为 L0");
    return Task.CompletedTask;
}

static Task SameSizeReplacement()
{
    var root = NewTempDir();
    var file = Path.Combine(root, "f.bin");
    try
    {
        File.WriteAllBytes(file, new byte[4096]);
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { ScanOne(file, root) }, DeleteMode.Permanent, root);

        // 用同尺寸新文件替换，并把时间戳复位成与计划一致：身份（FileId）必须仍能识破。
        File.Delete(file);
        File.WriteAllBytes(file, new byte[4096]);
        File.SetLastWriteTimeUtc(file, stamp);

        var result = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.Skipped == 1 && result.Deleted == 0,
            $"同尺寸替换必须跳过，实际 deleted={result.Deleted} skipped={result.Skipped}");
        Assert(File.Exists(file), "替换后的文件不得被删除");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task LedgerTailRepair()
{
    var path = Path.Combine(Path.GetTempPath(), "dsg-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
    try
    {
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\n{\"partial\":", new System.Text.UTF8Encoding(false));
        var removed = DeleteService.RepairLedgerTail(path);

        Assert(removed > 0, "半行台账应被截断");
        var text = File.ReadAllText(path);
        Assert(text.EndsWith("\n"), "修复后必须以换行结尾");
        Assert(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2,
            $"应保留 2 条完整记录，实际：{text}");
        Assert(!text.Contains("partial"), "不完整的最后一行必须被移除");
    }
    finally { try { File.Delete(path); } catch { } }
    return Task.CompletedTask;
}

static Task ParentDirectoryReplaced()
{
    var root = NewTempDir();
    var dir = Path.Combine(root, "folder");
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "f.bin");
    try
    {
        File.WriteAllBytes(file, new byte[2048]);
        var stamp = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        var service = new DeleteService();
        var plan = service.BuildPlan(new[] { ScanOne(file, root) }, DeleteMode.Permanent, root);

        // 父目录被整体替换：路径相同、文件名相同、大小与时间戳相同，但对象身份已变。
        var moved = dir + "-old";
        Directory.Move(dir, moved);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, stamp);

        var result = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.Skipped == 1 && result.Deleted == 0,
            $"父目录替换后必须跳过，实际 deleted={result.Deleted} skipped={result.Skipped}");
        Assert(File.Exists(file), "新目录下的同名文件不得被删除");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task CustomProtectedRoot()
{
    var root = NewTempDir();
    var protectedDir = Path.Combine(root, "keep");
    Directory.CreateDirectory(protectedDir);
    var file = Path.Combine(protectedDir, "a.bin");
    try
    {
        File.WriteAllBytes(file, new byte[16]);
        var evaluator = new ProtectionEvaluator();
        evaluator.AddCustomRoot(protectedDir);

        var level = evaluator.Evaluate(file, File.GetAttributes(file), out var reason);
        Assert(level == ProtectionLevel.L4, $"应判为 L4，实际 {level}（{reason}）");
        Assert(evaluator.CustomProtectedRoots.Count == 1, "保护目录应被记录");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task RecursiveDirectoryDelete()
{
    var root = NewTempDir();
    try
    {
        var sel = Path.Combine(root, "sel");
        var sub = Path.Combine(sel, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sel, "a.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[8192]);

        var node = new ScanNode("sel", sel, NodeKind.Directory);
        var service = new DeleteService();
        var plan = service.TryBuildPlan(new[] { node }, DeleteMode.Permanent, root, recursive: true,
            out var report, out var error);

        Assert(plan is not null, "递归计划生成失败：" + error);
        Assert(report.FileCount == 2 && report.DirectoryCount == 2,
            $"计划应含 2 文件 / 2 目录，实际 {report.FileCount} / {report.DirectoryCount}");

        var items = plan!.Items.ToList();
        var subIndex = items.FindIndex(i => i.NormalizedPath == ProtectionEvaluator.Normalize(sub));
        var fileIndex = items.FindIndex(i => i.NormalizedPath == ProtectionEvaluator.Normalize(Path.Combine(sub, "b.bin")));
        var rootIndex = items.FindIndex(i => i.NormalizedPath == ProtectionEvaluator.Normalize(sel));
        Assert(subIndex > fileIndex, "子目录必须排在其文件之后（自底向上）");
        Assert(rootIndex > subIndex, "选中目录必须排在最后");

        var result = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.Deleted == 4 && result.Failed == 0 && result.Skipped == 0,
            $"应删除 4 项，实际 deleted={result.Deleted} skipped={result.Skipped} failed={result.Failed}");
        Assert(!Directory.Exists(sel), "递归完成后选中目录应被移除");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task RecursiveOutOfPlanKept()
{
    var root = NewTempDir();
    try
    {
        var sel = Path.Combine(root, "sel");
        Directory.CreateDirectory(sel);
        var planned = Path.Combine(sel, "planned.bin");
        File.WriteAllBytes(planned, new byte[1024]);

        var service = new DeleteService();
        var plan = service.TryBuildPlan(new[] { new ScanNode("sel", sel, NodeKind.Directory) },
            DeleteMode.Permanent, root, recursive: true, out _, out var error);
        Assert(plan is not null, "递归计划生成失败：" + error);

        // 预演之后新增：不在计划内，执行时一律不删。
        var late = Path.Combine(sel, "late.bin");
        File.WriteAllBytes(late, new byte[2048]);

        var result = service.ExecuteAsync(plan!, new DeleteRunState { PlanId = plan!.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(!File.Exists(planned), "计划内文件应被删除");
        Assert(File.Exists(late), "计划外新增文件不得被删除");
        Assert(Directory.Exists(sel), "计划外项使目录非空，目录必须保留");
        Assert(result.Deleted == 1 && result.Skipped >= 1,
            $"应删除 1 项且跳过目录，实际 deleted={result.Deleted} skipped={result.Skipped}");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task RecursiveReparseNotFollowed()
{
    var root = NewTempDir();
    var link = string.Empty;
    try
    {
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "inside.bin");
        File.WriteAllBytes(targetFile, new byte[4096]);

        var sel = Path.Combine(root, "sel");
        Directory.CreateDirectory(sel);
        File.WriteAllBytes(Path.Combine(sel, "real.bin"), new byte[1024]);

        link = Path.Combine(sel, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert(Directory.Exists(link), "junction 创建失败：" + proc.StandardError.ReadToEnd());

        var service = new DeleteService();
        var plan = service.TryBuildPlan(new[] { new ScanNode("sel", sel, NodeKind.Directory) },
            DeleteMode.Permanent, root, recursive: true, out var report, out var error);
        Assert(plan is not null, "递归计划生成失败：" + error);
        Assert(report.ReparseSkipped >= 1, "重解析点必须被单独计数");
        Assert(plan!.Items.All(i => !ProtectionEvaluator.IsUnder(i.NormalizedPath, target)),
            "不得把重解析点目标的内容写入计划");

        var result = service.ExecuteAsync(plan, new DeleteRunState { PlanId = plan.PlanId },
            null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(File.Exists(targetFile), "重解析点目标不得被删除");
        Assert(Directory.Exists(link), "重解析点本体不应进入计划，必须保留");
        Assert(!File.Exists(Path.Combine(sel, "real.bin")), "计划内普通文件应被删除");
        Assert(Directory.Exists(sel), "因重解析点保留，目录必须非空并保留");
        Assert(result.Deleted == 1 && result.Skipped >= 1,
            $"应删除 1 项且跳过目录，实际 deleted={result.Deleted} skipped={result.Skipped}");
    }
    finally
    {
        if (link.Length > 0) { try { Directory.Delete(link); } catch { } }
        SafeDelete(root);
    }
    return Task.CompletedTask;
}

static Task CleanupRuleEvaluation()
{
    var root = NewTempDir();
    try
    {
        var oldFile = Path.Combine(root, "old.tmp");
        var freshFile = Path.Combine(root, "fresh.tmp");
        File.WriteAllBytes(oldFile, new byte[3000]);
        File.WriteAllBytes(freshFile, new byte[5000]);
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-2));

        var rule = new CleanupRule("test.rule", "测试规则", "测试", root,
            TimeSpan.FromHours(24), "只统计超过 24 小时的文件");
        var service = new CleanupRuleService(new ProtectionEvaluator(), new[] { rule });
        var suggestions = service.Evaluate(CancellationToken.None);

        Assert(suggestions.Count == 1, $"应有 1 条建议，实际 {suggestions.Count}");
        var suggestion = suggestions[0];
        Assert(suggestion.RuleId == "test.rule", "建议必须携带稳定 RuleId");
        Assert(suggestion.FileCount == 1, $"只应命中 1 个旧文件，实际 {suggestion.FileCount}");
        Assert(suggestion.Bytes == 3000, $"应只统计旧文件字节，实际 {suggestion.Bytes}");
        Assert(suggestion.ExcludedCount >= 1, "过新的文件应计入排除项");
        Assert(File.Exists(freshFile) && File.Exists(oldFile), "规则统计不得删除任何文件");

        var missingRule = new CleanupRule("test.missing", "不存在", "测试",
            Path.Combine(root, "nope"), TimeSpan.Zero, "目录不存在");
        var missingService = new CleanupRuleService(new ProtectionEvaluator(), new[] { missingRule });
        Assert(missingService.Evaluate(CancellationToken.None).Count == 0,
            "目标不存在的规则不应生成建议");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task LedgerRead()
{
    var path = Path.Combine(Path.GetTempPath(), "dsg-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
    try
    {
        var planId = Guid.NewGuid();
        var record = new
        {
            schema = "1.0",
            planId,
            createdUtc = new DateTime(2026, 3, 1, 1, 2, 3, DateTimeKind.Utc),
            executedUtc = new DateTime(2026, 3, 1, 1, 2, 4, DateTimeKind.Utc),
            mode = "Permanent",
            planHash = new string('a', 64),
            total = 1,
            deleted = 1,
            recyclePending = 0,
            skipped = 0,
            failed = 0,
            estimatedBytes = 100L,
            actualBytes = 100L,
            fatal = (string?)null,
            items = new[]
            {
                new { ItemId = 1, path = @"C:\tmp\a.bin", outcome = "Deleted", verify = "Matched", Message = "" }
            }
        };

        var line = System.Text.Json.JsonSerializer.Serialize(record);
        File.WriteAllText(path, line + "\n{\"broken\":\n" + line + "\n",
            new System.Text.UTF8Encoding(false));

        var result = CleanupLedgerReader.Read(path);
        Assert(result.Error is null, "正常文件不应返回读取错误");
        Assert(result.Entries.Count == 2, $"应读取 2 条记录，实际 {result.Entries.Count}");
        Assert(result.SkippedLines == 1, $"损坏行应被跳过，实际 {result.SkippedLines}");
        Assert(result.Entries[0].PlanId == planId, "计划 ID 应正确解析");
        Assert(result.Entries[0].Deleted == 1 && result.Entries[0].ActualBytes == 100,
            "执行统计应正确解析");
        Assert(result.Entries[0].Items.Count == 1 &&
               result.Entries[0].Items[0].Path == @"C:\tmp\a.bin" &&
               result.Entries[0].Items[0].Outcome == "Deleted",
            "条目明细应正确解析");
    }
    finally { try { File.Delete(path); } catch { } }
    return Task.CompletedTask;
}

static Task LedgerCsvExport()
{
    var newer = new CleanupLedgerEntry(
        Guid.NewGuid(),
        new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
        "RecycleFirst", "hash", 1, 0, 1, 0, 0, 1024, 0, "",
        new[]
        {
            new CleanupLedgerItem(1, "C:\\a,b\\\"x\".bin", "MovedToRecycleBin", "Matched", "含,逗号与\"引号\"")
        });

    var older = new CleanupLedgerEntry(
        Guid.NewGuid(),
        new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        "Permanent", "hash2", 0, 0, 0, 0, 0, 0, 0, "fatal", Array.Empty<CleanupLedgerItem>());

    var csv = CleanupLedgerCsv.Build(new[] { older, newer });
    var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    Assert(lines.Length == 3, $"应为表头 + 2 行数据，实际 {lines.Length} 行");
    Assert(lines[0].StartsWith("executedUtc,"), "首行应为表头");
    Assert(lines[1].Contains(newer.PlanId.ToString()), "应按执行时间倒序，新记录在前");
    Assert(lines[1].Contains("\"C:\\a,b\\\"\"x\"\".bin\""), "路径中的逗号与引号必须正确转义");
    Assert(lines[1].Contains("\"含,逗号与\"\"引号\"\"\""), "说明字段中的引号必须双写转义");
    Assert(lines[2].Contains("fatal"), "无条目记录也应导出执行级字段");
    return Task.CompletedTask;
}

static Task LocalizationCompleteness()
{
    foreach (var (key, pair) in LocalizationService.All)
    {
        Assert(!string.IsNullOrWhiteSpace(pair.Zh), $"{key} 缺少中文文案");
        Assert(!string.IsNullOrWhiteSpace(pair.En), $"{key} 缺少英文文案");
    }

    Assert(LocalizationService.Text("Nav.Overview", UiLanguage.EnUs) == "Overview",
        "英文导航文案错误");
    Assert(LocalizationService.Text("Nav.Overview", UiLanguage.ZhCn) == "总览",
        "中文导航文案错误");

    var original = Loc.Instance.Language;
    try
    {
        Loc.Instance.SetLanguage(UiLanguage.EnUs);
        var query = FileInfoCatalog.BuildQuery("chrome.dll");
        Assert(query.Contains("chrome.dll") && query.StartsWith("What"),
            "英文查询串应使用英文模板：" + query);

        Loc.Instance.SetLanguage(UiLanguage.ZhCn);
        Assert(FileInfoCatalog.BuildQuery("chrome.dll").Contains("是什么文件"),
            "中文查询串应使用中文模板");
    }
    finally
    {
        Loc.Instance.SetLanguage(original);
    }

    return Task.CompletedTask;
}

static Task RestorePathSelection()
{
    var recycle = new CleanupLedgerEntry(
        Guid.NewGuid(), default, DateTime.UtcNow, "RecycleFirst", "hash",
        3, 0, 3, 0, 0, 0, 0, "",
        new[]
        {
            new CleanupLedgerItem(1, @"C:\a\one.bin", "MovedToRecycleBin", "Matched", ""),
            new CleanupLedgerItem(2, @"C:\A\ONE.BIN", "MovedToRecycleBin", "Matched", ""),
            new CleanupLedgerItem(3, @"C:\a\two.bin", "Deleted", "Matched", ""),
            new CleanupLedgerItem(4, "", "MovedToRecycleBin", "Matched", "")
        });

    var paths = RecycleBinRestoreService.RestorablePaths(recycle);
    Assert(paths.Count == 1 && paths[0] == @"C:\a\one.bin",
        $"应只保留去重后的回收站条目，实际 {string.Join(",", paths)}");

    var permanent = recycle with { Mode = "Permanent" };
    Assert(RecycleBinRestoreService.RestorablePaths(permanent).Count == 0,
        "永久删除记录不得提供还原路径");
    return Task.CompletedTask;
}

static Task HardwareFieldRules()
{
    Assert(HardwareCatalog.BusTypeKey(17) == "HW.Bus.NVMe", "NVMe 映射错误");
    Assert(HardwareCatalog.BusTypeKey(11) == "HW.Bus.SATA", "SATA 映射错误");
    Assert(HardwareCatalog.BusTypeKey(7) == "HW.Bus.USB", "USB 映射错误");
    Assert(HardwareCatalog.BusTypeKey(8) == "HW.Bus.RAID", "RAID 映射错误");
    Assert(HardwareCatalog.BusTypeKey(14) == "HW.Bus.Virtual", "Virtual 映射错误");
    Assert(HardwareCatalog.BusTypeKey(999) == "HW.Bus.Unknown", "未知总线必须标未知");

    Assert(HardwareCatalog.MediaTypeKey(3) == "HW.Media.HDD", "HDD 映射错误");
    Assert(HardwareCatalog.MediaTypeKey(4) == "HW.Media.SSD", "SSD 映射错误");
    Assert(HardwareCatalog.MediaTypeKey(5) == "HW.Media.SCM", "SCM 映射错误");
    Assert(HardwareCatalog.MediaTypeKey(0) == "HW.Media.Unknown", "未知介质必须标未知");

    Assert(HardwareCatalog.HealthKey(0) == "HW.Health.Healthy", "健康映射错误");
    Assert(HardwareCatalog.HealthKey(1) == "HW.Health.Warning", "警告映射错误");
    Assert(HardwareCatalog.HealthKey(2) == "HW.Health.Unhealthy", "异常映射错误");
    Assert(HardwareCatalog.HealthKey(5) == "HW.Health.Unknown", "未知健康必须标未知");

    Assert(HardwareCatalog.WearDisplay(15) == "15%", "寿命消耗应原样展示");
    Assert(HardwareCatalog.WearDisplay(120) == "120%", "超过 100% 不得截断");
    Assert(HardwareCatalog.WearExceedsHundred(120), "超过 100% 应可被标注");
    Assert(HardwareCatalog.WearDisplay(null) is null, "缺失值必须为 null，不得填 0");
    return Task.CompletedTask;
}

static Task DuplicateDetection()
{
    var root = NewTempDir();
    try
    {
        var content = new byte[1024 * 1024 + 7];
        new Random(42).NextBytes(content);

        var a = Path.Combine(root, "a.bin");
        var b = Path.Combine(root, "sub", "b.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllBytes(a, content);
        File.WriteAllBytes(b, content);

        var hard = Path.Combine(root, "hard.bin");
        Assert(Native.CreateHardLink(hard, a, IntPtr.Zero),
            "CreateHardLink 失败，错误码 " + Marshal.GetLastWin32Error());

        File.WriteAllBytes(Path.Combine(root, "zero.bin"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(root, "unique.bin"), new byte[2048]);

        var service = new DuplicateFinderService();
        var result = service.ScanAsync(root, 0, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.Groups.Count == 1, $"应有 1 个重复组，实际 {result.Groups.Count}");
        var group = result.Groups[0];
        Assert(group.Files.Count == 2, $"硬链接应被排除，组内应剩 2 条路径，实际 {group.Files.Count}");
        Assert(result.Stats.HardLinks >= 1, "硬链接应被计数");
        Assert(result.Stats.ExcludedSmall >= 1, "0 字节文件应被排除");
        Assert(group.Files.All(f => !f.Unstable), "稳定文件不应标记为 Unstable");
        Assert(!string.IsNullOrEmpty(group.RecommendedPath), "应给出低置信度保留建议");
        Assert(result.Stats.WastedBytes == content.Length,
            $"可释放空间应为一份文件大小，实际 {result.Stats.WastedBytes}");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task DuplicateSampleCollision()
{
    var root = NewTempDir();
    try
    {
        const int size = 512 * 1024;
        var first = new byte[size];
        var second = new byte[size];
        Array.Fill(first, (byte)0x5A);
        Array.Fill(second, (byte)0x5A);

        // 采样区间：头 64KB、中段 224–288KB、尾 64KB；此处差异落在 100KB，采样看不见。
        second[100 * 1024] = 0xA5;

        File.WriteAllBytes(Path.Combine(root, "one.bin"), first);
        File.WriteAllBytes(Path.Combine(root, "two.bin"), second);

        var service = new DuplicateFinderService();
        var result = service.ScanAsync(root, 0, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.Groups.Count == 0, "采样相同但全量内容不同，不得判为重复");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task DuplicateVerifyBeforeDelete()
{
    var root = NewTempDir();
    try
    {
        var content = new byte[256 * 1024];
        new Random(7).NextBytes(content);
        var a = Path.Combine(root, "a.bin");
        var b = Path.Combine(root, "b.bin");
        File.WriteAllBytes(a, content);
        File.WriteAllBytes(b, content);

        var service = new DuplicateFinderService();
        var result = service.ScanAsync(root, 0, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Groups.Count == 1, "应形成 1 个重复组");

        var changed = (byte[])content.Clone();
        changed[123] ^= 0xFF;
        File.WriteAllBytes(b, changed);

        var verification = service.VerifyForDelete(result.Groups[0], new[] { a, b });
        Assert(verification.Verified.Count == 1,
            $"内容变化项必须被排除，实际 verified={verification.Verified.Count}");
        Assert(verification.Rejected.Count == 1,
            $"应有 1 项被拒绝，实际 {verification.Rejected.Count}");
        Assert(File.Exists(a) && File.Exists(b), "校验过程不得删除文件");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task DuplicateL4Excluded()
{
    var root = NewTempDir();
    try
    {
        var keep = Path.Combine(root, "keep");
        Directory.CreateDirectory(keep);
        var content = new byte[256 * 1024];
        new Random(9).NextBytes(content);
        File.WriteAllBytes(Path.Combine(keep, "a.bin"), content);
        File.WriteAllBytes(Path.Combine(keep, "b.bin"), content);

        var protection = new ProtectionEvaluator();
        protection.AddCustomRoot(keep);
        var guarded = new DuplicateFinderService(protection);
        var result = guarded.ScanAsync(root, 0, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert(result.Groups.Count == 0, "L4 保护目录中的文件不得参与查重");
        Assert(result.Stats.ExcludedProtected >= 2, "L4 排除项应被计数");

        var plain = new DuplicateFinderService();
        var plainResult = plain.ScanAsync(root, 0, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert(plainResult.Groups.Count == 1, "对照：未加保护时应识别为重复组");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

static Task AppDedupeAndFilter()
{
    var productCode = "11111111-2222-3333-4444-555555555555";
    var msi64 = new InstalledApp
    {
        AppKey = "msi:" + productCode,
        DisplayName = "7-Zip",
        Publisher = "Igor Pavlov",
        DisplayVersion = "23.01",
        Source = AppSource.Registry64,
        Kind = AppKind.Msi,
        UninstallCommand = $"MsiExec.exe /X{{{productCode}}}",
        IsUninstallable = true
    };
    var msi32 = msi64 with { Source = AppSource.Registry32, RequiresElevation = true };

    var deduped = InstalledAppService.Deduplicate(new[] { msi32, msi64 });
    Assert(deduped.Count == 1, $"同一 MSI 应去重为 1 条，实际 {deduped.Count}");
    Assert(deduped[0].Source == AppSource.Registry64, "去重应优先保留 64 位视图");

    var system = AppCatalog.ApplyPolicy(new InstalledApp
    {
        AppKey = "hidden.update",
        DisplayName = "Update for Windows",
        Source = AppSource.Registry64,
        Kind = AppKind.Exe,
        UninstallCommand = @"C:\Windows\update.exe",
        HiddenReasonKey = "Apps.Hidden.Update",
        IsSystemComponent = true
    });

    var visible = InstalledAppService.Filter(new[] { msi64, system }, includeHidden: false);
    Assert(visible.Count == 1, $"默认应隐藏系统组件，实际 {visible.Count}");
    var all = InstalledAppService.Filter(new[] { msi64, system }, includeHidden: true);
    Assert(all.Count == 2, "打开开关后系统组件应可显示");
    Assert(!system.IsUninstallable && system.BlockReasonKey == "Apps.Blocked.SystemComponent",
        "系统组件即使显示也不得提供卸载入口");

    return Task.CompletedTask;
}

static Task AppCommandParsing()
{
    var app = new InstalledApp
    {
        DisplayName = "My App",
        Kind = AppKind.Exe,
        IsUninstallable = true,
        UninstallCommand = "\"C:\\Program Files\\My App\\uninstall.exe\" /S /path:\"C:\\Temp Files\\data\""
    };

    var plan = AppUninstallService.BuildLaunchPlan(app, silent: false);
    Assert(plan is not null, "可卸载条目应生成启动计划");
    Assert(plan!.FileName == @"C:\Program Files\My App\uninstall.exe",
        $"带空格路径应解析为完整文件名，实际 {plan.FileName}");
    Assert(plan.Arguments.Count == 2, $"参数应为 2 个，实际 {plan.Arguments.Count}");
    Assert(plan.Arguments[0] == "/S", "第一个参数解析错误");
    Assert(plan.Arguments[1] == @"/path:C:\Temp Files\data",
        $"引号内参数应保留空格，实际 {plan.Arguments[1]}");
    Assert(!plan.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase),
        "不得通过 cmd.exe 执行应用命令");

    var rebuilt = WindowsCommandLine.QuoteArgument(plan.FileName) + " " +
                  WindowsCommandLine.Join(plan.Arguments);
    var round = WindowsCommandLine.Split(rebuilt);
    Assert(round.Count == 3 && round[0] == plan.FileName && round[1] == "/S" &&
           round[2] == plan.Arguments[1], "参数重新加引号后应可无损解析");

    var injection = app with
    {
        UninstallCommand = "\"C:\\Program Files\\My App\\uninstall.exe\" /S \"x & calc.exe\""
    };
    var injectionPlan = AppUninstallService.BuildLaunchPlan(injection, silent: false);
    Assert(injectionPlan is not null && injectionPlan.Arguments.Count == 2,
        "注入尝试应保持为独立参数，不得拆成多条命令");
    Assert(injectionPlan!.Arguments[1] == "x & calc.exe", "特殊字符应作为普通参数保留");
    Assert(!injectionPlan.FileName.Contains("&"), "文件名不得被注入特殊字符");

    return Task.CompletedTask;
}

static Task AppBlockPolicy()
{
    var defender = AppCatalog.ApplyPolicy(new InstalledApp
    {
        DisplayName = "Windows Defender",
        Kind = AppKind.Exe,
        UninstallCommand = @"C:\setup.exe",
        IsUninstallable = true
    });
    Assert(!defender.IsUninstallable && defender.BlockReasonKey == "Apps.Blocked.Security",
        "Windows 安全组件必须硬禁止");

    var runtime = AppCatalog.ApplyPolicy(new InstalledApp
    {
        DisplayName = "Microsoft .NET Runtime 8.0",
        Kind = AppKind.Exe,
        UninstallCommand = @"C:\dotnet.exe"
    });
    Assert(!runtime.IsUninstallable && runtime.BlockReasonKey == "Apps.Blocked.Runtime",
        ".NET 运行时必须硬禁止");

    var feature = AppCatalog.ApplyPolicy(new InstalledApp
    {
        DisplayName = "Windows Sandbox",
        Kind = AppKind.Exe,
        UninstallCommand = @"C:\feature.exe"
    });
    Assert(!feature.IsUninstallable && feature.BlockReasonKey == "Apps.Blocked.Feature",
        "Windows 可选功能必须硬禁止");

    var noEntry = AppCatalog.ApplyPolicy(new InstalledApp { DisplayName = "Tiny App" });
    Assert(!noEntry.IsUninstallable && noEntry.BlockReasonKey == "Apps.Blocked.NoEntry",
        "无官方入口必须不可卸载");
    Assert(AppUninstallService.BuildLaunchPlan(noEntry, silent: false) is null,
        "无入口条目不得生成任何启动计划");
    Assert(AppUninstallService.BuildLaunchPlan(defender, silent: false) is null,
        "硬禁止条目不得生成任何启动计划");

    var dependent = AppCatalog.ApplyPolicy(new InstalledApp
    {
        DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
        Publisher = "Microsoft Corporation",
        Kind = AppKind.Msi,
        UninstallCommand = "MsiExec.exe /X{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"
    });
    Assert(dependent.IsUninstallable, "普通 VC++ 发行包应允许通过官方入口卸载");
    Assert(dependent.IsDependent, "VC++ 发行包应标注依赖风险");

    return Task.CompletedTask;
}

static Task AppQuietAndOutcome()
{
    var productCode = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
    var app = new InstalledApp
    {
        DisplayName = "Sample",
        Kind = AppKind.Msi,
        IsUninstallable = true,
        UninstallCommand = $"MsiExec.exe /X{{{productCode}}}",
        QuietUninstallCommand = $"MsiExec.exe /X{{{productCode}}} /qn"
    };

    var quiet = AppUninstallService.BuildLaunchPlan(app, silent: true);
    Assert(quiet is not null && quiet.IsSilent, "应选择应用提供的静默命令");
    Assert(quiet!.Arguments.Contains("/qn"), "静默计划应保留 /qn 参数");
    var normal = AppUninstallService.BuildLaunchPlan(app, silent: false);
    Assert(normal is not null && !normal.IsSilent, "默认应选择普通命令");

    var noQuiet = app with { QuietUninstallCommand = string.Empty };
    Assert(AppUninstallService.BuildLaunchPlan(noQuiet, silent: true) is null,
        "没有静默入口时不得自行拼接参数");
    Assert(AppUninstallService.BuildLaunchPlan(noQuiet, silent: false) is not null,
        "普通入口仍应可用");

    Assert(AppUninstallService.DecideOutcome(0, false) == AppUninstallOutcome.Succeeded,
        "退出码 0 且已不存在才可报告成功");
    Assert(AppUninstallService.DecideOutcome(0, true) == AppUninstallOutcome.NotRemoved,
        "退出码 0 但仍存在必须报告未完成");
    Assert(AppUninstallService.DecideOutcome(1, true) == AppUninstallOutcome.ExitCodeFailed,
        "非 0 退出码不得报告成功");
    Assert(AppUninstallService.DecideOutcome(1, false) == AppUninstallOutcome.ExitCodeFailed,
        "非 0 退出码即使已消失也不得按成功处理");
    Assert(AppUninstallService.DecideOutcome(0, null) == AppUninstallOutcome.VerifyUnavailable,
        "无法验证时不得假定成功");

    return Task.CompletedTask;
}

static Task AppLeftoverSafety()
{
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    Assert(!AppLeftoverStore.IsSafeLocation(windows), "Windows 目录不得登记为残留");
    Assert(!AppLeftoverStore.IsSafeLocation(@"C:\"), "盘符根不得登记为残留");

    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    if (!string.IsNullOrWhiteSpace(programFiles))
        Assert(!AppLeftoverStore.IsSafeLocation(programFiles), "Program Files 共享根不得登记为残留");

    var root = NewTempDir();
    try
    {
        var sub = Path.Combine(root, "UninstalledApp");
        Directory.CreateDirectory(sub);
        Assert(AppLeftoverStore.IsSafeLocation(sub), "普通应用子目录应可作为残留候选登记");
    }
    finally { SafeDelete(root); }

    return Task.CompletedTask;
}

static Task AppxParsing()
{
    const string json = """
    [
      {"Name":"Microsoft.WindowsCalculator","PackageFullName":"Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.WindowsCalculator_8wekyb3d8bbwe","Publisher":"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond","Version":"11.2210.0.0","InstallLocation":"C:/Program Files/WindowsApps/Calculator","IsFramework":false,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":3},
      {"Name":"Microsoft.VCLibs.140.00","PackageFullName":"Microsoft.VCLibs.140.00_14.0.30704.0_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.VCLibs.140.00_8wekyb3d8bbwe","Publisher":"CN=Microsoft Corporation","Version":"14.0.30704.0","InstallLocation":"C:/Program Files/WindowsApps/VCLibs","IsFramework":true,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":4},
      {"Name":"LockedApp","PackageFullName":"LockedApp_1.0.0.0_x64__abc","PackageFamilyName":"LockedApp_abc","Publisher":"CN=Contoso","Version":"1.0.0.0","InstallLocation":"C:/Program Files/WindowsApps/Locked","IsFramework":false,"IsResourcePackage":false,"NonRemovable":true,"SignatureKind":4}
    ]
    """;

    var apps = InstalledAppService.ParseAppxPackages(json, CancellationToken.None);
    Assert(apps.Count == 3, $"应解析 3 个 MSIX 包，实际 {apps.Count}");

    var calculator = apps.Single(a => a.DisplayName == "Microsoft.WindowsCalculator");
    Assert(calculator.IsUninstallable, "普通商店应用应可卸载");
    Assert(calculator.Publisher == "Microsoft Corporation", "发布者应提取 CN 值");
    Assert(calculator.UninstallCommand.Contains("Remove-AppxPackage"), "应使用官方 Remove-AppxPackage 入口");

    var framework = apps.Single(a => a.DisplayName == "Microsoft.VCLibs.140.00");
    Assert(framework.HiddenByDefault, "框架包应默认隐藏");
    Assert(!framework.IsUninstallable && framework.BlockReasonKey == "Apps.Blocked.Framework",
        "框架包不得提供卸载入口");

    var locked = apps.Single(a => a.DisplayName == "LockedApp");
    Assert(locked.IsPreinstalled, "NonRemovable 应标为系统预装");
    Assert(!locked.IsUninstallable && locked.BlockReasonKey == "Apps.Blocked.NonRemovable",
        "不可移除的预装应用不得提供卸载入口");

    var visible = InstalledAppService.Filter(apps, includeHidden: false);
    Assert(visible.Count == 2, $"框架包应被默认隐藏，实际可见 {visible.Count}");
    return Task.CompletedTask;
}

static Task DiagnosticsPrivacy()
{
    const string profile = @"C:\Users\Alice";
    var redacted = PrivacyRedactor.Redact(
        @"C:\Users\Alice\Documents\secret.txt (owner: Alice)", "Alice", profile);
    Assert(!redacted.Contains("Alice", StringComparison.OrdinalIgnoreCase),
        "诊断文本不得保留用户名：" + redacted);
    Assert(redacted.Contains("<user>"), "用户名与用户目录应替换为 <user>");

    const string plain = @"C:\Temp\data\a.bin";
    Assert(PrivacyRedactor.Redact(plain, "Alice", profile) == plain,
        "不含用户信息的文本不得被改写");

    var root = NewTempDir();
    try
    {
        var log = Path.Combine(root, "run.log");
        File.WriteAllLines(log, Enumerable.Range(1, 250).Select(i => $"line {i}"));
        var tail = DiagnosticsService.ReadTail(log, DiagnosticsService.MaxLogLines);
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToList();

        Assert(lines.Count == 200, $"日志尾部应限制为 200 行，实际 {lines.Count}");
        Assert(lines[0] == "line 51" && lines[^1] == "line 250",
            $"日志尾部范围错误：{lines[0]} .. {lines[^1]}");
        Assert(DiagnosticsService.ReadTail(Path.Combine(root, "missing.log"), 200) == "",
            "不存在的日志应返回空内容");
    }
    finally { SafeDelete(root); }

    return Task.CompletedTask;
}

static async Task DiagnosticsExport()
{
    var root = NewTempDir();
    var zipPath = Path.Combine(root, "diagnostics.zip");
    try
    {
        var service = new DiagnosticsService();
        var result = await service.ExportAsync(zipPath, redact: true, CancellationToken.None);
        Assert(File.Exists(zipPath), "诊断 ZIP 未生成");
        Assert(result.Entries >= 7, $"诊断 ZIP 条目过少：{result.Entries}");

        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        foreach (var required in new[]
                 {
                     "README.txt", "system.txt", "settings.json", "hardware.json",
                     "cleanup-log.tail.jsonl", "app-ops.tail.jsonl", "run-log.tail.txt"
                 })
        {
            Assert(names.Contains(required), $"诊断 ZIP 缺少 {required}");
        }

        var systemEntry = zip.GetEntry("system.txt")!;
        using var reader = new StreamReader(systemEntry.Open());
        var systemText = reader.ReadToEnd();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert(!systemText.Contains(profile, StringComparison.OrdinalIgnoreCase),
            "诊断报告默认必须脱敏用户目录");
    }
    finally { SafeDelete(root); }
}

static ScanNode ScanOne(string filePath, string root)
{
    var info = new FileInfo(filePath);
    return new ScanNode(info.Name, info.FullName, NodeKind.File) { LogicalSize = info.Length };
}

static Task FileInfoPrivacy()
{
    const string fileName = "chrome.dll";
    var query = FileInfoCatalog.BuildQuery(fileName);

    Assert(query.Contains(fileName), "查询串应包含文件名");
    Assert(!query.Contains('\\') && !query.Contains('/'), "查询串不得包含路径分隔符");
    Assert(!query.Contains("Users", StringComparison.OrdinalIgnoreCase), "查询串不得包含用户目录");
    Assert(!query.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "查询串不得包含用户名");
    Assert(query.Length < 80, "查询串应只含文件名与固定问句");
    return Task.CompletedTask;
}

static Task FileInfoVerdict()
{
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var blocked = FileInfoCatalog.Evaluate(Path.Combine(windows, "explorer.exe"), "explorer.exe");
    Assert(blocked.Level == FileSafety.Blocked, $"系统目录下应为 L0，实际 {blocked.Level}");

    var pagefile = FileInfoCatalog.Evaluate(@"C:\pagefile.sys", "pagefile.sys");
    Assert(pagefile.Level == FileSafety.Blocked, "pagefile.sys 应为 L0");

    var dll = FileInfoCatalog.Evaluate(@"C:\Program Files\App\a.dll", "a.dll");
    Assert(dll.Level == FileSafety.Keep, $"dll 应为建议保留，实际 {dll.Level}");

    var exe = FileInfoCatalog.Evaluate(@"C:\Downloads\setup.exe", "setup.exe");
    Assert(exe.Level == FileSafety.ConfirmBeforeDelete, $"exe 应为先确认再删，实际 {exe.Level}");

    var unknown = FileInfoCatalog.Evaluate(@"C:\Temp\a.xyz", "a.xyz");
    Assert(unknown.Level == FileSafety.Unknown, $"未知扩展名应为 Unknown，实际 {unknown.Level}");
    Assert(FileInfoCatalog.Describe("a.xyz").Contains("没有内置说明"), "未知扩展名应明确说明无内置说明");
    return Task.CompletedTask;
}

// T57：FileIdInfo 必须匹配原生 FILE_ID_INFO（DWORD + 16 字节 ID = 20 字节）。
// 旧布局（8+8+8=24 字节）会让 VolumeSerial 混入文件 ID 字节并读出未初始化尾部。
static Task FileIdentityLayout()
{
    var root = NewTempDir();
    try
    {
        var a = Path.Combine(root, "a.bin");
        var b = Path.Combine(root, "b.bin");
        File.WriteAllBytes(a, new byte[64]);
        File.WriteAllBytes(b, new byte[64]);

        Assert(FileIdentityService.TryReadIdentity(a, out var id1, out var err1), "读取身份失败：" + err1);
        Assert(id1.IsValid, "身份应为有效值");
        // 卷序列号是 32 位 DWORD；若超出说明结构体布局与原生不一致（回归旧 24 字节布局）。
        Assert(id1.VolumeSerial <= uint.MaxValue,
            $"VolumeSerial={id1.VolumeSerial:X} 超出 32 位，FileIdInfo 布局与原生 FILE_ID_INFO 不一致");

        Assert(FileIdentityService.TryReadIdentity(a, out var id2, out _), "第二次读取身份失败");
        Assert(id1.ToPlanString() == id2.ToPlanString(), "同一文件两次读取身份必须一致");

        Assert(FileIdentityService.TryReadIdentity(b, out var id3, out _), "读取 b 身份失败");
        Assert(id1.ToPlanString() != id3.ToPlanString(), "不同文件身份必须不同");

        // 目录身份同样应满足布局约束（目录走 FileFlagBackupSemantics 路径）。
        Assert(FileIdentityService.TryReadIdentity(root, out var dirId, out _), "读取目录身份失败");
        Assert(dirId.IsValid && dirId.VolumeSerial <= uint.MaxValue, "目录身份布局校验失败");
    }
    finally { SafeDelete(root); }
    return Task.CompletedTask;
}

// T58：黄金分割布局必须面积严格正比于文件大小，块不越界且互不重叠。
static Task GoldenLayoutInvariants()
{
    var rect = new Rect(10, 20, 1200, 520);
    var cases = new[]
    {
        new long[] { 900_000, 400_000, 250_000, 120_000, 60_000, 30_000 },
        new long[] { 1_000_000, 1_000_000, 1_000_000, 1_000_000 },
        new long[] { 1_000_000_000, 1, 1, 1 },
        Enumerable.Range(0, 300).Select(i => (long)(1 + i * 7919 % 65536)).ToArray()
    };

    foreach (var sizes in cases)
    {
        var items = sizes.Select((size, i) => new ScanNode($"n{i}", $@"C:\bench\n{i}", NodeKind.File)
        {
            LogicalSize = size
        }).ToList();
        var boxes = TreemapView.GoldenLayout(items, rect);
        Assert(boxes.Count == items.Count, $"布局块数应为 {items.Count}，实际 {boxes.Count}");

        var total = sizes.Sum();
        foreach (var (node, box) in boxes)
        {
            Assert(box.Width > 0 && box.Height > 0, "块尺寸必须为正");
            Assert(box.Left >= rect.Left - 0.001 && box.Top >= rect.Top - 0.001 &&
                   box.Right <= rect.Right + 0.001 && box.Bottom <= rect.Bottom + 0.001, "块不得越界");

            var expected = rect.Width * rect.Height * node.LogicalSize / (double)total;
            var actual = box.Width * box.Height;
            Assert(Math.Abs(actual - expected) <= Math.Max(1.0, expected) * 0.002,
                $"面积应与大小成正比：{node.Name} 期望 {expected:0.###}，实际 {actual:0.###}");
        }

        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i].Rect;
                var b = boxes[j].Rect;
                var overlapW = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                var overlapH = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                Assert(overlapW <= 0.001 || overlapH <= 0.001,
                    $"块重叠：{boxes[i].Node.Name} 与 {boxes[j].Node.Name}");
            }
        }
    }
    return Task.CompletedTask;
}

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}

