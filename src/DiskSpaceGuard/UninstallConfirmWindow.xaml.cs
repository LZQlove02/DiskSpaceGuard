using System.Windows;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard;

public partial class UninstallConfirmWindow : Window
{
    private readonly InstalledApp _app;

    public UninstallConfirmWindow(InstalledApp app)
    {
        InitializeComponent();
        _app = app;

        HeadingText.Text = string.Format(T("Apps.Confirm.Heading"), DisplayName(app));
        MetaText.Text = string.Format(T("Apps.Confirm.Meta"),
            string.IsNullOrWhiteSpace(app.Publisher) ? "—" : app.Publisher,
            string.IsNullOrWhiteSpace(app.DisplayVersion) ? "—" : app.DisplayVersion,
            T("Apps.Source." + app.Source));
        LocationText.Text = string.Format(T("Apps.Confirm.Location"),
            string.IsNullOrWhiteSpace(app.InstallLocation) ? "—" : app.InstallLocation);

        DependentNote.Visibility = app.IsDependent ? Visibility.Visible : Visibility.Collapsed;
        DevNote.Visibility = app.IsDeveloperTool ? Visibility.Visible : Visibility.Collapsed;
        PreinstalledNote.Visibility = app.IsPreinstalled ? Visibility.Visible : Visibility.Collapsed;
        ElevationNote.Visibility = app.RequiresElevation && !AppUninstallService.IsElevated()
            ? Visibility.Visible
            : Visibility.Collapsed;
        QuietCheck.Visibility = app.HasQuietMode ? Visibility.Visible : Visibility.Collapsed;

        UpdateCommand();
    }

    /// <summary>确认后将执行的确切方式；静默模式只在应用提供 QuietUninstallString 时可用。</summary>
    public bool UseQuietMode => QuietCheck.Visibility == Visibility.Visible && QuietCheck.IsChecked == true;

    public string Command => UseQuietMode ? _app.QuietUninstallCommand : _app.UninstallCommand;

    private void QuietCheck_Changed(object sender, RoutedEventArgs e) => UpdateCommand();

    private void UpdateCommand()
    {
        CommandText.Text = Command;
        QuietWarn.Visibility = UseQuietMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (UseQuietMode)
        {
            var answer = MessageBox.Show(this,
                string.Format(T("Apps.Confirm.QuietAgain"), DisplayName(_app)),
                T("Apps.Confirm.QuietTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string DisplayName(InstalledApp app)
        => string.IsNullOrWhiteSpace(app.DisplayName) ? T("Apps.Unnamed") : app.DisplayName;

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);
}
