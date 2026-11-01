// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Device name, batteries, noise control and (for earbuds set up for LE Audio) LC3 / Classic: the content of the
/// tray panel and of the panel on top of Windows 11 Quick Settings.
/// </summary>
public sealed partial class DeviceQuickPanel : UserControl
{
    public DeviceQuickPanel()
    {
        InitializeComponent();
        // Flyout diagnostics measure before activation; resolve bindings before measuring.
        Bindings.Initialize();
        UpdateForm();
        Loaded += (_, _) => { ViewModel.PropertyChanged += OnDeviceChanged; UpdateForm(); };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnDeviceChanged;
    }

    public DeviceViewModel ViewModel => App.Instance.Device;
    public Visibility EarbudBatteryVisibility(bool hasDevice, bool headphones) => hasDevice && !headphones ? Visibility.Visible : Visibility.Collapsed;

    private void OnDeviceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.IsHeadphones)) UpdateForm();
    }
    private void UpdateForm()
    {
        ArtworkColumn.Width = new GridLength(ViewModel.IsHeadphones ? 80 : 56);
        HeaderArtwork.Height = ViewModel.IsHeadphones ? 80 : 48;
    }

    private void OnTransportClick(object sender, RoutedEventArgs e)
    {
        // Revert the local toggle at once; the checked segment follows what Windows actually plays.
        LeAudioButton.IsChecked = ViewModel.IsLeAudio;
        ClassicButton.IsChecked = ViewModel.IsClassicAudio;
        if (sender is FrameworkElement { Tag: string target }) ViewModel.SwitchAudioTransportCommand.Execute(target);
    }

    /// <summary>The "open EncoDeck" button was clicked.</summary>
    public event EventHandler? OpenAppRequested;

    private void OnOpenApp(object sender, RoutedEventArgs e) => OpenAppRequested?.Invoke(this, EventArgs.Empty);
}
