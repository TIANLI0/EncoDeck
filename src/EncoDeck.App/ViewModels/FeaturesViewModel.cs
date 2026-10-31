// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Usb;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Platform.Settings;
using Microsoft.UI.Dispatching;

namespace EncoDeck.App.ViewModels;

/// <summary>
/// UI projection of the earbud features beyond noise control (game mode, gestures, dual connection,
/// spatial audio, custom EQ, …) together with the official client's coupling rules between them.
/// Pages render from <see cref="State"/> whenever <see cref="Changed"/> fires; every write goes to
/// the earbuds and is shown only after the earbuds confirm it.
/// </summary>
public sealed class FeaturesViewModel
{
    /// <summary>Long-press cycle bits (protocol index of each top-level mode).</summary>
    public const uint OffBit = 1u << 0, NoiseCancellingBit = 1u << 1, TransparencyBit = 1u << 2, AdaptiveBit = 1u << 11;

    /// <summary>Gesture function code for "noise control" on squeeze-and-hold.</summary>
    public const int NoiseControlFunction = 8;

    private readonly IDeviceStateStore _store;
    private readonly IDeviceCommandService _basic;
    private readonly IAdvancedDeviceCommands _commands;
    private readonly DeviceViewModel _device;
    private readonly ISettingsStore _settings;
    private readonly DispatcherQueue _dispatcher;
    private int _queued;

    public FeaturesViewModel(IDeviceStateStore store, IDeviceCommandService basic, IAdvancedDeviceCommands commands, ISettingsStore settings, DeviceViewModel device, DispatcherQueue dispatcher)
    {
        _store = store;
        _basic = basic;
        _commands = commands;
        _device = device;
        _settings = settings;
        _dispatcher = dispatcher;
        _settings.Changed += (_, _) => Queue();
        _store.StateChanged += (_, _) => Queue();
    }

    /// <summary>Raised on the UI thread after the device state changed (coalesced).</summary>
    public event EventHandler? Changed;

    public DeviceState State => _store.Current;

    public OppoModelDefinition? Model => (State.Profile as OppoDeviceProfile)?.Definition;

    public bool IsMoondrop => State.Profile is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile;

    public bool IsSennheiserTw4 => State.Profile is EncoDeck.Protocol.Brands.Sennheiser.SennheiserProfile { IsTw4: true };

    public bool IsXiaomi => State.Profile is EncoDeck.Protocol.Brands.Xiaomi.XiaomiDeviceProfile;

    public bool IsVivo => State.Profile is EncoDeck.Protocol.Brands.Vivo.VivoDeviceProfile;

    public bool IsEdifier => State.Profile is EncoDeck.Protocol.Brands.Edifier.EdifierProfile;

    /// <summary>Brands whose controllers gate read-back-only settings behind the experimental switch.</summary>
    public static bool OffersExperimental(EncoDeck.Core.Profiles.IDeviceProfile? profile) => profile
        is EncoDeck.Protocol.Brands.Moondrop.MoondropProfile
        or EncoDeck.Protocol.Brands.Akg.AkgHybridProfile
        or EncoDeck.Protocol.Brands.Vivo.VivoDeviceProfile
        or EncoDeck.Protocol.Brands.Edifier.EdifierProfile;

    public bool AllowExperimental => OffersExperimental(State.Profile) && _settings.Current.EnableExperimentalFeatures;

    public bool IsReady => State.Connection == DeviceConnectionState.ControlReady || IsUsbSettingsReady(State);

    /// <summary>
    /// A USB headset (Barracuda X 1532:0550) is linked and at least one of its settings was read back. Other USB
    /// receivers never get settings, so they stay read-only.
    /// </summary>
    public static bool IsUsbSettingsReady(DeviceState state) =>
        state.Connection == DeviceConnectionState.UsbHeadsetConnected && state.Profile is UsbHeadsetProfile { Settings.Count: > 0 };

    public bool IsUsbHeadset => State.Profile is UsbHeadsetProfile;

    /// <summary>Sets the earbuds' LE/BR mode switch (0x0433) through the active controller.</summary>
    public Task<CommandResult> SetLeBrSwitchAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _commands.SetLeBrSwitchAsync(enabled, cancellationToken);

    /// <summary>Reports the LE Audio transport (feature 0x1F) once control runs over LE.</summary>
    public Task<CommandResult> ReportLeAudioTransportAsync(CancellationToken cancellationToken) =>
        _commands.ReportLeAudioTransportAsync(cancellationToken);

    /// <summary>Always writes and reads back multipoint off before attempting LE Audio.</summary>
    public Task<CommandResult> DisableMultipointForLeAudioAsync(CancellationToken cancellationToken) =>
        _basic.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, cancellationToken);

    public bool Has(DeviceCapability capability) => State.Profile switch
    {
        OppoDeviceProfile { Definition: { IsGeneric: false } model } => model.Capabilities.Get(capability).Support == CapabilitySupport.Supported,
        OppoDeviceProfile => false,
        // Other brands: their profile's capabilities decide directly.
        { } profile => profile.Capabilities.IsAvailable(capability, AllowExperimental),
        _ => false,
    };

    public bool HasSwitch(DeviceToggle toggle) => State.Profile switch
    {
        OppoDeviceProfile => Model?.FeatureSwitches.Any(f => f.Toggle == toggle && f.Support == CapabilitySupport.Supported) == true,
        IControllableProfile profile => profile.Toggles.Contains(toggle),
        UsbHeadsetProfile usb => usb.Toggles.Contains(toggle),
        _ => false,
    };

    public bool? Toggle(DeviceToggle toggle) => State.Toggles.TryGetValue(toggle, out var value) ? value.Value : null;

    public bool IsPending(string name) => State.IsPending(name);

    public Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled) =>
        Run(() => _basic.SetToggleAsync(toggle, enabled));

    // ---- game mode (official GameSetFragment) ----------------------------------------------------

    public int GameSoundType => State.GameSound?.Value.SelectedType ?? 0;

    /// <summary>Configured game-sound types the earbuds report as supported; 0 (off) is always offered.</summary>
    public IReadOnlyList<int> GameSoundOptions =>
        Model?.GameSoundTypes.Where(t => t == 0 || State.GameSound?.Value.SupportedTypes.Contains(t) != false).ToArray() ?? [];

    public Task<CommandResult> SetGameModeAsync(bool enabled) => SetToggleAsync(DeviceToggle.GamingMode, enabled);

    /// <summary>
    /// Low latency off while game sound is off would leave game mode with nothing to do; the official
    /// client then turns game mode off and game sound back on instead.
    /// </summary>
    public Task<CommandResult> SetLowLatencyAsync(bool enabled) => Run(async () =>
    {
        int firstType = GameSoundOptions.FirstOrDefault(t => t != 0);
        if (!enabled && GameSoundType == 0 && firstType != 0)
        {
            var off = await _basic.SetToggleAsync(DeviceToggle.GamingMode, false);
            return off.IsSuccess ? await _commands.SetGameSoundAsync(firstType) : off;
        }
        return await _basic.SetToggleAsync(DeviceToggle.LowLatency, enabled);
    });

    /// <summary>Same rule in the other direction: game sound off while low latency is off.</summary>
    public Task<CommandResult> SetGameSoundAsync(int type) => Run(async () =>
    {
        if (type == 0 && Toggle(DeviceToggle.LowLatency) == false)
        {
            var off = await _basic.SetToggleAsync(DeviceToggle.GamingMode, false);
            return off.IsSuccess ? await _basic.SetToggleAsync(DeviceToggle.LowLatency, true) : off;
        }
        return await _commands.SetGameSoundAsync(type);
    });

    /// <summary>Official mutex: EQ changes and the bass engine switch need enhanced game sound off.</summary>
    public bool GameSoundBlocksSoundChanges => Toggle(DeviceToggle.GamingMode) == true && GameSoundType != 0;

    /// <summary>
    /// <see cref="GameSoundBlocksSoundChanges"/> after reading game mode and game sound from the
    /// earbuds again; the stored values can be out of date. A failed read keeps the stored values.
    /// </summary>
    public async Task<bool> CheckGameSoundBlocksAsync()
    {
        if (!GameSoundBlocksSoundChanges) return false;
        try
        {
            await _commands.RefreshGameModeAsync();
        }
        catch (Exception)
        {
            // Best effort: decide with what is known.
        }
        return GameSoundBlocksSoundChanges;
    }

    /// <summary>Turns enhanced game sound off the way the official mutex dialog does.</summary>
    public async Task<bool> ReleaseGameSoundAsync()
    {
        var result = await Run(async () =>
        {
            if (Toggle(DeviceToggle.LowLatency) == false)
            {
                var off = await _basic.SetToggleAsync(DeviceToggle.GamingMode, false);
                return off.IsSuccess ? await _basic.SetToggleAsync(DeviceToggle.LowLatency, true) : off;
            }
            return await _commands.SetGameSoundAsync(0);
        });
        return result.IsSuccess;
    }

    // ---- spatial audio ---------------------------------------------------------------------------

    public bool IsHiResOn => Toggle(DeviceToggle.HiRes) == true;

    /// <summary>While Hi-Res is on the official page shows spatial audio as off.</summary>
    public SpatialAudioMode? SpatialMode => State.Spatial is null ? null : IsHiResOn && !IsMoondrop ? SpatialAudioMode.Off : State.Spatial.Value;

    public Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode) => Run(() => _commands.SetSpatialAudioAsync(mode));

    // ---- prompt volume and bass engine -----------------------------------------------------------

    public Task<CommandResult> SetPromptVolumeAsync(int level) => Run(() => _commands.SetPromptVolumeAsync(level));

    public Task<CommandResult> SetGainAsync(int level) => Run(() => _commands.SetGainAsync(level));

    public Task<CommandResult> SetBassEngineLevelAsync(int level) => Run(() => _commands.SetBassEngineLevelAsync(level));

    // ---- device-reported options (auto power off, button assignment, levels) -----------------------

    /// <summary>Options the earbuds reported, in display order. Only present after a device readback.</summary>
    public IReadOnlyList<DeviceOption> Options => State.Options.Values.Select(o => o.Value).OrderBy(o => o.Order).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();

    public Task<CommandResult> SetOptionAsync(string id, int value) => Run(() => _commands.SetOptionAsync(id, value));

    // ---- gestures --------------------------------------------------------------------------------

    /// <summary>Gesture rows with a function choice (rows that only open the noise cycle are left out).</summary>
    public IReadOnlyList<OppoGestureRow> GestureRows(bool callControls) =>
        Model?.GestureRows.Where(r => r.IsCallControl == callControls && r.IsWritten).ToArray() ?? [];

    /// <summary>Label of a gesture function on this model.</summary>
    public string FunctionLabelKey(int function) =>
        Model?.FunctionLabels.TryGetValue(function, out var key) == true ? key! : $"Function_{function}";

    /// <summary>
    /// The function the earbuds report for a gesture on the selected earbud. Official matching:
    /// the earbud's own record or a both-earbuds record (the X4 reports call rows per earbud).
    /// Call rows fall back to their default.
    /// </summary>
    public int? CurrentFunction(OppoGestureRow row, EarSide side)
    {
        var match = State.KeyFunctions?.Value.FirstOrDefault(k =>
            (k.Side == side || k.Side == EarSide.Both) && k.Button == row.Button && k.Action == row.Action);
        if (match is not null) return match.Function;
        // Official: a row the earbuds did not report shows its configured default.
        if (State.KeyFunctions is null) return null;
        return row.DefaultFunction ?? (row.IsCallControl ? row.AllowedFunctions.LastOrDefault(f => f != 0) : null);
    }

    public Task<CommandResult> SetGestureAsync(OppoGestureRow row, EarSide side, int function) =>
        Run(() => _commands.SetKeyFunctionAsync(new KeyFunction(side, row.Button, row.Action, function)));

    public int? MoondropFunction(EarSide side, GestureAction action) => State.KeyFunctions?.Value
        .FirstOrDefault(k => k.Side == side && k.Button == 0 && k.Action == action)?.Function;

    public Task<CommandResult> SetMoondropGestureAsync(EarSide side, GestureAction action, int function) =>
        Run(() => _commands.SetKeyFunctionAsync(new KeyFunction(side, 0, action, function)));

    public int? SennheiserFunction(EarSide side, GestureAction action) => State.KeyFunctions?.Value
        .FirstOrDefault(k => k.Side == side && k.Button == 0 && k.Action == action)?.Function;

    /// <summary>Gesture-like rows a brand profile describes itself (vivo gestures, EDIFIER button control).</summary>
    public IReadOnlyList<BrandGestureSlot> BrandGestureSlots => (State.Profile as IBrandGestureProfile)?.GestureSlots ?? [];

    /// <summary>The value the device reported for a slot, or null before it reported one.</summary>
    public int? BrandFunction(BrandGestureSlot slot) => State.KeyFunctions?.Value.FirstOrDefault(slot.Matches)?.Function;

    /// <summary>A slot applies when it has a reported value and, if it depends on another slot, that one has the required value.</summary>
    public bool BrandSlotApplies(BrandGestureSlot slot) =>
        BrandFunction(slot) is not null
        && (slot.ShownWhen is not { } when || State.KeyFunctions?.Value.Any(k => k == when) == true);

    public Task<CommandResult> SetBrandGestureAsync(BrandGestureSlot slot, int function) =>
        Run(() => _commands.SetKeyFunctionAsync(new KeyFunction(slot.Side, slot.Button, slot.Action, function)));

    public Task<CommandResult> SetSennheiserGestureAsync(EarSide side, GestureAction action, int function) =>
        Run(() => _commands.SetKeyFunctionAsync(new KeyFunction(side, 0, action, function)));

    /// <summary>
    /// Whether the long-press noise cycle applies: always for models whose hold gesture only cycles
    /// noise modes, otherwise while the hold gesture is set to noise control.
    /// </summary>
    public bool HoldControlsNoise(EarSide side)
    {
        if (Model is not { NoiseCycleIndices.Count: > 0 } model) return false;
        var hold = model.GestureRows.FirstOrDefault(r => r.EditsNoiseCycle);
        if (hold is null) return false;
        return !hold.IsWritten || CurrentFunction(hold, side) == NoiseControlFunction;
    }

    public uint? CycleMask(EarSide side) =>
        side == EarSide.Right && Model?.NoiseCycleShared != true ? State.NoiseCycle?.Value.RightMask : State.NoiseCycle?.Value.LeftMask;

    public Task<CommandResult> SetNoiseCycleAsync(EarSide side, uint mask) => Run(() => _commands.SetNoiseCycleAsync(side, mask));

    public Task<CommandResult> SetHeadMotionTypeAsync(int type) => Run(() => _commands.SetHeadMotionTypeAsync(type));

    // ---- find earbuds and fit test ---------------------------------------------------------------

    public bool IsAnyEarbudInEar => State.Wear?.Value is { } wear && (wear.LeftInEar == true || wear.RightInEar == true);

    public bool AreBothEarbudsInEar => State.Wear?.Value is { LeftInEar: true, RightInEar: true };

    public Task<CommandResult> FindEarbudsAsync(bool ring) => Run(() => _commands.FindEarbudsAsync(ring));

    public Task<CommandResult> RunFitTestAsync() => Run(() => _commands.RunFitTestAsync());

    // ---- dual connection -------------------------------------------------------------------------

    /// <summary>Official order: this device first, then connected ones.</summary>
    public IReadOnlyList<SourceDevice> SourceDevices =>
        State.SourceDevices?.Value.OrderByDescending(d => d.IsThisDevice).ThenByDescending(d => d.IsConnected).ToArray() ?? [];

    /// <summary>The list is last session's (restored without addresses): shown, but not actionable until the earbuds report it.</summary>
    public bool AreSourceDevicesCached => State.SourceDevices?.Source == StateSource.Cache;

    public byte[]? PreferredAddress => State.PreferredDevice?.Value.Address;

    public bool IsPreferredKnown => State.PreferredDevice is not null;

    public bool CanManageSourceDevices => Model?.SupportsMultiConnectOperations == true || (IsVivo && Has(DeviceCapability.DualDeviceConnection));

    /// <summary>Only the OPPO protocol has a preferred-device setting.</summary>
    public bool HasPreferredDevice => Model?.SupportsMultiConnectOperations == true;

    /// <summary>The dual connection card: a device switch (OPPO family) or a source list reported without one.</summary>
    public bool HasDualConnectionCard => Has(DeviceCapability.DualDeviceConnection)
        && (HasSwitch(DeviceToggle.DualDeviceConnection) || HasSourceListWithoutSwitch);

    /// <summary>Sennheiser: listed hosts can be connected from here; disconnect / remove are never offered (they can clear pairings).</summary>
    public bool CanConnectSourceDevices => CanManageSourceDevices
        || State.Profile is EncoDeck.Protocol.Brands.Sennheiser.SennheiserProfile { CanConnectPairedDevices: true };

    /// <summary>A paired-host list the device reports without a dual-connection switch (vivo, Sennheiser).</summary>
    public bool HasSourceListWithoutSwitch => (IsVivo || State.Profile is EncoDeck.Protocol.Brands.Sennheiser.SennheiserProfile)
        && State.SourceDevices is not null;

    public Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, SourceDevice device) =>
        Run(() => _commands.MultiConnectAsync(operation, device.Address));

    public Task<CommandResult> SetPreferredDeviceAsync(byte[]? address) => Run(() => _commands.SetPreferredDeviceAsync(address));

    // ---- custom EQ -------------------------------------------------------------------------------

    public OppoCustomEqualizerSpec? CustomEqualizerSpec => Has(DeviceCapability.CustomEqualizer) ? Model?.CustomEqualizer : null;

    /// <summary>Other brands' single fixed user curve (gains in 0.1 dB); never added, renamed or deleted.</summary>
    public EncoDeck.Core.Profiles.BandEqualizerSpec? BandEqualizer =>
        State.Profile is OppoDeviceProfile || !Has(DeviceCapability.CustomEqualizer) ? null
        : State.Profile is EncoDeck.Core.Profiles.IBandEqualizerProfile { BandEqualizer: { } spec } ? spec
        // Xiaomi earbuds keep one custom curve in whole dB; its bands and range come from the earbuds' own readback.
        : IsXiaomi && State.CustomEqualizers?.Value is [var curve] ? new(curve.MinGain, curve.MaxGain, GainScale: 1)
        // The Barracuda X has one fixed custom slot in whole dB.
        : IsUsbHeadset ? new(BarracudaXSettingValues.BandMin, BarracudaXSettingValues.BandMax, GainScale: 1)
        : null;

    public IReadOnlyList<CustomEqualizer> CustomEqualizers => State.CustomEqualizers?.Value ?? [];

    public CustomEqualizer? SelectedCustomEqualizer => CustomEqualizers.FirstOrDefault(p => p.IsSelected);

    public bool CanAddCustomEqualizer => CustomEqualizerSpec is { } spec && State.CustomEqualizers is not null && CustomEqualizers.Count < spec.MaxPresets;

    public Task<CommandResult> SelectCustomEqualizerAsync(int id) => Run(() => _commands.SelectCustomEqualizerAsync(id));

    public Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset) => Run(() => _commands.SaveCustomEqualizerAsync(preset));

    public Task<CommandResult> AddCustomEqualizerAsync() => Run(() => _commands.AddCustomEqualizerAsync());

    public Task<CommandResult> DeleteCustomEqualizerAsync(int id) => Run(() => _commands.DeleteCustomEqualizerAsync(id));

    // ---- plumbing --------------------------------------------------------------------------------

    private async Task<CommandResult> Run(Func<Task<CommandResult>> action)
    {
        var result = await _device.RunCommandAsync(action);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void Queue()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            Volatile.Write(ref _queued, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
