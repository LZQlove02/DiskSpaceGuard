using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard;

public partial class FileInfoWindow : Window
{
    private readonly ScanNode _node;
    private readonly bool _aggregate;

    public FileInfoWindow(ScanNode node)
    {
        InitializeComponent();
        _node = node;
        _aggregate = string.IsNullOrEmpty(node.FullPath);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
        };

        FileTitle.Text = node.Name;
        FilePath.Text = _aggregate ? T("FileInfo.AggregatePath") : node.FullPath;
        SizeText.Text = node.SizeDisplay;
        TimeText.Text = node.TimeDisplay;

        var ext = FileInfoCatalog.ExtensionOf(node.Name);
        TypeText.Text = ext.Length > 0 ? "." + ext : T("FileInfo.NoExtension");

        if (_aggregate)
        {
            VerdictLabel.Text = T("FileInfo.Aggregate");
            VerdictText.Text = T("FileInfo.AggregateDesc");
            // SoftBrush 上 SecondaryTextBrush 只有 4.47:1，改用更浅的 SubtleBrush（4.64:1）。
            SetColors("SubtleBrush", "SecondaryTextBrush");
        }
        else
        {
            var verdict = FileInfoCatalog.Evaluate(node.FullPath, node.Name);
            VerdictLabel.Text = verdict.Label;
            VerdictText.Text = verdict.Description;
            ApplyVerdictColor(verdict.Level);
        }

        KnowledgeText.Text = FileInfoCatalog.Describe(node.Name);
        QueryText.Text = _aggregate
            ? T("FileInfo.AggregateQuery")
            : FileInfoCatalog.BuildQuery(node.Name);

        BingButton.IsEnabled = BaiduButton.IsEnabled = CopyButton.IsEnabled = !_aggregate;
    }

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private void ApplyVerdictColor(FileSafety level)
    {
        var (backgroundKey, foregroundKey) = level switch
        {
            FileSafety.Blocked => ("DangerBgBrush", "DangerTextBrush"),          // 4.76:1
            FileSafety.Keep or FileSafety.ConfirmBeforeDelete or FileSafety.Unknown
                => ("WarningBgBrush", "WarningTextBrush"),                        // 6.28:1
            _ => ("SuccessBgBrush", "SuccessTextBrush")                           // 8.37:1
        };
        SetColors(backgroundKey, foregroundKey);
    }

    private void SetColors(string backgroundKey, string foregroundKey)
    {
        VerdictBox.Background = ThemeService.ThemeBrush(backgroundKey);
        VerdictText.Foreground = ThemeService.ThemeBrush(foregroundKey);
    }

    private void Bing_Click(object sender, RoutedEventArgs e) =>
        OpenSearch("https://www.bing.com/search?q=" + Uri.EscapeDataString(FileInfoCatalog.BuildQuery(_node.Name)));

    private void Baidu_Click(object sender, RoutedEventArgs e) =>
        OpenSearch("https://www.baidu.com/s?wd=" + Uri.EscapeDataString(FileInfoCatalog.BuildQuery(_node.Name)));

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_node.Name);
            CopyButton.Content = T("FileInfo.Copied");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyButton.Content = T("FileInfo.Copy");
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(T("FileInfo.CopyFailed"), ex.Message),
                "DiskSpaceGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSearch(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                string.Format(T("FileInfo.OpenFailed"), ex.Message, FileInfoCatalog.BuildQuery(_node.Name)),
                "DiskSpaceGuard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
