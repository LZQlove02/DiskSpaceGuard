using System.Diagnostics;
using System.IO;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Services;

/// <summary>具名清理规则：只做建议，不自动勾选，也不直接删除。</summary>
public sealed record CleanupRule(
    string RuleId,
    string NameZh,
    string Category,
    string TargetPattern,
    TimeSpan MinAge,
    string Description,
    string RuleVersion = "1.0");

public sealed record CleanupSuggestion(
    string RuleId,
    string Name,
    string Category,
    string Path,
    long Bytes,
    int FileCount,
    int DirectoryCount,
    int ExcludedCount,
    DateTime LastWriteUtc,
    ProtectionLevel Protection,
    string Note);

/// <summary>
/// §14.2 规则引擎：按具名规则统计候选对象。
/// 不使用“目录大 ⇒ 建议清理”的推断；每条建议都带 RuleId。
/// </summary>
public sealed class CleanupRuleService
{
    private const int MaxItemsPerRule = 200_000;

    /// <summary>递归目录树的深度上限；防止极深目录导致 StackOverflow（不可捕获）。</summary>
    private const int MaxTreeDepth = 256;

    private readonly ProtectionEvaluator _protection;
    private readonly IReadOnlyList<CleanupRule> _rules;

    public CleanupRuleService(ProtectionEvaluator? protection = null, IEnumerable<CleanupRule>? rules = null)
    {
        _protection = protection ?? new ProtectionEvaluator();
        _rules = (rules ?? BuildDefaultRules()).ToList();
    }

    public IReadOnlyList<CleanupRule> Rules => _rules;

    public Task<IReadOnlyList<CleanupSuggestion>> EvaluateAsync(CancellationToken token)
        => Task.Run(() => Evaluate(token), token);

    public IReadOnlyList<CleanupSuggestion> Evaluate(CancellationToken token)
    {
        var suggestions = new List<CleanupSuggestion>();
        foreach (var rule in _rules)
        {
            token.ThrowIfCancellationRequested();

            // 目标必须是精确目录，不存在的规则直接跳过。
            if (!Directory.Exists(rule.TargetPattern)) continue;

            var result = Measure(rule, token);
            if (result.FileCount == 0) continue;

            suggestions.Add(result);
        }

        return suggestions;
    }

    /// <summary>
    /// §14.2：返回「当前时刻仍命中规则」的具体文件路径。
    /// 预演必须基于这份清单生成删除计划，不能把规则目录整体 recursive 展开——
    /// 否则 MinAge 排除的活跃文件与规则目录本体都会进入计划（P0 误删路径）。
    /// </summary>
    public IReadOnlyList<string> GetMatchedFiles(CleanupSuggestion suggestion,
        CancellationToken token = default)
    {
        // 多条“应用残留”规则共用 RuleId=app.leftovers，必须同时按目标路径匹配，
        // 否则预演任意一条残留都会打开第一条残留的目录。
        var rule = _rules.FirstOrDefault(r =>
            string.Equals(r.RuleId, suggestion.RuleId, StringComparison.Ordinal) &&
            string.Equals(r.TargetPattern, suggestion.Path, StringComparison.OrdinalIgnoreCase));
        if (rule is null || !Directory.Exists(rule.TargetPattern)) return Array.Empty<string>();

        var cutoff = DateTime.UtcNow - rule.MinAge;
        var hits = new List<string>();

        void Walk(string dir, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (hits.Count >= MaxItemsPerRule || depth > MaxTreeDepth) return;

            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (hits.Count >= MaxItemsPerRule) return;

                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { Walk(entry.FullName, depth + 1); continue; }
                if (entry.LastWriteTimeUtc > cutoff) continue;

                // 比统计阶段更保守：受保护项不进入可删清单。
                var level = _protection.Evaluate(entry.FullName, attributes, out _);
                if (level is ProtectionLevel.L0 or ProtectionLevel.L1 or ProtectionLevel.L4) continue;

                hits.Add(entry.FullName);
            }
        }

        try { Walk(rule.TargetPattern, 0); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cleanup rule {rule.RuleId} matched-files failed: {ex}");
        }

        return hits;
    }

    private CleanupSuggestion Measure(CleanupRule rule, CancellationToken token)
    {
        long bytes = 0;
        var files = 0;
        var dirs = 0;
        var excluded = 0;
        var truncated = false;
        var lastWrite = DateTime.MinValue;

        ProtectionLevel level;
        try
        {
            var rootInfo = new DirectoryInfo(rule.TargetPattern);
            // 规则根本身或其祖先可能是 junction：按真实落点判定，避免把系统目录当缓存目录。
            var hasIdentity = FileIdentityService.TryReadIdentityWithFinalPath(
                rule.TargetPattern, out _, out var finalPath, out _);
            level = hasIdentity
                ? _protection.EvaluateWithFinalPath(rule.TargetPattern, rootInfo.Attributes, finalPath, out _)
                : _protection.Evaluate(rule.TargetPattern, rootInfo.Attributes, out _);
            lastWrite = rootInfo.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            level = ProtectionLevel.L3;
        }

        var cutoff = DateTime.UtcNow - rule.MinAge;

        void Walk(string dir, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (files >= MaxItemsPerRule || depth > MaxTreeDepth)
            {
                truncated = true;
                return;
            }

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                excluded++;
                return;
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    excluded++;
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    excluded++;
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Walk(entry.FullName, depth + 1);
                    dirs++;
                    continue;
                }

                if (entry.LastWriteTimeUtc > cutoff)
                {
                    excluded++;
                    continue;
                }

                try
                {
                    bytes += ((FileInfo)entry).Length;
                    files++;
                    if (entry.LastWriteTimeUtc > lastWrite) lastWrite = entry.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    excluded++;
                }
            }
        }

        try
        {
            Walk(rule.TargetPattern, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cleanup rule {rule.RuleId} failed: {ex}");
        }

        var note = truncated
            ? string.Format(Text("Rule.Note.Truncated"), MaxItemsPerRule.ToString("N0"))
            : rule.MinAge > TimeSpan.Zero
                ? string.Format(Text("Rule.Note.Age"), rule.MinAge.TotalHours.ToString("0.#"))
                : Text("Rule.Note.All");

        return new CleanupSuggestion(
            rule.RuleId,
            TextOr("Rule." + rule.RuleId, rule.NameZh),
            TextOr(CategoryKey(rule.Category), rule.Category),
            rule.TargetPattern,
            bytes,
            files,
            dirs,
            excluded,
            lastWrite,
            level,
            note);
    }

    private static string Text(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);

    private static string TextOr(string key, string fallback)
    {
        var text = Text(key);
        return text == key ? fallback : text;
    }

    private static string CategoryKey(string category) => category switch
    {
        "临时文件" => "Rule.Category.Temp",
        "浏览器缓存" => "Rule.Category.Browser",
        "系统缓存" => "Rule.Category.System",
        "诊断数据" => "Rule.Category.Diagnostics",
        "应用残留" => "Rule.Category.App",
        _ => category
    };

    private static IReadOnlyList<CleanupRule> BuildDefaultRules()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rules = new List<CleanupRule>();

        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(temp))
        {
            rules.Add(new CleanupRule(
                "user.temp",
                "用户临时文件",
                "临时文件",
                temp,
                TimeSpan.FromHours(24),
                "当前用户临时目录中超过 24 小时未修改的文件"));
        }

        AddCacheRule(rules, "browser.chrome", "Chrome 缓存", "浏览器缓存",
            Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"));
        AddCacheRule(rules, "browser.edge", "Edge 缓存", "浏览器缓存",
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"));
        AddCacheRule(rules, "cache.inet", "IE / 系统网络缓存", "系统缓存",
            Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"));
        AddCacheRule(rules, "cache.crash", "应用崩溃转储", "诊断数据",
            Path.Combine(localAppData, "CrashDumps"), TimeSpan.FromHours(1));

        // §15.4 卸载残留：仅登记“卸载已验证完成且目录仍存在”的路径；绝不自动删除。
        rules.AddRange(AppLeftoverStore.BuildRules());

        return rules;
    }

    private static void AddCacheRule(
        List<CleanupRule> rules, string ruleId, string name, string category, string path,
        TimeSpan? minAge = null)
    {
        rules.Add(new CleanupRule(
            ruleId,
            name,
            category,
            path,
            minAge ?? TimeSpan.FromHours(1),
            "具名缓存目录；仅统计超过 1 小时未修改的文件，避免命中正在使用的缓存"));
    }
}
