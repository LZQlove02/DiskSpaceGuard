using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DiskSpaceGuard.Controls;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DiskSpaceGuard;

public sealed record HardwareDiskRow(PhysicalDiskInfo Disk)
{
    public string Name => string.IsNullOrWhiteSpace(Disk.Model)
        ? LocalizationService.Text("HW.UnnamedDisk", Loc.Instance.Language)
        : Disk.Model;
    public string Bus => LocalizationService.Text(HardwareCatalog.BusTypeKey(Disk.BusType), Loc.Instance.Language);
    public string Media => LocalizationService.Text(HardwareCatalog.MediaTypeKey(Disk.MediaType), Loc.Instance.Language);
    public string Health => LocalizationService.Text(HardwareCatalog.HealthKey(Disk.HealthStatus), Loc.Instance.Language);
    public string Size => Disk.Size > (ulong)long.MaxValue
        ? ScanNode.FormatBytes(long.MaxValue)
        : ScanNode.FormatBytes((long)Disk.Size);
}

public sealed record HardwareMetricRow(string Label, string Value, string Note);

public sealed class DuplicateFileRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public DuplicateFileRow(DuplicateGroup group, DuplicateFile file, bool recommended)
    {
        Group = group;
        File = file;
        IsRecommended = recommended;
    }

    public DuplicateGroup Group { get; }
    public DuplicateFile File { get; }
    public bool IsRecommended { get; }
    public bool IsDeletable => !File.Unstable;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Path => File.Path;
    public string SizeText => ScanNode.FormatBytes(File.Size);
    public string ModifiedText => File.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Flags
    {
        get
        {
            var flags = new List<string>();
            if (File.ReadOnly) flags.Add(Localized("Dup.ReadOnly"));
            if (File.System) flags.Add(Localized("Dup.System"));
            if (File.Unstable) flags.Add(Localized("Dup.Unstable"));
            return string.Join(" · ", flags);
        }
    }

    public string Note => IsRecommended ? Localized("Dup.Recommended") : string.Empty;

    private static string Localized(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record DuplicateGroupRow(DuplicateGroup Group)
{
    public string Title => string.Format(
        LocalizationService.Text("Dup.GroupTitle", Loc.Instance.Language),
        ScanNode.FormatBytes(Group.Size), Group.Files.Count);

    public string WastedText
    {
        get
        {
            var stable = Group.Files.Count(f => !f.Unstable);
            return string.Format(
                LocalizationService.Text("Dup.Wasted", Loc.Instance.Language),
                ScanNode.FormatBytes(Group.Size * Math.Max(0, stable - 1)));
        }
    }
}

public sealed class InstalledAppRow
{
    public InstalledAppRow(InstalledApp app) => App = app;

    public InstalledApp App { get; }

    public string Name => string.IsNullOrWhiteSpace(App.DisplayName)
        ? Localized("Apps.Unnamed")
        : App.DisplayName;

    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher)
        ? "—"
        : AppCatalog.SimplifyPublisher(App.Publisher);

    public string Version => string.IsNullOrWhiteSpace(App.DisplayVersion) ? "—" : App.DisplayVersion;

    public string InstallDateText => App.InstallDate?.ToString("yyyy-MM-dd") ?? "—";

    public string SizeText => App.EstimatedSizeBytes is { } bytes
        ? "≈ " + ScanNode.FormatBytes(bytes)
        : "—";

    public string SourceText => Localized("Apps.Source." + App.Source);

    public string ActionText => Localized("Apps.Uninstall");

    public bool CanUninstall => App.IsUninstallable;

    public string StatusText
    {
        get
        {
            if (!App.IsUninstallable)
            {
                return Localized(string.IsNullOrWhiteSpace(App.BlockReasonKey)
                    ? "Apps.Blocked.NoEntry"
                    : App.BlockReasonKey);
            }

            var flags = new List<string> { Localized("Apps.Status.Uninstallable") };
            if (App.RequiresElevation) flags.Add(Localized("Apps.Flag.Elevation"));
            if (App.IsDependent) flags.Add(Localized("Apps.Flag.Dependent"));
            if (App.IsDeveloperTool) flags.Add(Localized("Apps.Flag.Dev"));
            if (App.IsPreinstalled) flags.Add(Localized("Apps.Flag.Preinstalled"));
            return string.Join(" · ", flags);
        }
    }

    private static string Localized(string key)
        => LocalizationService.Text(key, Loc.Instance.Language);
}

public partial class MainWindow : Window
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private readonly DriveInventoryService _driveService = new();
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _deviceDebounceCts;
    private long _refreshGeneration;
    private bool _isClosing;
    private ScanService? _scanService;
    private CancellationTokenSource? _scanCts;
    private ScanResult? _scanResult;
    private ScanNode? _currentScanNode;
    private bool _analysisVolumesLoaded;
    private ChartKind _chartKind = ChartKind.Treemap;
    private TreemapColorMode _colorMode = TreemapColorMode.Type;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;
    private string _currentPage = "Overview";
    private ScanStats? _lastStats;
    private IReadOnlyList<DriveVolume> _volumes = Array.Empty<DriveVolume>();
    private string _lastVolumeStatusKey = "Refresh.Startup";
    private DateTime _lastVolumeStatusTime;
    private int _lastFailedVolumes;
    private readonly HardwareService _hardwareService = new();
    private HardwareSnapshot? _hardwareSnapshot;
    private bool _hardwareLoading;
    private CancellationTokenSource? _hardwareCts;
    private readonly DuplicateFinderService _duplicateService;
    private DuplicateScanResult? _duplicateResult;
    private CancellationTokenSource? _duplicateCts;
    private bool _duplicateVolumesLoaded;
    private string? _duplicateCustomRoot;
    private List<DuplicateFileRow> _duplicateRows = new();
    private readonly InstalledAppService _appService = new();
    private readonly AppUninstallService _appUninstallService = new();
    private InstalledAppSnapshot? _appSnapshot;
    private DateTime _appSnapshotUtc = DateTime.MinValue;
    private bool _appsLoading;
    private bool _appUninstallBusy;
    private CancellationTokenSource? _appsCts;

    private enum ChartKind
    {
        Treemap,
        Sunburst,
        Bars
    }

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        WireCharts();
        InitTray();
        _duplicateService = new DuplicateFinderService(_deleteService.Protection);
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        ApplyPage("Overview");
        UpdateScanHintForMode();
        UpdateScanStatusText(null);
        _deleteService.Protection.SetCustomRoots(SettingsStore.Load().ProtectedRoots);
        _ = RefreshVolumesAsync("Refresh.Startup");
    }

    private void WireCharts()
    {
        TreemapChart.DirectoryActivated += OnChartDirectory;
        TreemapChart.FileActivated += OnChartFile;
        SunburstChart.DirectoryActivated += OnChartDirectory;
        SunburstChart.FileActivated += OnChartFile;
        BarsChart.DirectoryActivated += OnChartDirectory;
        BarsChart.FileActivated += OnChartFile;
    }

    private void OnChartDirectory(ScanNode node)
    {
        if (node.Kind != NodeKind.Directory || !node.IsScanned) return;
        _currentScanNode = node;
        RenderScanNode();
        RenderChart();
    }

    private void OnChartFile(ScanNode node) => OpenFileInfo(node);

    /// <summary>高 DPI / 小屏下把窗口限制在工作区内，避免右侧内容被裁掉。</summary>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0) return;
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 32));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);
    }

    private void InitTray()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = T("Tray.Tip"),
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(T("Tray.Open"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(T("Tray.Exit"), null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private static string F(string key, params object?[] args) => string.Format(T(key), args);

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trayIcon is not null) _trayIcon.Text = T("Tray.Tip");
        if (_trayIcon?.ContextMenuStrip is { } menu && menu.Items.Count >= 3)
        {
            menu.Items[0].Text = T("Tray.Open");
            menu.Items[2].Text = T("Tray.Exit");
        }

        ApplyPage(_currentPage);
        UpdateVolumeTexts();
        DriveCards.ItemsSource = null;
        DriveCards.ItemsSource = _volumes;
        RenderHardware();
        RenderDuplicateResult();
        RenderApps();
        UpdateScanHintForMode();
        UpdateScanStatusText(_lastStats);
        RenderScanNode();
        RenderChart();
        UpdateNavigationSelection();
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    internal void PrepareForSystemShutdown() => _exitRequested = true;

    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        if (!SettingsStore.Load().MinimizeToTray) return;

        e.Cancel = true;
        Hide();
        if (!_trayHintShown && _trayIcon is not null)
        {
            _trayIcon.ShowBalloonTip(2000, "DiskSpaceGuard", T("Tray.Minimized"),
                WinForms.ToolTipIcon.Info);
            _trayHintShown = true;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Interlocked.Increment(ref _refreshGeneration);
        _deviceDebounceCts?.Cancel();
        _deviceDebounceCts?.Dispose();
        _scanService?.Cancel();
        _scanCts?.Cancel();
        _hardwareCts?.Cancel();
        _hardwareCts?.Dispose();
        _duplicateCts?.Cancel();
        _duplicateCts?.Dispose();
        _appsCts?.Cancel();
        _appsCts?.Dispose();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange &&
            (wParam.ToInt32() == DbtDeviceArrival || wParam.ToInt32() == DbtDeviceRemoveComplete))
            ScheduleDeviceRefresh();
        return IntPtr.Zero;
    }

    private void ScheduleDeviceRefresh()
    {
        _deviceDebounceCts?.Cancel();
        _deviceDebounceCts?.Dispose();
        _deviceDebounceCts = new CancellationTokenSource();
        var token = _deviceDebounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(900, token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    // 设备变化后同步刷新扫描卷下拉，避免仍列出已拔出的盘。
                    if (AnalysisPanel.Visibility == Visibility.Visible)
                    {
                        _analysisVolumesLoaded = false;
                        EnsureAnalysisVolumes();
                    }
                    if (DuplicatesPanel.Visibility == Visibility.Visible)
                    {
                        _duplicateVolumesLoaded = false;
                        EnsureDuplicateVolumes();
                    }
                    _ = RefreshVolumesAsync("Refresh.Device");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"Device refresh scheduling failed: {ex}"); }
        });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshVolumesAsync("Refresh.Manual");

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_deleteService.Protection) { Owner = this };
        if (dialog.ShowDialog() == true && _currentPage == "Apps")
        {
            _appSnapshotUtc = DateTime.MinValue;
            _ = LoadAppsAsync(force: true);
        }
    }

    private void Ledger_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CleanupLedgerWindow(_deleteService.LedgerPath) { Owner = this };
        dialog.ShowDialog();
    }

    private void CleanupRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CleanupRulesWindow(_deleteService) { Owner = this };
        dialog.ShowDialog();
    }

    private void UpdateVolumeTexts()
    {
        VolumeCountText.Text = F("Overview.VolumeCount", _volumes.Count);
        StatusText.Text = $"{T(_lastVolumeStatusKey)} · {_lastVolumeStatusTime:HH:mm:ss}" +
                          (_lastFailedVolumes > 0
                              ? F("Refresh.FailedCount", _lastFailedVolumes)
                              : string.Empty);
    }

    private async Task RefreshVolumesAsync(string statusKey)
    {
        if (_isClosing) return;
        var generation = Interlocked.Increment(ref _refreshGeneration);
        RefreshButton.IsEnabled = false;
        StatusText.Text = T("Refresh.Reading");
        try
        {
            // DriveInfo calls can block on removable/network volumes; keep them off the UI thread.
            var volumes = await Task.Run(_driveService.GetVolumes);
            if (_isClosing || generation != Interlocked.Read(ref _refreshGeneration)) return;
            _volumes = volumes;
            DriveCards.ItemsSource = volumes;
            _lastVolumeStatusKey = statusKey;
            _lastVolumeStatusTime = DateTime.Now;
            _lastFailedVolumes = volumes.Count(v => v.Status == "读取失败");
            UpdateVolumeTexts();
        }
        catch (Exception ex)
        {
            if (_isClosing || generation != Interlocked.Read(ref _refreshGeneration)) return;
            StatusText.Text = T("Refresh.Failed");
            RunLog.WriteException("Volume refresh", ex);
            MessageBox.Show(F("Refresh.FailedRead", ex.Message), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_isClosing && generation == Interlocked.Read(ref _refreshGeneration))
                RefreshButton.IsEnabled = true;
        }
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string page }) return;
        _currentPage = page;
        ApplyPage(page);
        if (page == "Analysis") EnsureAnalysisVolumes();
        UpdateNavigationSelection();
    }

    private void ApplyPage(string page)
    {
        var (titleKey, subKey) = page switch
        {
            "Analysis" => ("Page.Analysis.Title", "Page.Analysis.Sub"),
            "Duplicates" => ("Page.Duplicates.Title", "Page.Duplicates.Sub"),
            "Apps" => ("Page.Apps.Title", "Page.Apps.Sub"),
            "Hardware" => ("Page.Hardware.Title", "Page.Hardware.Sub"),
            _ => ("Page.Overview.Title", "Page.Overview.Sub")
        };

        PageTitle.Text = T(titleKey);
        PageSubtitle.Text = T(subKey);

        var overview = page == "Overview";
        var analysis = page == "Analysis";
        var hardware = page == "Hardware";
        var duplicates = page == "Duplicates";
        var apps = page == "Apps";
        var placeholder = !overview && !analysis && !hardware && !duplicates && !apps;
        OverviewPanel.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        AnalysisPanel.Visibility = analysis ? Visibility.Visible : Visibility.Collapsed;
        HardwarePanel.Visibility = hardware ? Visibility.Visible : Visibility.Collapsed;
        DuplicatesPanel.Visibility = duplicates ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = placeholder ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderTitle.Text = PageTitle.Text;
        PlaceholderDescription.Text = T("Placeholder.Pending");
        RefreshButton.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        if (hardware) EnsureHardwareLoaded();
        if (duplicates) EnsureDuplicateVolumes();
        if (apps) EnsureAppsLoaded();
    }

    /* ================= 硬件信息（M6） ================= */

    private void EnsureHardwareLoaded()
    {
        if (_hardwareSnapshot is not null || _hardwareLoading) return;
        _ = LoadHardwareAsync();
    }

    private void HardwareRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadHardwareAsync();

    private void HardwareDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticsWindow { Owner = this };
        dialog.ShowDialog();
    }

    private async Task LoadHardwareAsync()
    {
        _hardwareLoading = true;
        HardwareRefreshButton.IsEnabled = false;
        HardwareStatusText.Text = T("HW.Loading");

        _hardwareCts?.Cancel();
        _hardwareCts?.Dispose();
        _hardwareCts = new CancellationTokenSource();
        var token = _hardwareCts.Token;

        try
        {
            var snapshot = await _hardwareService.LoadAsync(token);
            if (token.IsCancellationRequested || _isClosing) return;
            _hardwareSnapshot = snapshot;
            RenderHardware();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunLog.WriteException("Hardware load", ex);
            HardwareStatusText.Text = string.Format(T("HW.LoadFailed"), ex.Message);
        }
        finally
        {
            _hardwareLoading = false;
            if (!_isClosing) HardwareRefreshButton.IsEnabled = true;
        }
    }

    private void RenderHardware()
    {
        var snapshot = _hardwareSnapshot;
        if (snapshot is null) return;

        var selectedId = (HardwareDiskList.SelectedItem as HardwareDiskRow)?.Disk.DeviceId;
        var rows = snapshot.Disks.Select(d => new HardwareDiskRow(d)).ToList();

        HardwareDiskList.ItemsSource = null;
        HardwareDiskList.ItemsSource = rows;

        HardwareStatusText.Text = snapshot.Error is not null
            ? string.Format(T("HW.LoadFailed"), snapshot.Error)
            : rows.Count == 0 ? T("HW.NoDisks") : string.Format(T("HW.Count"), rows.Count);

        if (rows.Count == 0)
        {
            HwTitleText.Text = T("HW.NoDisks");
            HwReliabilityNote.Text = string.Empty;
            HwReliabilityList.ItemsSource = null;
            HwPartitionList.ItemsSource = null;
            HwAdminHintText.Visibility = Visibility.Collapsed;
            return;
        }

        var index = selectedId is null ? 0 : rows.FindIndex(r => r.Disk.DeviceId == selectedId);
        HardwareDiskList.SelectedIndex = index >= 0 ? index : 0;

        HwAdminHintText.Text = T("HW.AdminHint");
        HwAdminHintText.Visibility = snapshot.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HardwareDiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HardwareDiskList.SelectedItem is not HardwareDiskRow row) return;
        RenderHardwareDetail(row.Disk);
    }

    private void RenderHardwareDetail(PhysicalDiskInfo disk)
    {
        HwTitleText.Text = string.IsNullOrWhiteSpace(disk.Model) ? T("HW.UnnamedDisk") : disk.Model;
        HwSerialText.Text = ValueOrDash(disk.Serial);
        HwFirmwareText.Text = ValueOrDash(disk.Firmware);
        HwBusText.Text = T(HardwareCatalog.BusTypeKey(disk.BusType));
        HwMediaText.Text = T(HardwareCatalog.MediaTypeKey(disk.MediaType));
        HwHealthText.Text = T(HardwareCatalog.HealthKey(disk.HealthStatus));
        HwSizeText.Text = ScanNode.FormatBytes(ToLong(disk.Size));
        HwLocationText.Text = ValueOrDash(disk.Location);

        var metrics = new List<HardwareMetricRow>();
        var reliability = disk.Reliability;
        if (reliability is null)
        {
            HwReliabilityNote.Text = T("HW.ReliabilityUnavailable") +
                (string.IsNullOrEmpty(disk.ReliabilityUnavailableReason)
                    ? string.Empty
                    : $"（{T(disk.ReliabilityUnavailableReason)}）");
        }
        else
        {
            var source = string.Format(T("HW.Source"), T("HW.SourceSmart"));
            AddMetric(metrics, "HW.Temp", reliability.TemperatureCelsius, "0.#", "°C", source);
            AddMetric(metrics, "HW.PowerOnHours", reliability.PowerOnHours, "N0", "h", source);
            AddMetric(metrics, "HW.PowerCycles", reliability.PowerCycleCount, "N0", string.Empty, source);
            AddMetric(metrics, "HW.ReadErrors", reliability.ReadErrors, "N0", string.Empty, source);
            AddMetric(metrics, "HW.WriteErrors", reliability.WriteErrors, "N0", string.Empty, source);

            var wear = HardwareCatalog.WearDisplay(reliability.WearPercent);
            if (wear is not null)
            {
                metrics.Add(new HardwareMetricRow(T("HW.Wear"), wear,
                    T("HW.WearNote") + (HardwareCatalog.WearExceedsHundred(reliability.WearPercent)
                        ? " " + string.Format(T("HW.Source"), T("HW.SourceSmart"))
                        : string.Empty)));
            }

            HwReliabilityNote.Text = metrics.Count == 0 ? T("HW.ReliabilityUnavailable") : string.Empty;
        }

        HwReliabilityList.ItemsSource = metrics;

        var partitions = disk.Partitions.Count == 0
            ? new List<string> { T("HW.NoPartitions") }
            : disk.Partitions.Select(p => string.Format(T("HW.PartitionLine"),
                p.PartitionNumber,
                string.IsNullOrEmpty(p.DriveLetter) ? "—" : p.DriveLetter + ":",
                ScanNode.FormatBytes(ToLong(p.Size)))).ToList();
        HwPartitionList.ItemsSource = partitions;
    }

    private void AddMetric(List<HardwareMetricRow> metrics, string labelKey,
        double? value, string format, string unit, string source)
    {
        if (value is null) return;
        var text = value.Value.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
        metrics.Add(new HardwareMetricRow(T(labelKey),
            string.IsNullOrEmpty(unit) ? text : $"{text} {unit}", source));
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private string ValueOrDash(string value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value;

    /* ================= 重复文件（M7） ================= */

    private void EnsureDuplicateVolumes()
    {
        if (_duplicateVolumesLoaded) return;
        _duplicateVolumesLoaded = true;
        _ = LoadDuplicateVolumesAsync();
    }

    private async Task LoadDuplicateVolumesAsync()
    {
        try
        {
            var volumes = await Task.Run(_driveService.GetVolumes);
            var ready = volumes.Where(v => v.Status == "正常").ToList();
            DupVolumeCombo.ItemsSource = ready;
            DupVolumeCombo.DisplayMemberPath = "Name";
            if (ready.Count > 0) DupVolumeCombo.SelectedIndex = 0;
            else DupStatusText.Text = T("Scan.NoReadyVolume");
        }
        catch (Exception ex)
        {
            DupStatusText.Text = F("Scan.VolumeListFailed", ex.Message);
        }
    }

    private long GetDuplicateMinSize()
        => long.TryParse((DupMinSizeCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var value)
            ? value
            : 1024 * 1024;

    private void DupBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = T("Dup.Browse"), Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _duplicateCustomRoot = dialog.FolderName;
        DupVolumeCombo.SelectedIndex = -1;
        DupStatusText.Text = F("Dup.RootFolder", _duplicateCustomRoot);
    }

    private void DupVolume_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DupVolumeCombo.SelectedItem is DriveVolume) _duplicateCustomRoot = null;
    }

    private async void DupStart_Click(object sender, RoutedEventArgs e)
    {
        var root = _duplicateCustomRoot;
        if (string.IsNullOrEmpty(root))
        {
            if (DupVolumeCombo.SelectedItem is not DriveVolume volume)
            {
                MessageBox.Show(this, T("Scan.SelectVolume"), "DiskSpaceGuard",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            root = volume.Name;
        }

        _duplicateCts?.Cancel();
        _duplicateCts?.Dispose();
        _duplicateCts = new CancellationTokenSource();
        var token = _duplicateCts.Token;

        DupStartButton.IsEnabled = false;
        DupCancelButton.IsEnabled = true;
        DupCleanButton.IsEnabled = false;
        DupGroupList.ItemsSource = null;
        DupFileGrid.ItemsSource = null;
        DupSelectionText.Text = string.Empty;
        DupProgress.Value = 0;
        DupStatusText.Text = F("Dup.Scanning", root);

        var minSize = GetDuplicateMinSize();
        var progress = new Progress<DuplicateScanProgress>(p =>
        {
            if (token.IsCancellationRequested) return;
            var phase = T(p.Phase == "sample" ? "Dup.Stage.Sample" : "Dup.Stage.Full");
            DupStatusText.Text = F("Dup.Hashing", phase, p.Hashed, p.Total);
            DupProgress.Value = p.Total > 0 ? Math.Clamp((double)p.Hashed / p.Total, 0, 1) : 0;
        });

        try
        {
            var result = await _duplicateService.ScanAsync(root, minSize, progress, token);
            if (token.IsCancellationRequested) return;

            _duplicateResult = result;
            RenderDuplicateResult();

            DupProgress.Value = 1;
            DupStatusText.Text =
                F("Dup.Completed", result.Groups.Count,
                    ScanNode.FormatBytes(result.Stats.WastedBytes),
                    result.Stats.Elapsed.TotalSeconds.ToString("0.0") + " s") +
                "\n" + F("Dup.Stats", result.Stats.Files, result.Stats.ExcludedSmall,
                    result.Stats.ExcludedCloud, result.Stats.ExcludedProtected, result.Stats.HardLinks,
                    result.Stats.ReadErrors, result.Stats.Unstable) +
                (result.Groups.Count == 0
                    ? "\n" + F("Dup.NoGroups", ScanNode.FormatBytes(minSize))
                    : string.Empty);
        }
        catch (OperationCanceledException)
        {
            DupStatusText.Text = T("Dup.Cancelled");
        }
        catch (Exception ex)
        {
            DupStatusText.Text = F("Dup.Failed", ex.Message);
        }
        finally
        {
            DupStartButton.IsEnabled = true;
            DupCancelButton.IsEnabled = false;
        }
    }

    private void DupCancel_Click(object sender, RoutedEventArgs e) => _duplicateCts?.Cancel();

    private void RenderDuplicateResult()
    {
        if (_duplicateResult is null) return;

        var rows = _duplicateResult.Groups.Select(g => new DuplicateGroupRow(g)).ToList();
        DupGroupList.ItemsSource = rows;
        DupFileGrid.ItemsSource = null;
        _duplicateRows = new List<DuplicateFileRow>();
        DupSelectionText.Text = string.Empty;
        DupCleanButton.IsEnabled = false;

        if (rows.Count > 0) DupGroupList.SelectedIndex = 0;
    }

    private void DupGroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DupGroupList.SelectedItem is not DuplicateGroupRow row) return;

        _duplicateRows = row.Group.Files
            .Select(f => new DuplicateFileRow(row.Group, f,
                string.Equals(f.Path, row.Group.RecommendedPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var fileRow in _duplicateRows)
            fileRow.PropertyChanged += (_, _) => UpdateDuplicateSelection();

        DupFileGrid.ItemsSource = _duplicateRows;
        UpdateDuplicateSelection();
    }

    private void DupFile_Checked(object sender, RoutedEventArgs e) => UpdateDuplicateSelection();

    private void UpdateDuplicateSelection()
    {
        var selected = _duplicateRows.Where(r => r.IsSelected && r.IsDeletable).ToList();
        DupSelectionText.Text = F("Dup.Selected", selected.Count,
            ScanNode.FormatBytes(selected.Sum(r => r.File.Size)));
        DupCleanButton.IsEnabled = selected.Count > 0;
    }

    private async void DupClean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _duplicateRows.Where(r => r.IsSelected && r.IsDeletable).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, T("Dup.SelectFirst"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var group = selected[0].Group;
        var stableCopies = group.Files.Count(f => !f.Unstable);
        if (selected.Count >= stableCopies)
        {
            MessageBox.Show(this, T("Dup.KeepOne"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 复用查重页的取消源：点「取消」或关闭窗口都能中止这次校验。
        if (_duplicateCts is null || _duplicateCts.IsCancellationRequested)
        {
            _duplicateCts?.Dispose();
            _duplicateCts = new CancellationTokenSource();
        }
        var verifyToken = _duplicateCts!.Token;
        DupCleanButton.IsEnabled = false;
        try
        {
            // 全量 SHA-256 可能读数十 GB，必须移出 UI 线程且可取消。
            var paths = selected.Select(r => r.File.Path).ToList();
            var verification = await Task.Run(
                () => _duplicateService.VerifyForDelete(group, paths, verifyToken));
            if (verification.Rejected.Count > 0)
            {
                MessageBox.Show(this, F("Dup.VerifyFailed", verification.Rejected.Count),
                    "DiskSpaceGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (verification.Verified.Count == 0) return;

            var nodes = verification.Verified
                .Select(f => new ScanNode(Path.GetFileName(f.Path), f.Path, NodeKind.File)
                {
                    LogicalSize = f.Size
                })
                .ToList();

            var rootHint = _duplicateCustomRoot ??
                (DupVolumeCombo.SelectedItem as DriveVolume)?.Name ?? string.Empty;
            var plan = _deleteService.BuildPlan(nodes, DeleteMode.RecycleFirst, rootHint);
            var dialog = new DeletePlanWindow(_deleteService, nodes, rootHint,
                recursive: false, initialPlan: plan, initialReport: null)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.Result is null) return;

            var result = dialog.Result;
            var success = result.Deleted + result.RecyclePending;
            MessageBox.Show(this,
                F("Cleanup.Success", success, result.RecyclePending) + "\n" +
                F("Cleanup.SkippedFailed", result.Skipped, result.Failed),
                T("Cleanup.ResultTitle"), MessageBoxButton.OK,
                result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            RemoveCleanedDuplicates(nodes.Select(n => n.FullPath).ToList());
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            DupCleanButton.IsEnabled = true;
        }
    }

    private void RemoveCleanedDuplicates(IReadOnlyCollection<string> attemptedPaths)
    {
        if (_duplicateResult is null) return;

        // 只对本轮真正尝试清理的路径做存在性检查；其余节点直接保留，
        // 避免删除后对整棵树逐文件 stat（100 万节点 ≈ 100 万次 File.Exists）。
        var attempted = new HashSet<string>(attemptedPaths, StringComparer.OrdinalIgnoreCase);
        var groups = _duplicateResult.Groups
            .Select(g => g with { Files = g.Files
                .Where(f => !attempted.Contains(f.Path) || File.Exists(f.Path)).ToList() })
            .Where(g => g.Files.Count(f => !f.Unstable) >= 2)
            .ToList();

        _duplicateResult = _duplicateResult with { Groups = groups };
        RenderDuplicateResult();
    }

    /* ================= 应用管理（M8） ================= */

    private void EnsureAppsLoaded()
    {
        if (_appSnapshot is not null &&
            DateTime.UtcNow - _appSnapshotUtc < TimeSpan.FromSeconds(60))
        {
            RenderApps();
            return;
        }
        _ = LoadAppsAsync(force: false);
    }

    private void AppRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadAppsAsync(force: true);

    private void AppFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _appSnapshot is null) return;
        RenderApps();
    }

    private async Task LoadAppsAsync(bool force)
    {
        if (_appsLoading || _isClosing) return;
        if (!force && _appSnapshot is not null &&
            DateTime.UtcNow - _appSnapshotUtc < TimeSpan.FromSeconds(60))
        {
            RenderApps();
            return;
        }

        _appsLoading = true;
        AppRefreshButton.IsEnabled = false;
        AppStatusText.Text = T("Apps.Loading");

        _appsCts?.Cancel();
        _appsCts?.Dispose();
        _appsCts = new CancellationTokenSource();
        var token = _appsCts.Token;

        try
        {
            var snapshot = await _appService.LoadAsync(token);
            if (token.IsCancellationRequested || _isClosing) return;
            _appSnapshot = snapshot;
            _appSnapshotUtc = DateTime.UtcNow;
            RenderApps();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RunLog.WriteException("Apps load", ex);
            if (!_isClosing) AppStatusText.Text = F("Apps.LoadFailed", ex.Message);
        }
        finally
        {
            _appsLoading = false;
            if (!_isClosing) AppRefreshButton.IsEnabled = true;
        }
    }

    private void RenderApps()
    {
        if (_appSnapshot is null) return;

        var includeHidden = SettingsStore.Load().ShowSystemComponents;
        IEnumerable<InstalledApp> apps = InstalledAppService.Filter(_appSnapshot.Apps, includeHidden);

        var search = AppSearchBox.Text.Trim();
        if (search.Length > 0)
        {
            apps = apps.Where(a =>
                a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var sourceTag = (AppSourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        if (sourceTag == "desktop") apps = apps.Where(a => a.Source != AppSource.Appx);
        else if (sourceTag == "store") apps = apps.Where(a => a.Source == AppSource.Appx);

        if (AppOnlyUninstallableCheck.IsChecked == true) apps = apps.Where(a => a.IsUninstallable);

        var sortTag = (AppSortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "size";
        apps = sortTag switch
        {
            "name" => apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            "installed" => apps.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => apps.OrderByDescending(a => a.EstimatedSizeBytes ?? 0)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        var rows = apps.Select(a => new InstalledAppRow(a)).ToList();
        AppGrid.ItemsSource = rows;

        var status = F("Apps.Count", _appSnapshot.Apps.Count, rows.Count,
            rows.Count(r => r.App.IsUninstallable));
        if (rows.Count == 0) status += " · " + T("Apps.Empty");
        if (!string.IsNullOrWhiteSpace(_appSnapshot.AppxError))
            status += "\n" + F("Apps.AppxFailed", _appSnapshot.AppxError);
        AppStatusText.Text = status;
    }

    private async void AppUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledAppRow row }) return;
        var app = row.App;

        if (!app.IsUninstallable)
        {
            MessageBox.Show(this, T("Apps.Result.NotUninstallable"), T("Apps.Result.Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_appUninstallBusy || _appUninstallService.IsBusy)
        {
            MessageBox.Show(this, T("Apps.Result.Busy"), T("Apps.Result.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = new UninstallConfirmWindow(app) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        _appUninstallBusy = true;
        AppRefreshButton.IsEnabled = false;
        AppGrid.IsEnabled = false;
        AppStatusText.Text = F("Apps.Uninstalling", row.Name);

        try
        {
            var result = await _appUninstallService.UninstallAsync(app, confirm.UseQuietMode,
                CancellationToken.None);
            if (_isClosing) return;
            ShowUninstallResult(app, result);
            _appSnapshotUtc = DateTime.MinValue;
            await LoadAppsAsync(force: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, F("Apps.LoadFailed", ex.Message), T("Apps.Result.Title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _appUninstallBusy = false;
            if (!_isClosing)
            {
                AppGrid.IsEnabled = true;
                AppRefreshButton.IsEnabled = true;
            }
        }
    }

    private void ShowUninstallResult(InstalledApp app, AppUninstallResult result)
    {
        var name = string.IsNullOrWhiteSpace(app.DisplayName) ? T("Apps.Unnamed") : app.DisplayName;
        var icon = MessageBoxImage.Information;
        string message;

        switch (result.Outcome)
        {
            case AppUninstallOutcome.Succeeded:
                message = F("Apps.Result.Success", name, result.ExitCode ?? 0);
                break;
            case AppUninstallOutcome.NotRemoved:
                message = F("Apps.Result.StillPresent", name);
                icon = MessageBoxImage.Warning;
                break;
            case AppUninstallOutcome.ExitCodeFailed:
                message = result.StillInstalled == true
                    ? F("Apps.Result.ExitCodePresent", result.ExitCode ?? 0, name)
                    : F("Apps.Result.ExitCode", result.ExitCode ?? 0, name);
                icon = MessageBoxImage.Warning;
                break;
            case AppUninstallOutcome.VerifyUnavailable:
                message = F("Apps.Result.VerifyUnavailable", result.ExitCode?.ToString() ?? "—");
                icon = MessageBoxImage.Warning;
                break;
            case AppUninstallOutcome.DeclinedElevation:
                message = T("Apps.Result.Declined");
                break;
            case AppUninstallOutcome.StillRunning:
                message = T("Apps.Result.StillRunning");
                icon = MessageBoxImage.Warning;
                break;
            case AppUninstallOutcome.Busy:
                message = T("Apps.Result.Busy");
                icon = MessageBoxImage.Warning;
                break;
            case AppUninstallOutcome.NotUninstallable:
                message = T("Apps.Result.NotUninstallable");
                icon = MessageBoxImage.Warning;
                break;
            case AppUninstallOutcome.LaunchFailed:
                message = F("Apps.Result.LaunchFailed", result.Detail ?? "—");
                icon = MessageBoxImage.Warning;
                break;
            default:
                message = T("Apps.Result.Cancelled");
                break;
        }

        if (result.Outcome == AppUninstallOutcome.Succeeded &&
            !string.IsNullOrWhiteSpace(result.ResidualPath))
        {
            message += "\n" + F("Apps.Result.Residual", result.ResidualPath);
        }

        MessageBox.Show(this, message, T("Apps.Result.Title"), MessageBoxButton.OK, icon);
    }

    private void UpdateNavigationSelection()
    {
        foreach (var button in FindNavigationButtons())
        {
            var isSelected = string.Equals(button.Tag as string, _currentPage, StringComparison.Ordinal);
            button.Background = isSelected ? ThemeBrush("NavSelectedBrush") : Brushes.Transparent;
            button.Foreground = isSelected ? ThemeBrush("NavSelectedTextBrush") : ThemeBrush("NavTextBrush");
            button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /* ================= 空间分析 / M2 扫描 ================= */

    private void EnsureAnalysisVolumes()
    {
        if (_analysisVolumesLoaded) return;
        _analysisVolumesLoaded = true;
        _ = LoadScanVolumesAsync();
    }

    private async Task LoadScanVolumesAsync()
    {
        try
        {
            var volumes = await Task.Run(_driveService.GetVolumes);
            var ready = volumes.Where(v => v.Status == "正常").ToList();
            ScanVolumeCombo.ItemsSource = ready;
            ScanVolumeCombo.DisplayMemberPath = "Name";
            if (ready.Count > 0) ScanVolumeCombo.SelectedIndex = 0;
            else ScanHintText.Text = T("Scan.NoReadyVolume");
        }
        catch (Exception ex)
        {
            ScanHintText.Text = F("Scan.VolumeListFailed", ex.Message);
        }
    }

    private void ScanModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ScanDepthCombo.IsEnabled = ScanStartButton.IsEnabled && GetSelectedMode() == ScanMode.Shallow;
        UpdateScanHintForMode();
    }

    private ScanMode GetSelectedMode()
    {
        var tag = (ScanModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "Shallow" => ScanMode.Shallow,
            "FastThenFull" => ScanMode.FastThenFull,
            _ => ScanMode.FullRecursive
        };
    }

    private int GetSelectedDepth()
    {
        var text = (ScanDepthCombo.SelectedItem as ComboBoxItem)?.Content as string;
        return int.TryParse(text, out var depth) ? depth : 1;
    }

    private async void ScanStart_Click(object sender, RoutedEventArgs e)
    {
        if (_scanService is { State: ScanTaskState.Preparing or ScanTaskState.Running or ScanTaskState.Pausing
            or ScanTaskState.Paused or ScanTaskState.Cancelling })
        {
            MessageBox.Show(T("Scan.AlreadyRunning"), "DiskSpaceGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ScanVolumeCombo.SelectedItem is not DriveVolume volume)
        {
            MessageBox.Show(T("Scan.SelectVolume"), "DiskSpaceGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mode = GetSelectedMode();
        var depth = GetSelectedDepth();
        var service = new ScanService();
        _scanService = service;
        _scanCts = new CancellationTokenSource();
        service.StateChanged += OnScanStateChanged;

        _scanResult = null;
        _currentScanNode = null;
        ScanResultList.ItemsSource = null;
        ScanNodeSummary.Text = string.Empty;
        ScanPathText.Text = T("Scan.Scanning");
        UpdateScanHintForMode();
        ApplyScanState(ScanTaskState.Preparing);

        try
        {
            var progress = new Progress<ScanStats>(UpdateScanStatus);
            var result = await service.ScanAsync(volume.Name, mode, depth, progress, _scanCts.Token);
            _scanResult = result;
            _currentScanNode = result.Root;
            if (_isClosing) return;
            RenderScanNode();
            ApplyScanState(result.Stats.State);
            UpdateScanStatus(result.Stats);
        }
        catch (Exception ex)
        {
            RunLog.WriteException("Scan", ex);
            ScanPathText.Text = T("Scan.Failed");
            ScanHintText.Foreground = ThemeBrush("DangerTextBrush");
            ScanHintText.Text = F("Scan.FailedDetail", ex.Message);
            ApplyScanState(ScanTaskState.Failed);
        }
        finally
        {
            service.StateChanged -= OnScanStateChanged;
            _scanService = null;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private void OnScanStateChanged(ScanTaskState state)
    {
        if (_isClosing) return;
        Dispatcher.InvokeAsync(() => ApplyScanState(state));
    }

    private void ApplyScanState(ScanTaskState state)
    {
        var active = state is ScanTaskState.Preparing or ScanTaskState.Running
            or ScanTaskState.Pausing or ScanTaskState.Paused or ScanTaskState.Cancelling;

        ScanStartButton.IsEnabled = !active;
        ScanPauseButton.IsEnabled = state is ScanTaskState.Running or ScanTaskState.Pausing or ScanTaskState.Paused;
        ScanStopButton.IsEnabled = state is ScanTaskState.Preparing or ScanTaskState.Running
            or ScanTaskState.Pausing or ScanTaskState.Paused;
        ScanPauseButton.Content = T(state == ScanTaskState.Paused ? "Scan.Resume" : "Scan.Pause");

        ScanVolumeCombo.IsEnabled = !active;
        ScanModeCombo.IsEnabled = !active;
        ScanDepthCombo.IsEnabled = !active && GetSelectedMode() == ScanMode.Shallow;

        ScanProgress.IsIndeterminate = state is ScanTaskState.Preparing or ScanTaskState.Running
            or ScanTaskState.Pausing;
        if (state == ScanTaskState.Paused) ScanProgress.IsIndeterminate = false;
    }

    private void ScanPause_Click(object sender, RoutedEventArgs e)
    {
        if (_scanService is null) return;
        var state = _scanService.State;
        if (state == ScanTaskState.Paused) _scanService.Resume();
        else if (state == ScanTaskState.Running) _scanService.Pause();
        else if (state == ScanTaskState.Pausing) _scanService.Resume();
    }

    private void ScanStop_Click(object sender, RoutedEventArgs e)
    {
        _scanService?.Cancel();
    }

    private void UpdateScanStatus(ScanStats stats)
    {
        if (_isClosing) return;
        _lastStats = stats;
        UpdateScanStatusText(stats);

        if (!string.IsNullOrEmpty(stats.Message))
        {
            ScanHintText.Foreground = ThemeBrush("DangerTextBrush");
            ScanHintText.Text = stats.Message;
            return;
        }

        UpdateScanHintForMode();
        if (!string.IsNullOrEmpty(stats.Phase))
            ScanHintText.Text = $"{ScanHintText.Text} ({stats.Phase})";
    }

    private void UpdateScanStatusText(ScanStats? stats)
    {
        if (_isClosing) return;
        ScanStatusText.Text = stats is null
            ? F("Scan.Status", T("Status.Idle"), "—", "—", "—", "0", "0", "0", "0", "0", "0")
            : F("Scan.Status",
                TaskStateText(stats.State), CompletenessText(stats.Completeness),
                PrecisionText(stats.Precision), ChangeText(stats.Change),
                stats.Files.ToString("N0"), stats.Directories.ToString("N0"),
                stats.AccessDenied.ToString("N0"), stats.ReparseSkipped.ToString("N0"),
                stats.OtherErrors.ToString("N0"), stats.ElapsedDisplay);
    }

    private void UpdateScanHintForMode()
    {
        if (!ScanStartButton.IsEnabled) return;
        ScanHintText.Foreground = (Brush)FindResource("SecondaryTextBrush");
        ScanHintText.Text = GetSelectedMode() switch
        {
            ScanMode.Shallow => F("Scan.Hint.Shallow", GetSelectedDepth()),
            ScanMode.FastThenFull => T("Scan.Hint.Fast"),
            _ => T("Scan.Hint.Full")
        };
    }

    private void RenderScanNode()
    {
        var node = _currentScanNode;
        if (node is null)
        {
            ScanPathText.Text = T("Scan.NotScanned");
            ScanNodeSummary.Text = string.Empty;
            ScanUpButton.IsEnabled = false;
            ScanResultList.ItemsSource = null;
            return;
        }

        ScanPathText.Text = node.FullPath;
        ScanNodeSummary.Text = F("Scan.Summary", node.SizeDisplay, node.Children.Count);
        ScanUpButton.IsEnabled = _scanResult is not null && !ReferenceEquals(node, _scanResult.Root);

        ScanResultList.ItemsSource = node.Children
            .OrderBy(c => c.Kind switch
            {
                NodeKind.Directory => 0,
                NodeKind.ReparsePoint => 1,
                _ => 2
            })
            .ThenByDescending(c => c.IsScanned)
            .ThenByDescending(c => c.LogicalSize)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RenderChart();
    }

    private void RenderChart()
    {
        var node = _currentScanNode;
        if (node is null)
        {
            TreemapChart.Root = null;
            SunburstChart.Root = null;
            BarsChart.Root = null;
            ChartSummary.Text = string.Empty;
            return;
        }

        // §6 降级：本层项数过多导致方块无法辨认时改用条形图；
        // 深层节点由 TreemapView 的 1500 块预算截断，并在图上提示，不整体降级。
        var total = CountNodes(node, 20000);
        var degrade = _chartKind == ChartKind.Treemap && node.Children.Count > 400;
        var kind = degrade ? ChartKind.Bars : _chartKind;

        TreemapChart.Visibility = kind == ChartKind.Treemap ? Visibility.Visible : Visibility.Collapsed;
        SunburstChart.Visibility = kind == ChartKind.Sunburst ? Visibility.Visible : Visibility.Collapsed;
        BarsChart.Visibility = kind == ChartKind.Bars ? Visibility.Visible : Visibility.Collapsed;

        TreemapChart.ColorMode = _colorMode;
        SunburstChart.ColorMode = _colorMode;
        BarsChart.ColorMode = _colorMode;
        TreemapChart.MergeSmall = MergeSmallCheck.IsChecked == true;

        TreemapChart.Root = node;
        SunburstChart.Root = node;
        BarsChart.Root = node;

        ChartSummary.Text = degrade
            ? F("Chart.Downgraded", node.Children.Count)
            : F("Chart.Summary", node.Children.Count, total);
    }

    private static int CountNodes(ScanNode node, int limit)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            count += CountNodes(child, limit);
            if (count > limit) return count;
        }
        return count;
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _chartKind = tag switch
        {
            "sunburst" => ChartKind.Sunburst,
            "bars" => ChartKind.Bars,
            _ => ChartKind.Treemap
        };
        UpdateToggleButtons();
        RenderChart();
    }

    private void ColorMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _colorMode = tag switch
        {
            "depth" => TreemapColorMode.Depth,
            "size" => TreemapColorMode.Size,
            _ => TreemapColorMode.Type
        };
        UpdateToggleButtons();
        RenderChart();
    }

    private void UpdateToggleButtons()
    {
        UpdateToggle(TreeViewButton, _chartKind == ChartKind.Treemap);
        UpdateToggle(SunViewButton, _chartKind == ChartKind.Sunburst);
        UpdateToggle(BarViewButton, _chartKind == ChartKind.Bars);
        UpdateToggle(ColorTypeButton, _colorMode == TreemapColorMode.Type);
        UpdateToggle(ColorDepthButton, _colorMode == TreemapColorMode.Depth);
        UpdateToggle(ColorSizeButton, _colorMode == TreemapColorMode.Size);
    }

    private static void UpdateToggle(Button button, bool selected)
    {
        button.Background = selected ? ThemeBrush("AccentBrush") : ThemeBrush("CardBrush");
        button.BorderBrush = selected ? ThemeBrush("AccentBrush") : ThemeBrush("BorderBrush");
        button.Foreground = selected ? ThemeBrush("OnAccentBrush") : ThemeBrush("PrimaryTextBrush");
    }

    private void MergeSmall_Changed(object sender, RoutedEventArgs e)
    {
        TreemapChart.MergeSmall = MergeSmallCheck.IsChecked == true;
        RenderChart();
    }

    private void OpenFileInfo(ScanNode node)
    {
        var window = new FileInfoWindow(node) { Owner = this };
        window.ShowDialog();
    }

    /* ================= 清理（M4） ================= */

    private readonly DeleteService _deleteService = new();

    private static bool IsDeletableSelection(ScanNode node)
    {
        if (node.IsAggregate || string.IsNullOrEmpty(node.FullPath)) return false;
        return node.Kind == NodeKind.File ||
               (node.Kind == NodeKind.Directory && node.IsScanned);
    }

    private void ScanResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = ScanResultList.SelectedItems.OfType<ScanNode>()
            .Where(IsDeletableSelection)
            .ToList();
        var dirs = selected.Count(n => n.Kind == NodeKind.Directory);
        DeleteSelectedButton.IsEnabled = selected.Count > 0;
        DeleteSelectedButton.Content = selected.Count == 0
            ? T("Scan.DeleteSelected")
            : dirs > 0
                ? F("Cleanup.SelectedWithDirs", selected.Count)
                : F("Cleanup.SelectedCount", selected.Count);
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var nodes = ScanResultList.SelectedItems.OfType<ScanNode>()
            .Where(IsDeletableSelection)
            .ToList();

        if (nodes.Count == 0)
        {
            MessageBox.Show(this, T("Cleanup.SelectFirst"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_scanService is { State: ScanTaskState.Running or ScanTaskState.Paused or ScanTaskState.Pausing })
        {
            MessageBox.Show(this, T("Scan.BusyCleanup"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rootHint = _currentScanNode?.FullPath ?? string.Empty;
        var recursive = nodes.Any(n => n.Kind == NodeKind.Directory);

        // 递归计划按磁盘现状展开；生成失败（超限/取消）时先提示，不打开预演窗口。
        DeletePlan? initialPlan = null;
        PlanBuildReport? buildReport = null;
        if (recursive)
        {
            // 递归展开目录会产生大量 CreateFileW/stat，必须移出 UI 线程。
            DeletePlan? built;
            PlanBuildReport report;
            string error;
            (built, report, error) = await Task.Run(() =>
            {
                var p = _deleteService.TryBuildPlan(nodes, DeleteMode.RecycleFirst, rootHint,
                    recursive: true, out var r, out var e);
                return (p, r, e);
            });
            initialPlan = built;
            if (initialPlan is null)
            {
                MessageBox.Show(this, error, T("Cleanup.PlanFailed"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            buildReport = report;
        }

        var dialog = new DeletePlanWindow(_deleteService, nodes, rootHint, recursive, initialPlan, buildReport)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        var result = dialog.Result;
        var success = result.Deleted + result.RecyclePending;
        var space = result.FreeSpaceDelta is { } delta
            ? "\n" + F("Cleanup.SpaceCheck",
                ScanNode.FormatBytes(result.FreeBytesBefore!.Value),
                ScanNode.FormatBytes(result.FreeBytesAfter!.Value),
                ScanNode.FormatBytes(System.Math.Abs(delta)),
                T(delta >= 0 ? "Cleanup.Released" : "Cleanup.NotReleased"))
            : string.Empty;
        var text = F("Cleanup.ModeLine", T(result.Mode == DeleteMode.RecycleFirst ? "Cleanup.ModeRecycle" : "Cleanup.ModePermanent")) + "\n" +
                   F("Cleanup.Success", success, result.RecyclePending) + "\n" +
                   F("Cleanup.SkippedFailed", result.Skipped, result.Failed) + "\n" +
                   F("Cleanup.Estimated", ScanNode.FormatBytes(result.EstimatedBytes)) +
                   (result.Mode == DeleteMode.RecycleFirst ? T("Cleanup.RecycleDelay") : string.Empty) +
                   space +
                   (result.FatalMessage is null ? string.Empty : $"\n\n{result.FatalMessage}");

        MessageBox.Show(this, text, T("Cleanup.ResultTitle"), MessageBoxButton.OK,
            result.Failed > 0 || result.FatalMessage is not null ? MessageBoxImage.Warning : MessageBoxImage.Information);

        RemoveDeletedNodes(result);
        RenderScanNode();
    }

    private void RemoveDeletedNodes(DeleteResult result)
    {
        if (_scanResult is null || result.Deleted + result.RecyclePending == 0) return;

        // 计划外或已变化的文件不会被删；按“当前是否仍存在”裁剪扫描树并重算大小。
        PruneMissingFiles(_scanResult.Root);
        RecalculateSizes(_scanResult.Root);
    }

    private static void PruneMissingFiles(ScanNode node)
    {
        if (node.Children.Count == 0) return;

        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            if (child.Kind == NodeKind.File && !child.IsAggregate && !string.IsNullOrEmpty(child.FullPath))
            {
                try
                {
                    if (!File.Exists(child.FullPath)) node.Children.RemoveAt(i);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                continue;
            }

            if (child.Kind == NodeKind.Directory && child.IsScanned) PruneMissingFiles(child);
        }
    }

    private static long RecalculateSizes(ScanNode node)
    {
        if (node.Kind == NodeKind.File) return node.LogicalSize;
        long sum = 0;
        foreach (var child in node.Children) sum += RecalculateSizes(child);
        node.LogicalSize = sum;
        return sum;
    }

    private void ScanUp_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || _currentScanNode is null) return;
        var parent = FindScanParent(_scanResult.Root, _currentScanNode);
        if (parent is null) return;
        _currentScanNode = parent;
        RenderScanNode();
    }

    private void ScanResultList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ScanResultList.SelectedItem is not ScanNode node) return;

        if (node.Kind != NodeKind.Directory || node.IsAggregate)
        {
            OpenFileInfo(node);
            return;
        }

        if (!node.IsScanned)
        {
            MessageBox.Show(T("Scan.Boundary"),
                "DiskSpaceGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _currentScanNode = node;
        RenderScanNode();
    }

    private static ScanNode? FindScanParent(ScanNode root, ScanNode target)
    {
        foreach (var child in root.Children)
        {
            if (ReferenceEquals(child, target)) return root;
            if (child.Children.Count == 0) continue;
            var found = FindScanParent(child, target);
            if (found is not null) return found;
        }
        return null;
    }

    private static string TaskStateText(ScanTaskState state) => state switch
    {
        ScanTaskState.Idle => T("Status.Idle"),
        ScanTaskState.Preparing => T("Status.Preparing"),
        ScanTaskState.Running => T("Status.Running"),
        ScanTaskState.Pausing => T("Status.Pausing"),
        ScanTaskState.Paused => T("Status.Paused"),
        ScanTaskState.Cancelling => T("Status.Cancelling"),
        ScanTaskState.Cancelled => T("Status.Cancelled"),
        ScanTaskState.Completed => T("Status.Completed"),
        ScanTaskState.Failed => T("Status.Failed"),
        _ => state.ToString()
    };

    private static string CompletenessText(ScanCompleteness completeness) => completeness switch
    {
        ScanCompleteness.Complete => T("Completeness.Complete"),
        ScanCompleteness.Partial => T("Completeness.Partial"),
        ScanCompleteness.Cancelled => T("Completeness.Cancelled"),
        ScanCompleteness.Failed => T("Completeness.Failed"),
        _ => "—"
    };

    private static string PrecisionText(SizePrecision precision) => precision switch
    {
        SizePrecision.Exact => T("Precision.Exact"),
        SizePrecision.Approximate => T("Precision.Approximate"),
        _ => T("Common.Unknown")
    };

    private static string ChangeText(ObjectChangeState change) => change switch
    {
        ObjectChangeState.Stable => T("Change.Stable"),
        ObjectChangeState.ChangedDuringScan => T("Change.Changed"),
        _ => T("Change.Unknown")
    };

    private IEnumerable<Button> FindNavigationButtons()
    {
        return FindButtons(this).Where(b => PageKeys.Contains((string)b.Tag!));
        static IEnumerable<Button> FindButtons(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button && button.Tag is string) yield return button;
                foreach (var nested in FindButtons(child)) yield return nested;
            }
        }
    }

    private static readonly HashSet<string> PageKeys = new(StringComparer.Ordinal)
    {
        "Overview", "Analysis", "Duplicates", "Apps", "Hardware"
    };

    /// <summary>统一走 ThemeService，避免各窗口各写一份资源查询。</summary>
    private static Brush ThemeBrush(string key) => ThemeService.ThemeBrush(key);
}
