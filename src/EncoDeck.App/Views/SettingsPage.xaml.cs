// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        // Slider values are saved once the slider rests; leaving the page saves what is still waiting.
        Unloaded += (_, _) => ViewModel.FlushPendingSettings();
    }

    public SettingsViewModel ViewModel => App.Instance.Settings;

    private void OnPreviewPopup(object sender, RoutedEventArgs e)
    {
        // The popup reads its size and duration from the stored settings: save a slider value still waiting first.
        ViewModel.FlushPendingSettings();
        App.Instance.Windows.ShowConnectionPopup(TimeSpan.FromSeconds(App.Instance.Host.Settings.Current.PopupDurationSeconds));
    }
}
