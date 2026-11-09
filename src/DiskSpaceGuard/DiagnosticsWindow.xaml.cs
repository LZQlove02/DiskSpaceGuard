using System.Windows;
using DiskSpaceGuard.Services;
using Microsoft.Win32;

namespace DiskSpaceGuard;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsService _service = new();
    private bool _loading = true;

    public DiagnosticsWindow()
    {
        InitializeComponent();
        RedactCheck.IsChecked = true;
        _loading = false;
        RefreshPreview();
    }

    private void Redact_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        try
        {
            var preview = _service.BuildPreview(RedactCheck.IsChecked == true);
            PreviewBox.Text = preview.Text;
            FilesText.Text = string.Join("  ·  ", preview.IncludedFiles);
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = F("Diag.PreviewFailed", ex.Message);
            RunLog.WriteException("Diagnostics preview", ex);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = T("Diag.Export"),
            Filter = T("Diag.ZipFilter"),
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"DiskSpaceGuard-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportButton.IsEnabled = false;
        RedactCheck.IsEnabled = false;
        StatusText.Text = T("Diag.Exporting");

        try
        {
            var result = await _service.ExportAsync(dialog.FileName,
                RedactCheck.IsChecked == true, CancellationToken.None);
            StatusText.Text = F("Diag.Exported", result.Entries);
            var message = F("Diag.ExportSuccess", result.Path, result.Entries);
            if (!string.IsNullOrWhiteSpace(result.HardwareError))
                message += "\n" + F("Diag.HardwareFailed", result.HardwareError);
            MessageBox.Show(this, message, T("Diag.Title"), MessageBoxButton.OK,
                string.IsNullOrWhiteSpace(result.HardwareError)
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            RunLog.WriteException("Diagnostics export", ex);
            StatusText.Text = F("Diag.ExportFailed", ex.Message);
            MessageBox.Show(this, StatusText.Text, T("Diag.Title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            RedactCheck.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private static string F(string key, params object?[] args) => string.Format(T(key), args);
}
