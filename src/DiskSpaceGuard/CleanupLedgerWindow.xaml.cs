using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;
using Microsoft.Win32;

namespace DiskSpaceGuard;

public sealed record LedgerEntryRow(CleanupLedgerEntry Entry)
{
    public string Time => Entry.ExecutedUtc == default
        ? "—"
        : Entry.ExecutedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string Mode => LocalizationService.Text(
        Entry.Mode == "Permanent" ? "Ledger.PermanentMode" : "Ledger.RecycleMode",
        Loc.Instance.Language);
    public string Success => (Entry.Deleted + Entry.RecyclePending).ToString("N0");
    public string Problems => $"{Entry.Skipped:N0} / {Entry.Failed:N0}";
}

public sealed record LedgerItemRow(CleanupLedgerItem Item)
{
    public string Path => Item.Path;
    public string Outcome => Item.Outcome;
    public string Verify => Item.Verify;
    public string Message => Item.Message;
}

public partial class CleanupLedgerWindow : Window
{
    private readonly string _ledgerPath;
    private IReadOnlyList<CleanupLedgerEntry> _entries = Array.Empty<CleanupLedgerEntry>();

    public CleanupLedgerWindow(string ledgerPath)
    {
        InitializeComponent();
        _ledgerPath = ledgerPath;
        Loaded += (_, _) => _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        StatusText.Text = T("Ledger.Reading");
        try
        {
            var path = _ledgerPath;
            var result = await Task.Run(() => CleanupLedgerReader.Read(path));
            _entries = result.Entries;

            EntriesList.ItemsSource = _entries
                .OrderByDescending(e => e.ExecutedUtc)
                .Select(e => new LedgerEntryRow(e))
                .ToList();

            StatusText.Text = result.Error is not null
                ? string.Format(T("Ledger.ReadFailed"), result.Error)
                : string.Format(T("Ledger.Count"), _entries.Count) +
                  (result.SkippedLines > 0
                      ? string.Format(T("Ledger.SkippedLines"), result.SkippedLines)
                      : string.Empty);

            FooterText.Text = string.Format(T("Ledger.File"), _ledgerPath);

            if (_entries.Count > 0) EntriesList.SelectedIndex = 0;
            else
            {
                EntryTitle.Text = T("Ledger.NoRecords");
                EntryMeta.Text = T("Ledger.NoRecordsHint");
                ItemsList.ItemsSource = null;
                RestoreButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(T("Ledger.ReadFailed"), ex.Message);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesList.SelectedItem is not LedgerEntryRow row) return;
        var entry = row.Entry;

        EntryTitle.Text = string.Format(T("Ledger.EntryTitle"), row.Time, row.Mode, row.Success);
        EntryMeta.Text = string.Format(T("Ledger.EntryMeta"),
            entry.PlanId.ToString()[..8], ShortHash(entry.PlanHash), entry.Total,
            entry.Skipped, entry.Failed,
            ScanNode.FormatBytes(entry.EstimatedBytes), ScanNode.FormatBytes(entry.ActualBytes)) +
            (string.IsNullOrEmpty(entry.Fatal) ? string.Empty : $"\n{entry.Fatal}");

        ItemsList.ItemsSource = entry.Items.Select(i => new LedgerItemRow(i)).ToList();
        RestoreButton.IsEnabled = RecycleBinRestoreService.RestorablePaths(entry).Count > 0;
    }

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not LedgerEntryRow row) return;
        var entry = row.Entry;

        if (entry.Mode == "Permanent")
        {
            MessageBox.Show(this, T("Ledger.RestoreOnlyRecycle"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var paths = RecycleBinRestoreService.RestorablePaths(entry);

        if (paths.Count == 0)
        {
            MessageBox.Show(this, T("Ledger.RestoreNoEntries"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, string.Format(T("Ledger.RestoreConfirm"), paths.Count),
                "DiskSpaceGuard", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        RestoreButton.IsEnabled = false;
        StatusText.Text = T("Ledger.Restoring");
        try
        {
            var result = await Task.Run(() => RecycleBinRestoreService.Restore(paths));
            StatusText.Text = string.Format(T("Ledger.RestoreResult"), result.Restored, result.Failed);

            var detail = result.AnyFailure
                ? "\n" + string.Join("\n", result.Items.Where(i => !i.Restored)
                    .Take(12).Select(i => $"{i.Path} — {i.Message}"))
                : string.Empty;
            MessageBox.Show(this,
                string.Format(T("Ledger.RestoreResult"), result.Restored, result.Failed) + detail,
                T("Ledger.RestoreTitle"), MessageBoxButton.OK,
                result.AnyFailure ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(T("Ledger.RestoreFailed"), ex.Message);
            MessageBox.Show(this, string.Format(T("Ledger.RestoreFailed"), ex.Message),
                T("Ledger.RestoreTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RestoreButton.IsEnabled = RecycleBinRestoreService.RestorablePaths(entry).Count > 0;
        }
    }

    private static string ShortHash(string hash)
        => string.IsNullOrEmpty(hash)
            ? "—"
            : hash.Length <= 20 ? hash : hash[..12] + "…" + hash[^8..];

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            MessageBox.Show(this, T("Ledger.NoExport"), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = T("Ledger.CsvFilter"),
            DefaultExt = ".csv",
            FileName = $"cleanup-log-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // 带 BOM，避免 Excel 打开中文为乱码。
            File.WriteAllText(dialog.FileName, CleanupLedgerCsv.Build(_entries),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = string.Format(T("Ledger.Exported"), dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, string.Format(T("Ledger.ExportFailed"), ex.Message), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_ledgerPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_ledgerPath}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(_ledgerPath);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(T("Ledger.OpenFailed"), ex.Message), "DiskSpaceGuard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
