using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DiskSpaceGuard.Services;
using Microsoft.Win32;

namespace DiskSpaceGuard;

public partial class SettingsWindow : Window
{
    private readonly ProtectionEvaluator _protection;
    private readonly List<string> _roots;
    private readonly SettingsStore.AppSettings _settings;
    private readonly UiLanguage _originalLanguage;
    private bool _loadingLanguage = true;

    public SettingsWindow(ProtectionEvaluator protection)
    {
        InitializeComponent();
        _protection = protection;
        _roots = protection.CustomProtectedRoots.ToList();
        _settings = SettingsStore.Load();
        _originalLanguage = Loc.Instance.Language;
        TrayCheck.IsChecked = _settings.MinimizeToTray;
        ThemeCheck.IsChecked = _settings.FollowSystemTheme;
        ShowSystemCheck.IsChecked = _settings.ShowSystemComponents;

        LanguageCombo.SelectedIndex = _settings.Language == "en-US" ? 1 : 0;
        _loadingLanguage = false;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.Instance.PropertyChanged -= OnLanguageChanged;
        Refresh();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Refresh();

    private void Refresh()
    {
        RootList.ItemsSource = null;
        RootList.ItemsSource = _roots.ToList();
        HintText.Text = _roots.Count == 0
            ? LocalizationService.Text("Settings.NoRoots", Loc.Instance.Language)
            : string.Format(LocalizationService.Text("Settings.RootCount", Loc.Instance.Language), _roots.Count);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Text("Settings.PickFolder", Loc.Instance.Language),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var normalized = ProtectionEvaluator.Normalize(dialog.FolderName);
        if (_roots.Any(r => string.Equals(r, normalized, System.StringComparison.OrdinalIgnoreCase)))
        {
            HintText.Text = LocalizationService.Text("Settings.AlreadyAdded", Loc.Instance.Language);
            return;
        }

        _roots.Add(normalized);
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RootList.SelectedItem is not string selected)
        {
            HintText.Text = LocalizationService.Text("Settings.SelectToRemove", Loc.Instance.Language);
            return;
        }
        _roots.Remove(selected);
        Refresh();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage) return;
        var tag = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        Loc.Instance.SetLanguage(tag == "en-US" ? UiLanguage.EnUs : UiLanguage.ZhCn);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _protection.SetCustomRoots(_roots);
        var language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
        var updated = _settings with
        {
            ProtectedRoots = _roots.ToList(),
            MinimizeToTray = TrayCheck.IsChecked == true,
            FollowSystemTheme = ThemeCheck.IsChecked == true,
            Language = language,
            ShowSystemComponents = ShowSystemCheck.IsChecked == true
        };
        if (!SettingsStore.Save(updated))
        {
            MessageBox.Show(this,
                LocalizationService.Text("Settings.SaveFailed", Loc.Instance.Language),
                LocalizationService.Text("Settings.SaveFailedTitle", Loc.Instance.Language),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Loc.Instance.SetLanguage(_originalLanguage);
        DialogResult = false;
    }
}
