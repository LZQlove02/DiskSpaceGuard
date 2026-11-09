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
            level = _protection.Evaluate(rule.TargetPattern, rootInfo.Attributes, out _);
            lastWrite = rootInfo.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            level = ProtectionLevel.L3;
        }

        var cutoff = DateTime.UtcNow - rule.MinAge;

        void Walk(string dir)
        {
            token.ThrowIfCancellationRequested();
            if (files >= MaxItemsPerRule)
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
                    Walk(entry.FullName);
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
            Walk(rule.TargetPattern);
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
