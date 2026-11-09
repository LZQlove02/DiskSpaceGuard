using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
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
            SetColors("#F1F5F9", "#475569");
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
        var (background, foreground) = level switch
        {
            FileSafety.Blocked => ("#FEE2E2", "#B91C1C"),
            FileSafety.Keep or FileSafety.ConfirmBeforeDelete or FileSafety.Unknown => ("#FEF3C7", "#B45309"),
            _ => ("#DCFCE7", "#15803D")
        };
        SetColors(background, foreground);
    }

    private void SetColors(string background, string foreground)
    {
        VerdictBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        VerdictText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
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
