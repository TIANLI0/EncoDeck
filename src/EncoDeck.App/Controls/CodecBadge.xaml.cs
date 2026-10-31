// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Platform.A2dpCodec;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// The audio tag beside the connection state. A codec the PC streams with is filled; a link whose codec is not known
/// ("A2DP", "LE Audio") is outlined. Clicking it explains the tag; while Windows' codec log is off it offers to turn it
/// on (<see cref="WindowsA2dpCodecLog.AuthorizeAsync"/>, one administrator prompt).
/// </summary>
public sealed partial class CodecBadge : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(CodecBadge), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(CodecBadge), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty IsDetailedProperty =
        DependencyProperty.Register(nameof(IsDetailed), typeof(bool), typeof(CodecBadge), new PropertyMetadata(true, OnChanged));

    public static readonly DependencyProperty CanEnableProperty =
        DependencyProperty.Register(nameof(CanEnable), typeof(bool), typeof(CodecBadge), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty MaxLabelWidthProperty =
        DependencyProperty.Register(nameof(MaxLabelWidth), typeof(double), typeof(CodecBadge), new PropertyMetadata(220.0, OnChanged));

    private bool _busy;

    public CodecBadge()
    {
        InitializeComponent();
        Enable.Content = Loc.Get("CodecBadge_Enable");
        Update();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>The tag names a codec (filled) rather than only the link (outlined).</summary>
    public bool IsDetailed
    {
        get => (bool)GetValue(IsDetailedProperty);
        set => SetValue(IsDetailedProperty, value);
    }

    /// <summary>Windows' codec log is off and can be turned on from here.</summary>
    public bool CanEnable
    {
        get => (bool)GetValue(CanEnableProperty);
        set => SetValue(CanEnableProperty, value);
    }

    public double MaxLabelWidth
    {
        get => (double)GetValue(MaxLabelWidthProperty);
        set => SetValue(MaxLabelWidthProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CodecBadge)d).Update();

    private void Update()
    {
        LabelText.Text = Label;
        LabelText.MaxWidth = MaxLabelWidth;
        Frame.Style = (Style)Application.Current.Resources[IsDetailed ? "CodecBadgeStyle" : "CodecBadgeSubtleStyle"];
        LabelText.Style = (Style)Application.Current.Resources[IsDetailed ? "CodecBadgeTextStyle" : "CodecBadgeSubtleTextStyle"];
        DescriptionText.Text = Description;
        ToolTipService.SetToolTip(Badge, string.IsNullOrEmpty(Description) ? null : Description);
        AutomationProperties.SetName(Badge, string.IsNullOrEmpty(Description) ? Label : Description);
        Enable.Visibility = CanEnable ? Visibility.Visible : Visibility.Collapsed;
        Enable.IsEnabled = !_busy;
    }

    private void OnOpening(object? sender, object e)
    {
        if (_busy) return;
        ResultText.Visibility = Visibility.Collapsed;
        Update();
    }

    private async void OnEnable(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        Update();
        string? result = null;
        try
        {
            await WindowsA2dpCodecLog.AuthorizeAsync();
            await App.Instance.Host.WindowsCodec.RefreshAsync();
            result = WindowsA2dpCodecLog.IsAuthorized ? Loc.Get("CodecBadge_Enabled")
                : Loc.Format("WindowsCodec_Result_NotApplied", WindowsA2dpCodecLog.LogDirectory);
        }
        catch (OperationCanceledException)
        {
            result = Loc.Get("WindowsCodec_Result_Declined");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            result = Loc.Format("WindowsCodec_Result_Failed", ex.Message);
        }
        finally
        {
            _busy = false;
            ResultText.Text = result ?? "";
            ResultText.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
            Update();
        }
    }
}
