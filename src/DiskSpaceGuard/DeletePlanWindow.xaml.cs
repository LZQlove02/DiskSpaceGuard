using System.Windows;
using System.Windows.Controls;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard;

public partial class DeletePlanWindow : Window
{
    private DeletePlan _plan;
    private DeletePreview _preview;
    private readonly DeleteService _service;
    private readonly string _rootHint;
    private readonly IReadOnlyList<ScanNode> _nodes;
    private readonly bool _recursive;
    private PlanBuildReport? _buildReport;
    private CancellationTokenSource? _execCts;
    private volatile bool _executing;
    private int _execProcessed;

    public DeleteMode SelectedMode { get; private set; } = DeleteMode.RecycleFirst;
    public DeleteResult? Result { get; private set; }

    private sealed record Row(string Path, string Size, string Protection, ProtectionLevel Level, int ItemId);

    public DeletePlanWindow(
        DeleteService service,
        IReadOnlyList<ScanNode> nodes,
        string rootHint,
        bool recursive = false,
        DeletePlan? initialPlan = null,
        PlanBuildReport? initialReport = null)
    {
        InitializeComponent();
        _service = service;
        _nodes = nodes;
        _rootHint = rootHint;
        _recursive = recursive;
        _buildReport = initialReport;
        _plan = initialPlan ?? BuildPlan(DeleteMode.RecycleFirst);
        _preview = service.Preview(_plan);
        Bind(_plan, _preview);
    }

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private static string F(string key, params object?[] args) => string.Format(T(key), args);

    private DeletePlan BuildPlan(DeleteMode mode)
    {
        var plan = _service.TryBuildPlan(_nodes, mode, _rootHint, _recursive,
            out var report, out var error);
        if (plan is null) throw new InvalidOperationException(error);
        _buildReport = report;
        return plan;
    }

    private void Bind(DeletePlan plan, DeletePreview preview)
    {
        ItemsText.Text = F("Plan.Summary", preview.DeletableItems, preview.TotalItems);
        BytesText.Text = Models.ScanNode.FormatBytes(preview.DeletableBytes);
        // “需强确认”= L2；"禁止 / 跳过" = L0 + L1 + L4（执行时必然跳过）
        StrongText.Text = F("Plan.Scope", preview.StrongConfirmCount);
        BlockedText.Text = F("Plan.Scope", preview.BlockedCount + preview.ReparseCount + preview.CustomProtectedCount);

        ItemsList.ItemsSource = plan.Items.Select(i => new Row(
            i.DisplayPath,
            i.Kind == DeleteItemKind.Directory ? T("Plan.Dir") : Models.ScanNode.FormatBytes(i.SnapshotSize),
            i.Protection switch
            {
                ProtectionLevel.L0 => T("Plan.L0"),
                ProtectionLevel.L1 => T("Plan.L1"),
                ProtectionLevel.L2 => T("Plan.L2"),
                ProtectionLevel.L4 => T("Plan.L4"),
                _ => T("Plan.L3")
            },
            i.Protection,
            i.ItemId)).ToList();

        HashText.Text = F("Plan.Hash", plan.PlanHash[..16], plan.PlanHash[^8..],
                            plan.Items.Count,
                            plan.Items.Count(i => i.Kind == DeleteItemKind.File),
                            preview.DirectoryCount, plan.Mode) +
                        (preview.UnverifiableCount > 0
                            ? "\n" + F("Plan.Unverifiable", preview.UnverifiableCount)
                            : string.Empty) +
                        (preview.MissingCount + preview.ChangedCount > 0
                            ? "\n" + F("Plan.PreviewChanges", preview.MissingCount, preview.ChangedCount)
                            : string.Empty) +
                        (_recursive ? "\n" + T("Plan.RecursiveNote") : string.Empty) +
                        (_buildReport is { ReparseSkipped: > 0 }
                            ? "\n" + F("Plan.ReparseNote", _buildReport.ReparseSkipped)
                            : string.Empty) +
                        (_buildReport is { UnreadableDirectories: > 0 }
                            ? "\n" + F("Plan.UnreadableNote", _buildReport.UnreadableDirectories)
                            : string.Empty);
        ModeText.Text = T(plan.Mode == DeleteMode.RecycleFirst ? "Plan.RecycleHint" : "Plan.PermanentHint");
        RecycleNote.Visibility = plan.Mode == DeleteMode.RecycleFirst ? Visibility.Visible : Visibility.Collapsed;
        DangerNote.Visibility = plan.Mode == DeleteMode.Permanent ? Visibility.Visible : Visibility.Collapsed;

        // L2 强确认：必须输入目标文件夹名；跨多个不同父目录时需逐一输入全部名称。
        var strongItems = plan.Items.Where(i => i.Protection == ProtectionLevel.L2).ToList();
        if (strongItems.Count > 0)
        {
            var folders = strongItems
                .Select(i => System.IO.Path.GetFileName(
                    System.IO.Path.GetDirectoryName(i.NormalizedPath) ?? string.Empty))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders.Count == 0) folders.Add(string.Empty);
            StrongHint.Text = F("Plan.L2Hdiff", strongItems.Count, string.Join(", ", folders));
            StrongPanel.Visibility = Visibility.Visible;
            StrongInput.Tag = string.Join(",", folders);
        }
        else
        {
            StrongPanel.Visibility = Visibility.Collapsed;
            StrongInput.Clear();
            StrongInput.Tag = null;
        }

        ExecuteButton.IsEnabled = preview.DeletableItems > 0;
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var tag = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        SelectedMode = tag == "permanent" ? DeleteMode.Permanent : DeleteMode.RecycleFirst;
        try
        {
            _plan = BuildPlan(SelectedMode);
            _preview = _service.Preview(_plan);
            Bind(_plan, _preview);
        }
        catch (InvalidOperationException ex)
        {
            ExecuteButton.IsEnabled = false;
            MessageBox.Show(this, ex.Message, T("Cleanup.PlanFailed"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (StrongPanel.Visibility == Visibility.Visible)
        {
            // 多个不同父目录时需全部输入（顺序不限，逗号/顿号分隔）。
            var expected = (StrongInput.Tag as string ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var typed = StrongInput.Text
                .Split(new[] { ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matched = typed.Length == expected.Length
                && !expected.Except(typed, StringComparer.OrdinalIgnoreCase).Any();
            if (!matched)
            {
                MessageBox.Show(this, T("Plan.L2Mismatch"), "DiskSpaceGuard",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var scope = _preview.DirectoryCount > 0
            ? F("Plan.ScopeDir", _preview.DeletableItems, _preview.DirectoryCount)
            : F("Plan.Scope", _preview.DeletableItems);
        var bytes = Models.ScanNode.FormatBytes(_preview.DeletableBytes);
        var confirm = MessageBox.Show(this,
            SelectedMode == DeleteMode.Permanent
                ? F("Plan.ConfirmPermanent", scope, bytes)
                : F("Plan.ConfirmRecycle", scope, bytes),
            "DiskSpaceGuard", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        // 进入执行态：展示逐项进度，允许随时停止（已完成的操作不会回滚）。
        ExecuteButton.IsEnabled = false;
        ModeCombo.IsEnabled = false;
        StrongInput.IsEnabled = false;
        CancelButton.Content = T("Plan.Stop");
        ExecPanel.Visibility = Visibility.Visible;
        _execProcessed = 0;
        ExecProgress.Maximum = Math.Max(1, _plan.Items.Count);
        ExecProgress.Value = 0;
        ExecStatus.Text = F("Plan.ExecProgress", 0, _plan.Items.Count);

        // 进度已被节流，不能按回调次数累加；改用结果自带的 ItemId 保持计数准确。
        var progress = new Progress<DeleteItemResult>(r =>
        {
            _execProcessed = Math.Max(_execProcessed, r.ItemId);
            ExecProgress.Value = _execProcessed;
            ExecStatus.Text = F("Plan.ExecProgress", _execProcessed, _plan.Items.Count);
        });
        var state = new DeleteRunState { PlanId = _plan.PlanId, PlanHashAtStart = _plan.PlanHash };
        _executing = true;
        _execCts = new CancellationTokenSource();
        try
        {
            Result = await _service.ExecuteAsync(_plan, state, progress, _execCts.Token);
            // 节流可能让最后一次回调尚未到达，收尾时把进度补到终值。
            _execProcessed = _plan.Items.Count;
            ExecProgress.Value = _execProcessed;
            ExecStatus.Text = F("Plan.ExecProgress", _execProcessed, _plan.Items.Count);
        }
        finally
        {
            _executing = false;
            _execCts.Dispose();
            _execCts = null;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_executing)
        {
            try { _execCts?.Cancel(); } catch (ObjectDisposedException) { }
            CancelButton.IsEnabled = false;
            return;
        }
        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 执行中不允许关闭：中止等待会丢失结果窗口，停止按钮才是取消入口。
        if (_executing)
        {
            e.Cancel = true;
            MessageBox.Show(this, T("Plan.ClosingBlocked"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
