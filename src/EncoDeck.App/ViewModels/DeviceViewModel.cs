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
    [ObservableProperty] public partial string? DeviceImagePath { get; set; }
    [ObservableProperty] public partial string? LeftArtworkPath { get; set; }
    [ObservableProperty] public partial string? RightArtworkPath { get; set; }
    [ObservableProperty] public partial string? CaseArtworkPath { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDeviceMotion))] public partial bool HasComponentArtwork { get; set; }
    [ObservableProperty] public partial bool IsRepresentativeArtwork { get; set; }
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
    [ObservableProperty] public partial string NoiseText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsEqualizerAvailable { get; set; }
    [ObservableProperty] public partial string EqualizerText { get; set; } = string.Empty;

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
        (CommandStatus.Rejected, "battery-low") => Loc.Get("Error_BatteryLow"),
        (CommandStatus.Rejected, "at-least-two") => Loc.Get("Error_CycleAtLeastTwo"),
        (CommandStatus.Rejected, "at-least-one") => Loc.Get("Error_CycleAtLeastOne"),
        (CommandStatus.Rejected, "remove-connected") => Loc.Get("Error_RemoveConnected"),
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
            IsRepresentativeArtwork = image?.RepresentativeArtwork ?? false;
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
        bool batteryReady = IsReady || (state.Connection == DeviceConnectionState.UsbReceiverConnected && state.LastResponseAt is not null);
        Left.Apply(state.Battery.Left, batteryReady, now);
        Right.Apply(state.Battery.Right, batteryReady, now);
        Case.Apply(state.Battery.Case, batteryReady, now);

        RebuildOptionsIfNeeded(profile);
        ApplyNoise(state, profile);
        ApplyEqualizer(state, profile);
        ApplyToggles(state);

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
        profile is IControllableProfile controllable ? string.Join(',', controllable.Toggles.Order()) : string.Empty);

    private void RebuildOptionsIfNeeded(IDeviceProfile? profile)
    {
        string key = ProfileKey;
        if (key == _profileKey) return;
        _profileKey = key;

        NoiseLevels.Clear();
        AdaptiveLevels.Clear();
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
        AddToggle(profile, DeviceToggle.SleepDetection);
        AddToggle(profile, DeviceToggle.SwiftPair);
        AddToggle(profile, DeviceToggle.VoiceCommands);
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
            : profile is IControllableProfile other && other.Toggles.Contains(toggle)
                && (other is not EncoDeck.Protocol.Brands.Moondrop.MoondropProfile moondrop
                    || moondrop.Capabilities.IsAvailable(EncoDeck.Protocol.Brands.Moondrop.MoondropProfile.ToggleCapability(toggle), _settings.Current.EnableExperimentalFeatures));
        if (!offered) return;
        Toggles.Add(new DeviceToggleViewModel(toggle, Loc.Get($"Toggle_{toggle}"), Loc.Get($"Toggle_{toggle}_Description")));
    }

    private void ApplyNoise(DeviceState state, IDeviceProfile? profile)
    {
        IsNoiseAvailable = IsReady && profile is not null && profile.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, allowExperimental: false);
        IsNoisePending = state.IsPending("noise-control");
        var noise = state.NoiseControl?.Value;
        IsOffSelected = noise?.Mode == NoiseControlMode.Off;
        IsAncSelected = noise?.Mode == NoiseControlMode.NoiseCancellation;
        IsAdaptiveSelected = noise?.Mode == NoiseControlMode.Adaptive;
        IsTransparencySelected = noise?.Mode == NoiseControlMode.Transparency;
        ShowNoiseLevels = IsAncSelected && NoiseLevels.Count > 0 && IsNoiseAvailable;
        ShowAdaptiveLevels = IsAdaptiveSelected && AdaptiveLevels.Count > 1 && IsNoiseAvailable;
        foreach (var level in NoiseLevels) level.IsSelected = IsAncSelected && level.Id == noise?.LevelId;
        foreach (var level in AdaptiveLevels) level.IsSelected = IsAdaptiveSelected && level.Id == noise?.LevelId;

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

    private void ApplyEqualizer(DeviceState state, IDeviceProfile? profile)
    {
        IsEqualizerAvailable = IsReady && EqualizerPresets.Count > 0;
        var eq = state.Equalizer?.Value;
        foreach (var preset in EqualizerPresets) preset.IsSelected = eq?.ActivePresetId == preset.Id;
        if (state.CustomEqualizers?.Value.FirstOrDefault(p => p.IsSelected) is { } custom)
        {
            EqualizerText = custom.Name;
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
    }

    private void ApplyToggles(DeviceState state)
    {
        foreach (var toggle in Toggles)
        {
            bool known = state.Toggles.TryGetValue(toggle.Toggle, out var value);
            toggle.IsKnown = known && IsReady;
            toggle.IsOn = known && value!.Value;
            toggle.IsPending = state.IsPending("toggle:" + toggle.Toggle);
        }
    }

    private string BuildToolTip()
    {
        if (!HasDevice) return "EncoDeck";
        var parts = new List<string> { $"{DeviceName} · {ConnectionText}" };
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

    private static bool IsAllowed(CapabilitySupport support) => support == CapabilitySupport.Supported;

    private static string Text(string key, string fallback)
    {
        string value = Loc.Get(key);
        return value == key ? fallback : value;
    }
}
