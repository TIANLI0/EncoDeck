// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EncoDeck.App.Services;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Platform.Settings;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Usb;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.ViewModels;

/// <summary>A selectable noise-control level (e.g. "Deep").</summary>
public sealed partial class NoiseLevelViewModel(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A device-side EQ preset.</summary>
public sealed partial class EqualizerPresetViewModel(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A boolean device setting.</summary>
public sealed partial class DeviceToggleViewModel(DeviceToggle toggle, string label, string description) : ObservableObject
{
    public DeviceToggle Toggle { get; } = toggle;
    public string Label { get; } = label;
    public string Description { get; } = description;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsKnown { get; set; }

    [ObservableProperty]
    public partial bool IsPending { get; set; }
}

/// <summary>
/// The single UI-facing projection of <see cref="IDeviceStateStore"/>. The main window, the tray
/// panel and the connection popup all bind to this one instance, so they can never disagree.
/// </summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    private readonly IDeviceStateStore _store;
    private readonly IDeviceCommandService _commands;
    private readonly ISettingsStore _settings;
    private readonly IDeviceResourceProvider _resources;
    private readonly DispatcherQueue _dispatcher;
    private int _applyQueued;
    private string? _profileKey;

    public DeviceViewModel(IDeviceStateStore store, IDeviceCommandService commands, ISettingsStore settings, IDeviceResourceProvider resources, DispatcherQueue dispatcher)
    {
        _store = store;
        _commands = commands;
        _settings = settings;
        _resources = resources;
        _dispatcher = dispatcher;
        _store.StateChanged += (_, _) => QueueApply();
        _resources.Changed += (_, _) => QueueApply();
        _settings.Changed += (_, _) => QueueApply(rebuildOptions: true);
        Apply(_store.Current);
    }

    public BatteryItemViewModel Left { get; } = new(BatteryComponent.Left);
    public BatteryItemViewModel Right { get; } = new(BatteryComponent.Right);
    public BatteryItemViewModel Case { get; } = new(BatteryComponent.Case);

    public ObservableCollection<NoiseLevelViewModel> NoiseLevels { get; } = [];
    public ObservableCollection<NoiseLevelViewModel> AdaptiveLevels { get; } = [];
    public ObservableCollection<NoiseLevelViewModel> TransparencyLevels { get; } = [];
    public ObservableCollection<EqualizerPresetViewModel> EqualizerPresets { get; } = [];
    public ObservableCollection<DeviceToggleViewModel> Toggles { get; } = [];

    [ObservableProperty] public partial bool HasDevice { get; set; }
    [ObservableProperty] public partial string DeviceName { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModelName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsGenericModel { get; set; }
    /// <summary>Changes when the offered settings change (model, or what these earbuds report).</summary>
    [ObservableProperty] public partial string ProfileKey { get; set; } = string.Empty;
    /// <summary>Banner text for earbuds the official client does not list either.</summary>
    [ObservableProperty] public partial string GenericModelMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial DeviceConnectionState Connection { get; set; }
    [ObservableProperty] public partial string ConnectionText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsReady { get; set; }
    [ObservableProperty] public partial bool IsBusyConnecting { get; set; }
    [ObservableProperty] public partial bool IsControlUnavailable { get; set; }
    [ObservableProperty] public partial bool IsLinkDown { get; set; }

    /// <summary>The audio runs over LE Audio (shown as an "LC3" tag next to the status).</summary>
    [ObservableProperty] public partial bool IsLeAudio { get; set; }
    /// <summary>
    /// A2DP Windows Bridge streams to these earbuds but neither it nor Windows carries their control channel. The
    /// "A2DP Bridge" tag already names the bridge in every other case, so only this one takes room above the page.
    /// </summary>
    [ObservableProperty] public partial bool HasBridgeNotice { get; set; }
    /// <summary>Connected only through the bridge: Windows does not see the earbuds, so their controls are unavailable.</summary>
    [ObservableProperty] public partial bool IsBridgeOnly { get; set; }
    [ObservableProperty] public partial string BridgeMessage { get; set; } = string.Empty;
    /// <summary>A2DP Windows Bridge streams to these earbuds: an "A2DP Bridge" tag next to the status.</summary>
    [ObservableProperty] public partial bool HasBridgeBadge { get; set; }
    [ObservableProperty] public partial string BridgeBadgeDescription { get; set; } = string.Empty;
    /// <summary>Audio codec or link evidence, with its source visible in the label.</summary>
    [ObservableProperty] public partial string CodecLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string CodecDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasCodec { get; set; }
    /// <summary>The tag names a codec rather than only the link (A2DP whose codec Windows has not logged).</summary>
    [ObservableProperty] public partial bool CodecIsDetailed { get; set; } = true;
    /// <summary>The tag is plain A2DP because Windows' codec log is off: clicking it offers to turn it on.</summary>
    [ObservableProperty] public partial bool CodecCanEnable { get; set; }
    [ObservableProperty] public partial string? DeviceImagePath { get; set; }
    [ObservableProperty] public partial string? LeftArtworkPath { get; set; }
    [ObservableProperty] public partial string? RightArtworkPath { get; set; }
    [ObservableProperty] public partial string? CaseArtworkPath { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDeviceMotion))] public partial bool HasComponentArtwork { get; set; }
    /// <summary>HeyMelody's 360° device model (turntable WebP) for the home page, when available.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDeviceMotion)), NotifyPropertyChangedFor(nameof(IsGenericMotion))] public partial string? DeviceModelPath { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDeviceMotion)), NotifyPropertyChangedFor(nameof(IsGenericMotion))] public partial string? DeviceMotionPath { get; set; }
    [ObservableProperty] public partial bool DeviceMotionIsVideo { get; set; }
    [ObservableProperty] public partial string? DeviceMotionDarkPath { get; set; }
    public bool HasDeviceMotion => DeviceModelPath is null && !HasComponentArtwork;
    public bool IsGenericMotion => DeviceModelPath is null && DeviceMotionPath is null;
    [ObservableProperty] public partial bool HasDeviceImage { get; set; }

    /// <summary>Selects the headphones illustration when no product image is available.</summary>
    [ObservableProperty] public partial bool IsHeadphones { get; set; }
    [ObservableProperty] public partial string? PopupMediaPath { get; set; }
    [ObservableProperty] public partial bool PopupMediaIsVideo { get; set; }
    /// <summary>Night variant of the pop-up media (HeyMelody ships one per theme); falls back to the day one.</summary>
    [ObservableProperty] public partial string? PopupMediaDarkPath { get; set; }
    [ObservableProperty] public partial bool PopupMediaDarkIsVideo { get; set; }

    /// <summary>Key used to look up this device's images and animations.</summary>
    public DeviceResourceKey? ResourceKey { get; private set; }

    [ObservableProperty] public partial bool IsNoiseAvailable { get; set; }
    /// <summary>
    /// Noise control is shown but cannot be changed: over LE Audio the X4 refuses every noise-mode
    /// change (status 0x01) once it settled in LE mode, so the controls stay greyed out.
    /// </summary>
    [ObservableProperty] public partial bool CanSetNoise { get; set; }
    [ObservableProperty] public partial bool IsNoisePending { get; set; }
    [ObservableProperty] public partial bool IsOffSelected { get; set; }
    [ObservableProperty] public partial bool IsAncSelected { get; set; }
    [ObservableProperty] public partial bool IsTransparencySelected { get; set; }
    [ObservableProperty] public partial bool IsAdaptiveSelected { get; set; }
    [ObservableProperty] public partial bool HasAdaptiveMode { get; set; }
    [ObservableProperty] public partial bool ShowAdaptiveLevels { get; set; }
    [ObservableProperty] public partial bool HasOffMode { get; set; }
    [ObservableProperty] public partial bool HasAncMode { get; set; }
    [ObservableProperty] public partial bool HasTransparencyMode { get; set; }
    [ObservableProperty] public partial bool ShowNoiseLevels { get; set; }
    [ObservableProperty] public partial bool ShowTransparencyLevels { get; set; }
    [ObservableProperty] public partial string NoiseText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsEqualizerAvailable { get; set; }
    [ObservableProperty] public partial string EqualizerText { get; set; } = string.Empty;

    /// <summary>Band values (dB offsets, -6..+6) the device reported for the active preset.</summary>
    [ObservableProperty] public partial IReadOnlyList<int> EqualizerBands { get; set; } = [];

    /// <summary>True when the active preset's band values can be overwritten (Sony manual / custom slots).</summary>
    [ObservableProperty] public partial bool CanEditEqualizerBands { get; set; }

    [ObservableProperty] public partial string FirmwareText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastUpdatedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TrayToolTip { get; set; } = "EncoDeck";

    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    public IDeviceProfile? Profile { get; private set; }

    public DeviceState Current => _store.Current;

    [RelayCommand]
    private async Task SetNoiseModeAsync(string mode)
    {
        if (!Enum.TryParse<NoiseControlMode>(mode, out var target)) return;
        await RunAsync(() => _commands.SetNoiseControlAsync(new NoiseControlState(target)));
    }

    [RelayCommand]
    private async Task SetNoiseLevelAsync(string levelId)
    {
        var option = Profile?.NoiseControlOptions.FirstOrDefault(o => o.LevelId == levelId);
        if (option is null) return;
        await RunAsync(() => _commands.SetNoiseControlAsync(option.ToState()));
    }

    [RelayCommand]
    private async Task SetEqualizerAsync(string presetId)
    {
        await RunAsync(() => _commands.SetEqualizerPresetAsync(presetId));
    }

    /// <summary>Pushes edited band values (dB offsets) for the active manual/custom preset.</summary>
    public async Task SetEqualizerBandsAsync(IReadOnlyList<int> bands)
    {
        await RunAsync(() => _commands.SetEqualizerBandsAsync(bands));
    }

    public async Task SetToggleAsync(DeviceToggle toggle, bool enabled)
    {
        await RunAsync(() => _commands.SetToggleAsync(toggle, enabled));
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(() => _commands.RefreshAsync());

    [RelayCommand]
    private Task ReconnectAsync() => RunAsync(() => _commands.ReconnectAsync());

    [RelayCommand]
    private void DismissError()
    {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>Runs a device command, shows a failure in the error banner and re-projects the state.</summary>
    public async Task<CommandResult> RunCommandAsync(Func<Task<CommandResult>> action)
    {
        CommandResult result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            result = new CommandResult(CommandStatus.TransportError, ex.Message);
        }
        if (!result.IsSuccess && result.Status != CommandStatus.Cancelled)
        {
            ErrorMessage = DescribeFailure(result);
            HasError = true;
        }
        // Re-project even on success: controls that were toggled locally must snap back to device truth.
        Apply(_store.Current, forceNotify: true);
        return result;
    }

    private Task RunAsync(Func<Task<CommandResult>> action) => RunCommandAsync(action);

    private static string DescribeFailure(CommandResult result) => (result.Status, result.Detail) switch
    {
        // Reasons the official client reports with its own message.
        (CommandStatus.Rejected, OppoDeviceController.WearRequiredDetail) => Loc.Get("Error_WearRequired"),
        (CommandStatus.Rejected, "busy-other-device") => Loc.Get("Error_BusyOtherDevice"),
        (CommandStatus.Rejected, EncoDeck.Protocol.Brands.Xiaomi.XiaomiProtocol.FindWornDetail) => Loc.Get("Error_FindWhileWorn"),
        (CommandStatus.Rejected, "battery-low") => Loc.Get("Error_BatteryLow"),
        (CommandStatus.Rejected, "at-least-two") => Loc.Get("Error_CycleAtLeastTwo"),
        (CommandStatus.Rejected, "at-least-one") => Loc.Get("Error_CycleAtLeastOne"),
        (CommandStatus.Rejected, "remove-connected") => Loc.Get("Error_RemoveConnected"),
        (CommandStatus.Rejected, "le-audio-multipoint") => Loc.Get("Error_LeAudioMultipoint"),
        (CommandStatus.Rejected, "max-presets") => Loc.Get("Error_MaxPresets"),
        (CommandStatus.Rejected, "invalid-name") => Loc.Get("Error_InvalidName"),
        (CommandStatus.Timeout, _) => Loc.Get("Error_Timeout"),
        (CommandStatus.TransportError, _) => Loc.Get("Error_Transport"),
        (CommandStatus.Rejected, _) => Loc.Get("Error_Rejected"),
        (CommandStatus.Unsupported, _) => Loc.Get("Error_Unsupported"),
        (CommandStatus.NotConnected, _) => Loc.Get("Error_NotConnected"),
        _ => result.ToString(),
    };

    private void QueueApply(bool rebuildOptions = false)
    {
        if (rebuildOptions) _profileKey = null;
        if (Interlocked.Exchange(ref _applyQueued, 1) == 1) return;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Volatile.Write(ref _applyQueued, 0);
            Apply(_store.Current);
        });
    }

    /// <summary>Manual demos (BrandDemo) set their own artwork; state changes then leave it alone, so the
    /// paths never briefly flip (which would restart the stage animation).</summary>
    internal bool ArtworkPinned { get; set; }

    private void Apply(DeviceState state, bool forceNotify = false)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = state.Profile;
        Profile = profile;
        HasDevice = state.Device is not null;
        if (!ArtworkPinned) DeviceName = state.Device?.Name ?? Loc.Get("Device_None");
        ModelName = profile?.DisplayName ?? string.Empty;
        IsGenericModel = profile?.ModelId == "oppo-generic" && state.ProductId is not null;
        GenericModelMessage = state.ProductId is { } unknownPid ? Loc.Format("Home_GenericModel_Message", unknownPid.ToString("X6")) : string.Empty;
        ProfileKey = profile switch
        {
            OppoDeviceProfile oppo => oppo.Definition.ProfileKey,
            EncoDeck.Protocol.Brands.Moondrop.MoondropProfile moondrop => moondrop.ProfileKey,
            // Other brands keep their model id while live answers add features (Xiaomi configs,
            // Sony EQ, HUAWEI spatial), so the key must change with what the profile offers.
            { } other => $"{other.ModelId}|{OfferSignature(other)}",
            null => string.Empty,
        };
        Connection = state.Connection;
        ConnectionText = DescribeConnection(state.Connection);
        IsReady = state.Connection == DeviceConnectionState.ControlReady;
        IsBusyConnecting = state.Connection is DeviceConnectionState.ControlConnecting or DeviceConnectionState.Reconnecting or DeviceConnectionState.AudioConnected;
        IsControlUnavailable = state.Connection == DeviceConnectionState.ControlUnavailable;
        IsLinkDown = state.Connection is DeviceConnectionState.PairedNotConnected or DeviceConnectionState.Disconnected;
        IsLeAudio = state.IsLeAudio && !IsLinkDown;
        // A2DP Windows Bridge streams to these earbuds over its own adapter: Windows (and so the control
        // channel) may not see them at all, but they are connected, and the codec is the bridge's.
        IsBridgeOnly = state.Bridge is not null && !state.Connection.IsLinkUp();
        HasBridgeNotice = IsBridgeOnly && state.Bridge is { IsStreaming: true };
        BridgeMessage = HasBridgeNotice ? Loc.Format("Home_Bridge_Only", DescribeBridge(state.Bridge!)) : string.Empty;
        HasBridgeBadge = state.Device?.Transport == DeviceTransport.Bluetooth && state.Bridge is { IsStreaming: true };
        // With A2DPWB 2.0.2 the control channel itself can run through the bridge.
        BridgeBadgeDescription = HasBridgeBadge
            ? Loc.Format(IsBridgeOnly ? "Badge_Bridge_AudioOnly" : state.LinkViaBridge ? "Badge_Bridge_Control" : "Badge_Bridge_Windows", DescribeBridge(state.Bridge!))
            : string.Empty;
        if (state.Bridge is { } bridge && IsBridgeOnly)
        {
            ConnectionText = Loc.Get(bridge.IsStreaming ? "Connection_Bridge" : "Connection_BridgeConnecting");
            IsLinkDown = false;
            IsBusyConnecting = !bridge.IsStreaming;
            IsLeAudio = false;
        }
        var codecDisplay = CodecDisplay.Resolve(state);
        CodecLabel = codecDisplay.Kind switch
        {
            // The "A2DP Bridge" tag beside a bridge stream already names its source.
            CodecDisplayKind.BridgeStream or CodecDisplayKind.WindowsStream => codecDisplay.Codec!,
            CodecDisplayKind.LeLink => "LE Audio",
            // Only the Classic link is known: the earbuds' own codec report is never shown.
            CodecDisplayKind.A2dpLogOff or CodecDisplayKind.A2dpNotLogged => "A2DP",
            // An unknown bridge codec shows no tag at all (the diagnostics still record why).
            _ => string.Empty,
        };
        CodecDescription = codecDisplay.Kind switch
        {
            CodecDisplayKind.BridgeStream => Loc.Format("Codec_BridgeDescription", codecDisplay.Codec),
            CodecDisplayKind.WindowsStream => Loc.Format("Codec_WindowsDescription", codecDisplay.Codec),
            CodecDisplayKind.LeLink => Loc.Get("Codec_LeAudioDescription"),
            CodecDisplayKind.A2dpLogOff => Loc.Get("Codec_A2dpDescription"),
            CodecDisplayKind.A2dpNotLogged => Loc.Get("Codec_A2dpNotLoggedDescription"),
            _ => string.Empty,
        };
        CodecIsDetailed = codecDisplay.Kind is not (CodecDisplayKind.A2dpLogOff or CodecDisplayKind.A2dpNotLogged);
        CodecCanEnable = codecDisplay.Kind == CodecDisplayKind.A2dpLogOff;
        HasCodec = CodecLabel.Length > 0;
        // One earbud worn while the other charges is ordinary use; warn only when the other should be up.
        if (LeAudioStatus.IsMissingEarbud(state, warnWithoutWear: profile is OppoDeviceProfile))
            ConnectionText = Loc.Get("Connection_LeAudioSingleEar");
        // Switched to LE Audio but LC3 is not up (e.g. after the case reopened): the earbuds only come
        // back over LE once they advertise; EncoDeck keeps the existing bond ready to reconnect. OPPO
        // control waits for that link; Classic-controlled earbuds show their own state meanwhile.
        if (LeAudioStatus.IsWaitingForLeAudio(state, controlNeedsLeAudio: profile is OppoDeviceProfile) && !IsBridgeOnly)
            ConnectionText = Loc.Get("Connection_WaitingForLeAudio");
        // Until the earbuds connect and report their ids, use the ones remembered from last time
        // (restored by the device cache) or the model's own product id, so the art shows at once.
        uint? productId = state.ProductId;
        if (productId is null && profile is OppoDeviceProfile { Definition.ProductIds: [var modelProductId, ..] }) productId = modelProductId;
        ResourceKey = DeviceResourceKey.FromState(state, productId);
        IsHeadphones = profile?.Form == DeviceForm.Headphones;
        if (!ArtworkPinned)
        {
            var image = ResourceKey is { } key ? _resources.Resolve(key, DeviceResourceRole.Device) : null;
            DeviceImagePath = image?.Path ?? DeviceImages.Resolve(profile?.ImageKey);
            LeftArtworkPath = ResourceKey is { } kl ? _resources.Resolve(kl, DeviceResourceRole.LeftBud)?.Path : null;
            RightArtworkPath = ResourceKey is { } kr ? _resources.Resolve(kr, DeviceResourceRole.RightBud)?.Path : null;
            CaseArtworkPath = ResourceKey is { } kc ? _resources.Resolve(kc, DeviceResourceRole.Case)?.Path : null;
            DeviceModelPath = ResourceKey is { } km ? _resources.Resolve(km, DeviceResourceRole.DeviceModel)?.Path : null;
            var motion = ResourceKey is { } ka ? _resources.Resolve(ka, DeviceResourceRole.DeviceAnimation) : null;
            if (ResourceKey?.Brand == "huawei") motion = HuaweiArtworkPolicy.ForRole(motion, DeviceResourceRole.DeviceAnimation);
            DeviceMotionIsVideo = motion?.Kind == DeviceResourceKind.Video;
            DeviceMotionPath = motion?.Path;
            var motionDark = ResourceKey is { } kmd ? _resources.Resolve(kmd, DeviceResourceRole.DeviceAnimationDark) : null;
            if (ResourceKey?.Brand == "huawei") motionDark = HuaweiArtworkPolicy.ForRole(motionDark, DeviceResourceRole.DeviceAnimationDark);
            DeviceMotionDarkPath = motionDark?.Path;
            HasComponentArtwork = DeviceImagePath is null && DeviceModelPath is null && DeviceMotionPath is null &&
                (LeftArtworkPath is not null || RightArtworkPath is not null || CaseArtworkPath is not null);
            HasDeviceImage = DeviceImagePath is not null;
            var popup = ResourceKey is { } k ? _resources.Resolve(k, DeviceResourceRole.PopupAnimation) : null;
            if (ResourceKey?.Brand == "huawei") popup = HuaweiArtworkPolicy.ForRole(popup, DeviceResourceRole.PopupAnimation);
            PopupMediaPath = popup?.Path ?? DeviceImagePath;
            PopupMediaIsVideo = popup?.Kind == DeviceResourceKind.Video;
            var popupDark = ResourceKey is { } kd ? _resources.Resolve(kd, DeviceResourceRole.PopupAnimationDark) : null;
            if (ResourceKey?.Brand == "huawei") popupDark = HuaweiArtworkPolicy.ForRole(popupDark, DeviceResourceRole.PopupAnimationDark);
            PopupMediaDarkPath = popupDark?.Path ?? PopupMediaPath;
            PopupMediaDarkIsVideo = popupDark is not null ? popupDark.Kind == DeviceResourceKind.Video : PopupMediaIsVideo;
        }

        foreach (var item in new[] { Left, Right, Case }) item.SetSingleBattery(IsHeadphones);
        bool batteryReady = IsReady || (state.Connection is DeviceConnectionState.UsbReceiverConnected or DeviceConnectionState.UsbHeadsetConnected && state.LastResponseAt is not null);
        Left.Apply(state.Battery.Left, batteryReady, now);
        Right.Apply(state.Battery.Right, batteryReady, now);
        Case.Apply(state.Battery.Case, batteryReady, now);

        RebuildOptionsIfNeeded(profile);
        ApplyNoise(state, profile);
        // A Barracuda X on its USB receiver has no control channel; its read-back settings make it controllable.
        bool controlReady = IsReady || FeaturesViewModel.IsUsbSettingsReady(state);
        ApplyEqualizer(state, profile, controlReady);
        ApplyToggles(state, controlReady);

        FirmwareText = state.Firmware?.Value is { } fw ? DescribeFirmware(fw, profile) : string.Empty;
        var newest = new[] { state.Battery.Left?.Timestamp, state.Battery.Right?.Timestamp, state.NoiseControl?.Timestamp, state.LastResponseAt }.Max();
        LastUpdatedText = newest is { } t && IsReady ? Loc.Format("Status_UpdatedAt", t.ToLocalTime().ToString("T")) : string.Empty;

        if (state.LastError is { } error && state.Connection is DeviceConnectionState.ControlUnavailable && !HasError)
        {
            ErrorMessage = error.ResourceKey is { } key ? Loc.Get(key) : error.Message;
        }

        TrayToolTip = BuildToolTip();
        if (forceNotify)
        {
            OnPropertyChanged(nameof(IsOffSelected));
            OnPropertyChanged(nameof(IsAncSelected));
            OnPropertyChanged(nameof(IsTransparencySelected));
            OnPropertyChanged(nameof(IsAdaptiveSelected));
        }
    }

    private static string OfferSignature(IDeviceProfile profile) => string.Join(';',
        string.Concat(Enum.GetValues<DeviceCapability>().Select(c => (char)('0' + (int)profile.Capabilities.Get(c).Support))),
        string.Join(',', profile.NoiseControlOptions.Select(o => $"{o.Mode}/{o.LevelId}/{o.Support}")),
        string.Join(',', profile.EqualizerPresets.Select(p => $"{p.Id}/{p.Support}")),
        profile switch
        {
            IControllableProfile controllable => string.Join(',', controllable.Toggles.Order()),
            UsbHeadsetProfile usb => $"{string.Join(',', usb.Toggles)}/{string.Join(',', usb.AutoPowerOffMinutes)}",
            _ => string.Empty,
        });

    private void RebuildOptionsIfNeeded(IDeviceProfile? profile)
    {
        string key = ProfileKey;
        if (key == _profileKey) return;
        _profileKey = key;

        NoiseLevels.Clear();
        AdaptiveLevels.Clear();
        TransparencyLevels.Clear();
        EqualizerPresets.Clear();
        Toggles.Clear();
        if (profile is null) return;

        foreach (var option in profile.NoiseControlOptions.Where(o => o.Mode == NoiseControlMode.NoiseCancellation && o.LevelId is not null && IsAllowed(o.Support)))
        {
            NoiseLevels.Add(new NoiseLevelViewModel(option.LevelId!, Text(option.ResourceKey, option.DisplayName)));
        }
        foreach (var option in profile.NoiseControlOptions.Where(o => o.Mode == NoiseControlMode.Adaptive && o.LevelId is not null && IsAllowed(o.Support)))
        {
            AdaptiveLevels.Add(new NoiseLevelViewModel(option.LevelId!, Text(option.ResourceKey, option.DisplayName)));
        }
        foreach (var option in profile.NoiseControlOptions.Where(o => o.Mode == NoiseControlMode.Transparency && o.LevelId is not null && IsAllowed(o.Support)))
            TransparencyLevels.Add(new NoiseLevelViewModel(option.LevelId!, Text(option.ResourceKey, option.DisplayName)));
        if (profile.Capabilities.IsAvailable(DeviceCapability.Equalizer, allowExperimental: false))
        {
            foreach (var preset in profile.EqualizerPresets.Where(p => IsAllowed(p.Support)))
            {
                EqualizerPresets.Add(new EqualizerPresetViewModel(preset.Id, Text(preset.ResourceKey, preset.DisplayName)));
            }
        }
        // "More settings" switches; game mode, dual connection, head gestures and the bass engine
        // have their own sections, as in the official client.
        AddToggle(profile, DeviceToggle.InEarDetection);
        if (profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile) AddToggle(profile, DeviceToggle.ChannelSwap);
        AddToggle(profile, DeviceToggle.AutoVolume);
        AddToggle(profile, DeviceToggle.AutoAnswerCalls);
        AddToggle(profile, DeviceToggle.SleepDetection);
        AddToggle(profile, DeviceToggle.SwiftPair);
        AddToggle(profile, DeviceToggle.VoiceCommands);
        AddToggle(profile, DeviceToggle.SpeakToChat);
        AddToggle(profile, DeviceToggle.Upscaling);
        AddToggle(profile, DeviceToggle.TouchControls);
        AddToggle(profile, DeviceToggle.DoNotDisturb);
        if (profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile)
        {
            AddToggle(profile, DeviceToggle.HiRes);
            AddToggle(profile, DeviceToggle.Ldac);
            AddToggle(profile, DeviceToggle.Lc3);
            AddToggle(profile, DeviceToggle.IndicatorLight);
            AddToggle(profile, DeviceToggle.PromptTone);
        }

        bool Has(NoiseControlMode mode) => profile.NoiseControlOptions.Any(o => o.Mode == mode && IsAllowed(o.Support));
        HasOffMode = Has(NoiseControlMode.Off);
        HasAncMode = Has(NoiseControlMode.NoiseCancellation);
        HasTransparencyMode = Has(NoiseControlMode.Transparency);
        HasAdaptiveMode = Has(NoiseControlMode.Adaptive);
    }

    private void AddToggle(IDeviceProfile profile, DeviceToggle toggle)
    {
        bool offered = profile is OppoDeviceProfile { Definition: var model }
            ? model.FeatureSwitches.Any(f => f.Toggle == toggle && f.Support == CapabilitySupport.Supported)
            : profile is IControllableProfile other ? other.Toggles.Contains(toggle)
                && (other is not EncoDeck.Protocol.Brands.Moondrop.MoondropProfile moondrop
                    || moondrop.Capabilities.IsAvailable(EncoDeck.Protocol.Brands.Moondrop.MoondropProfile.ToggleCapability(toggle), _settings.Current.EnableExperimentalFeatures))
            : profile is UsbHeadsetProfile usb && usb.Toggles.Contains(toggle);
        if (!offered) return;
        Toggles.Add(new DeviceToggleViewModel(toggle, Loc.Get($"Toggle_{toggle}"), Loc.Get($"Toggle_{toggle}_Description")));
    }

    private void ApplyNoise(DeviceState state, IDeviceProfile? profile)
    {
        IsNoiseAvailable = IsReady && profile is not null && profile.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, allowExperimental: false);
        // Only models verified to refuse noise changes over LE Audio (the X4) are greyed out there.
        CanSetNoise = IsNoiseAvailable && !(state.IsLeAudio && profile is OppoDeviceProfile { Definition.RejectsNoiseChangeOnLeAudio: true });
        IsNoisePending = state.IsPending("noise-control");
        var noise = state.NoiseControl?.Value;
        IsOffSelected = noise?.Mode == NoiseControlMode.Off;
        IsAncSelected = noise?.Mode == NoiseControlMode.NoiseCancellation;
        IsAdaptiveSelected = noise?.Mode == NoiseControlMode.Adaptive;
        IsTransparencySelected = noise?.Mode == NoiseControlMode.Transparency;
        ShowNoiseLevels = IsAncSelected && NoiseLevels.Count > 0 && IsNoiseAvailable;
        ShowAdaptiveLevels = IsAdaptiveSelected && AdaptiveLevels.Count > 1 && IsNoiseAvailable;
        ShowTransparencyLevels = IsTransparencySelected && TransparencyLevels.Count > 1 && IsNoiseAvailable;
        foreach (var level in NoiseLevels) level.IsSelected = IsAncSelected && level.Id == noise?.LevelId;
        foreach (var level in AdaptiveLevels) level.IsSelected = IsAdaptiveSelected && level.Id == noise?.LevelId;
        foreach (var level in TransparencyLevels) level.IsSelected = IsTransparencySelected && level.Id == noise?.LevelId;

        if (noise is null)
        {
            NoiseText = IsReady ? Loc.Get("Noise_Unknown") : string.Empty;
        }
        else
        {
            var option = profile?.NoiseControlOptions.FirstOrDefault(o => o.Matches(noise));
            NoiseText = option is not null ? DescribeNoise(option) : Loc.Get($"NoiseMode_{noise.Mode}");
        }
    }

    private string DescribeNoise(NoiseControlOption option)
    {
        string mode = Loc.Get($"NoiseMode_{option.Mode}");
        return option.LevelId is null ? mode : $"{mode} · {Text(option.ResourceKey, option.DisplayName)}";
    }

    private void ApplyEqualizer(DeviceState state, IDeviceProfile? profile, bool controlReady)
    {
        IsEqualizerAvailable = controlReady && EqualizerPresets.Count > 0;
        var eq = state.Equalizer?.Value;
        foreach (var preset in EqualizerPresets) preset.IsSelected = eq?.ActivePresetId == preset.Id;
        if (state.CustomEqualizers?.Value.FirstOrDefault(p => p.IsSelected) is { } custom)
        {
            // A fixed device curve (non-OPPO brands) carries no name of its own.
            EqualizerText = custom.Name.Length == 0 ? Loc.Get("Eq_DeviceCustom") : custom.Name;
        }
        else if (eq is null)
        {
            EqualizerText = string.Empty;
        }
        else
        {
            var preset = profile?.EqualizerPresets.FirstOrDefault(p => p.Id == eq.ActivePresetId);
            EqualizerText = preset is not null ? Text(preset.ResourceKey, preset.DisplayName) : Loc.Format("Eq_UnknownPreset", eq.RawPresetValue);
        }

        // The band values the device reported for the active preset (none: no curve is drawn). The
        // faders are the ten 31 Hz-16 kHz bands, so other layouts are shown but never written.
        EqualizerBands = eq?.Bands ?? [];
        CanEditEqualizerBands = IsReady && IsEqualizerAvailable && EqualizerBands.Count == 10
            && profile?.EqualizerPresets.FirstOrDefault(p => p.Id == eq?.ActivePresetId)?.EditableBands == true;
    }

    private void ApplyToggles(DeviceState state, bool controlReady)
    {
        foreach (var toggle in Toggles)
        {
            bool known = state.Toggles.TryGetValue(toggle.Toggle, out var value);
            toggle.IsKnown = known && controlReady;
            toggle.IsOn = known && value!.Value;
            toggle.IsPending = state.IsPending("toggle:" + toggle.Toggle);
        }
    }

    private string BuildToolTip()
    {
        if (!HasDevice) return "EncoDeck";
        var parts = new List<string> { $"{DeviceName} · {ConnectionText}" + (HasBridgeBadge ? " · A2DP Bridge" : string.Empty) };
        if (IsHeadphones)
        {
            if (Left.HasValue) parts.Add($"{Left.Label} {Left.LevelText}");
        }
        else if (Left.HasValue || Right.HasValue)
        {
            parts.Add($"{Left.Label} {Left.LevelText} · {Right.Label} {Right.LevelText}" + (Case.HasValue ? $" · {Case.Label} {Case.LevelText}" : string.Empty));
        }
        string text = string.Join("\n", parts);
        // Shell tooltips are limited to 127 characters.
        return text.Length > 120 ? text[..120] : text;
    }

    private static string DescribeFirmware(FirmwareInfo fw, IDeviceProfile? profile)
    {
        var parts = new List<string>();
        if (fw.Extra.TryGetValue("left", out var l)) parts.Add($"{Loc.Get("Battery_Left")} {l}");
        if (fw.Extra.TryGetValue("right", out var r)) parts.Add($"{Loc.Get("Battery_Right")} {r}");
        if (fw.Extra.TryGetValue("case", out var c)) parts.Add($"{Loc.Get("Battery_Case")} {c}");
        // Labelled, or the bare version numbers read like battery levels.
        // Sennheiser reports one device-wide version; other brands keep their existing Home layout.
        if (parts.Count == 0 && profile is EncoDeck.Protocol.Brands.Sennheiser.SennheiserProfile && !string.IsNullOrWhiteSpace(fw.Version)) parts.Add(fw.Version);
        return parts.Count == 0 ? string.Empty : $"{Loc.Get("Home_FirmwareLabel")}  {string.Join(" · ", parts)}";
    }

    public static string DescribeConnection(DeviceConnectionState state) => Loc.Get($"Connection_{state}");

    /// <summary>"LDAC · 990 kbps · 96 kHz" for the bridge's stream (what is known of it).</summary>
    internal static string DescribeBridge(BridgeAudioLink link)
    {
        var parts = new List<string>();
        if (link.Codec is { } codec) parts.Add(codec);
        if (link.BitrateKbps > 0) parts.Add($"{link.BitrateKbps} kbps");
        if (link.SampleRate > 0) parts.Add((link.SampleRate / 1000.0).ToString(link.SampleRate % 1000 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture) + " kHz");
        return parts.Count > 0 ? string.Join(" · ", parts) : "A2DP";
    }

    private static bool IsAllowed(CapabilitySupport support) => support == CapabilitySupport.Supported;

    /// <summary>
    /// Localized text for a profile resource key, or <paramref name="fallback"/> when the key is unknown.
    /// A key of the form <c>Name:argument</c> names a format string with one argument (e.g. <c>Eq_DevicePreset:6</c>).
    /// </summary>
    private static string Text(string key, string fallback)
    {
        int colon = key.IndexOf(':');
        string name = colon < 0 ? key : key[..colon];
        string value = Loc.Get(name);
        if (value == name) return fallback;
        return colon < 0 ? value : string.Format(System.Globalization.CultureInfo.CurrentCulture, value, key[(colon + 1)..]);
    }
}
