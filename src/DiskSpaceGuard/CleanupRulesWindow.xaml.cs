using System.IO;
using System.Windows;
using System.Windows.Controls;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard;

public sealed record CleanupSuggestionRow(CleanupSuggestion Suggestion)
{
    public string RuleId => Suggestion.RuleId;
    public string Name => Suggestion.Name;
    public string Path => Suggestion.Path;
    public string Size => ScanNode.FormatBytes(Suggestion.Bytes);
    public string Files => Suggestion.FileCount.ToString("N0");
    public string Dirs => Suggestion.DirectoryCount.ToString("N0");
    public string Excluded => Suggestion.ExcludedCount.ToString("N0");
    public string Protection => Suggestion.Protection switch
    {
        ProtectionLevel.L0 => LocalizationService.Text("Level.L0", Loc.Instance.Language),
        ProtectionLevel.L1 => LocalizationService.Text("Level.L1", Loc.Instance.Language),
        ProtectionLevel.L2 => LocalizationService.Text("Level.L2", Loc.Instance.Language),
        ProtectionLevel.L4 => LocalizationService.Text("Level.L4", Loc.Instance.Language),
        _ => LocalizationService.Text("Level.L3", Loc.Instance.Language)
    };
}

public partial class CleanupRulesWindow : Window
{
    private readonly DeleteService _deleteService;
    private readonly CleanupRuleService _ruleService;
    private CancellationTokenSource? _cts;

    public CleanupRulesWindow(DeleteService deleteService)
    {
        InitializeComponent();
        _deleteService = deleteService;
        _ruleService = new CleanupRuleService(deleteService.Protection);
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
        };
    }

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        PreviewButton.IsEnabled = false;
        StatusText.Text = T("Rules.Statting");
        SuggestionList.ItemsSource = null;

        try
        {
            var suggestions = await _ruleService.EvaluateAsync(token);
            if (token.IsCancellationRequested) return;

            var rows = suggestions
                .OrderByDescending(s => s.Bytes)
                .Select(s => new CleanupSuggestionRow(s))
                .ToList();

            SuggestionList.ItemsSource = rows;
            StatusText.Text = rows.Count == 0
                ? T("Rules.None")
                : string.Format(T("Rules.Status"), rows.Count,
                    ScanNode.FormatBytes(rows.Sum(r => r.Suggestion.Bytes)));

            if (rows.Count > 0) SuggestionList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(T("Rules.Failed"), ex.Message);
        }
    }

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private void Reload_Click(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void SuggestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestionList.SelectedItem is not CleanupSuggestionRow row)
        {
            PreviewButton.IsEnabled = false;
            NoteText.Text = T("Rules.SelectHint");
            return;
        }

        var s = row.Suggestion;
        NoteText.Text = string.Format(T("Rules.NoteFormat"), s.Note, s.RuleId, row.Protection) +
                        (s.Protection is ProtectionLevel.L0 or ProtectionLevel.L1 or ProtectionLevel.L4
                            ? T("Rules.NoEntry")
                            : string.Empty);

        PreviewButton.IsEnabled = s.FileCount > 0 &&
            s.Protection is ProtectionLevel.L2 or ProtectionLevel.L3;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (SuggestionList.SelectedItem is not CleanupSuggestionRow row) return;
        var suggestion = row.Suggestion;

        if (suggestion.Protection is ProtectionLevel.L0 or ProtectionLevel.L1 or ProtectionLevel.L4)
        {
            MessageBox.Show(this, T("Rules.Protected"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PreviewButton.IsEnabled = false;
        try
        {
            // 只把「当前仍命中规则」的具体文件送进计划，不再 recursive 展开规则目录：
            // 活跃文件、计划外新增文件与目录本体都不会进入删除计划。
            var hitPaths = await Task.Run(
                () => _ruleService.GetMatchedFiles(suggestion, CancellationToken.None));
            if (hitPaths.Count == 0)
            {
                MessageBox.Show(this, T("Rules.None"), "DiskSpaceGuard",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var nodes = hitPaths
                .Select(p => new ScanNode(Path.GetFileName(p), p, NodeKind.File))
                .ToList();

            var plan = _deleteService.TryBuildPlan(nodes, DeleteMode.RecycleFirst,
                suggestion.Path, recursive: false, out var report, out var error);
            if (plan is null)
            {
                MessageBox.Show(this, error, T("Rules.PlanFailed"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new DeletePlanWindow(_deleteService, nodes, suggestion.Path,
                recursive: false, initialPlan: plan, initialReport: report)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;

            var success = result.Deleted + result.RecyclePending;
            MessageBox.Show(this,
                string.Format(T("Rules.Result"), suggestion.RuleId, suggestion.Name,
                    success, result.Skipped, result.Failed,
                    ScanNode.FormatBytes(result.EstimatedBytes),
                    result.Mode == DeleteMode.RecycleFirst ? T("Cleanup.RecycleDelay") : string.Empty),
                T("Rules.ResultTitle"), MessageBoxButton.OK,
                result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            _ = ReloadAsync();
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
