// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Ambient level, button control, game mode, sound effect and power-off timer follow mEDIFIER
// (https://github.com/wh201906/mEDIFIER, 10056b5, MIT, Copyright (c) 2023 Self Not Found) and the
// write-then-read-back flow of Macifier (https://github.com/luanyufei/Macifier, 5479859, MIT,
// Copyright (c) 2024-present Macifier Contributors); MIT notices in THIRD_PARTY_NOTICES.md. BES v2
// XOR payloads, noise slots and TWS models follow HyperEars (https://github.com/silverpoetry/HyperEars,
// 361d279, GPL-3.0-only) and BudsLink (https://github.com/maniacx/BudsLink, ba3160d, GPL-3.0).
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Edifier;

/// <summary>
/// Controller for EDIFIER headphones and earbuds over SPP or GATT: battery, noise control and the
/// settings each model is known to have. The device has no handshake and few unsolicited
/// notifications, so state is polled. Every optional setting is offered only after the headset
/// answered its query, and every write is confirmed by reading the setting back.
/// </summary>
public sealed class EdifierDeviceController : BrandControllerBase
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The W860NB PRO plays a ~1.9 s voice prompt after a noise change and ignores requests meanwhile (HyperEars).</summary>
    private static readonly TimeSpan PromptRetryDelay = TimeSpan.FromSeconds(2);

    private EdifierFrameDecoder _decoder = new();
    private readonly Lock _gate = new();
    private readonly List<(byte Command, TaskCompletionSource<EdifierFrame> Waiter)> _waiters = [];
    private readonly CancellationTokenSource _pollStop = new();
    private Task? _pollTask;
    private readonly TaskCompletionSource _deferredSettings = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EdifierProfile _profile;
    private EdifierFeatures _confirmed;
    private bool _version2;
    private bool _xor;
    private bool _xorKnown;

    public EdifierDeviceController(IDuplexByteChannel channel, ControllerContext context, EdifierProfile? profile = null)
        : base(channel, context)
    {
        _profile = profile ?? new EdifierProfile("edifier", "EDIFIER headphones", "edifier", isGeneric: true);
        _version2 = Spec.Framing != EdifierFraming.V1;
        _xor = Spec.Framing == EdifierFraming.V2Xor;
        _xorKnown = Spec.Framing != EdifierFraming.Negotiate;
        _decoder = new EdifierFrameDecoder(_version2);
    }

    private EdifierSpec Spec => _profile.Spec;

    private bool IsTws => _profile.IsTws;

    private EdifierProfile CurrentProfile
    {
        get
        {
            EdifierFeatures confirmed;
            lock (_gate) confirmed = _confirmed;
            return _profile.WithConfirmed(confirmed, Context.AllowExperimental());
        }
    }

    private bool Has(EdifierFeatures feature)
    {
        lock (_gate) return _confirmed.HasFlag(feature);
    }

    /// <summary>
    /// Noise control is read only where the dialect is known: v1 headsets, the community-recorded
    /// v2 slots, and unknown TWS only after falling back to v1 (some firmware drops the link on it).
    /// </summary>
    private bool CanQueryNoise => Spec.Anc switch
    {
        EdifierAnc.None => false,
        EdifierAnc.Probe => !_version2,
        _ => true,
    };

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        Store.TryUpdate(DeviceId, s => s with { Profile = CurrentProfile, NoiseControl = IsTws ? null : s.NoiseControl });
        bool battery = await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        if (Spec.Framing == EdifierFraming.Negotiate && !battery)
        {
            // Negotiate with read-only state queries; never send a control to guess a version.
            lock (_gate)
            {
                _version2 = false;
                _xor = false;
                _xorKnown = true;
                _decoder = new EdifierFrameDecoder();
            }
            battery = await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        }
        bool noise = CanQueryNoise && await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!battery && !noise && Spec.Framing == EdifierFraming.V1)
        {
            battery = await TryBesFramingAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!battery && !noise)
        {
            // An open channel that answers nothing is not speaking this protocol.
            Logger.LogInformation("EDIFIER device answered neither battery nor noise query");
            return false;
        }
        await QuerySettingsAsync(cancellationToken).ConfigureAwait(false);
        _pollTask ??= Task.Run(() => PollAsync(_pollStop.Token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// A v1 headset that answered nothing may be a later edition on BES v2 framing sold under the
    /// same name (W820NB 三金标版 answers neither v1 query). Only the read-only battery query is sent;
    /// on an answer the session continues with battery alone, because the v1 noise layout and
    /// settings are not known to carry over to v2 firmware.
    /// </summary>
    private async Task<bool> TryBesFramingAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _profile = _profile.WithSpec(EdifierSpec.BesFallback(Spec));
            _confirmed = EdifierFeatures.None;
            _version2 = true;
            _xor = false;
            _xorKnown = false;
            _decoder = new EdifierFrameDecoder(version2: true);
        }
        if (!await RefreshBatteryAsync(ct).ConfigureAwait(false)) return false;
        bool xor;
        lock (_gate) xor = _xor;
        Logger.LogInformation("EDIFIER headset answered BES v2 framing ({Payloads} payloads); offering battery only", xor ? "XOR" : "plaintext");
        Store.TryUpdate(DeviceId, s => s with { Profile = CurrentProfile, NoiseControl = null });
        return true;
    }

    /// <summary>One read per setting the model is known to have; an answer confirms it for this session.</summary>
    private async Task QuerySettingsAsync(CancellationToken ct)
    {
        var features = Spec.Features;
        if (features.HasFlag(EdifierFeatures.KeyControl)) await QueryAsync(EdifierConstants.QueryKeyControl, [EdifierConstants.KeyControlSlot], ct).ConfigureAwait(false);
        if (features.HasFlag(EdifierFeatures.SoundEffect)) await QueryAsync(EdifierConstants.QuerySoundEffect, [], ct).ConfigureAwait(false);
        if (features.HasFlag(EdifierFeatures.InEarSwitch)) await QueryAsync(EdifierConstants.QueryInEarSwitch, [], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Game mode and the auto power-off timer are read after the session is ready: a variant that
    /// does not answer them would otherwise hold back battery and noise control for a full request
    /// timeout each. Either setting still appears only once the headset answered for it.
    /// </summary>
    private async Task QueryDeferredSettingsAsync(CancellationToken ct)
    {
        try
        {
            var features = Spec.Features;
            if (features.HasFlag(EdifierFeatures.GameMode)) await QueryAsync(EdifierConstants.QueryGameMode, [], ct).ConfigureAwait(false);
            if (features.HasFlag(EdifierFeatures.PowerOffTimer)) await QueryAsync(EdifierConstants.QueryPowerOffTimer, [], ct).ConfigureAwait(false);
        }
        finally
        {
            _deferredSettings.TrySetResult();
        }
    }

    /// <summary>Completes once the deferred game-mode / power-off reads have been answered or timed out.</summary>
    internal Task DeferredSettingsRead => _deferredSettings.Task;

    private async Task<EdifierFrame?> QueryAsync(byte command, byte[] payload, CancellationToken ct)
    {
        var frame = await RequestAsync(command, payload, ct).ConfigureAwait(false);
        return frame is not null && Apply(frame, StateSource.QueryResponse) ? frame : null;
    }

    protected override void OnReceived(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<EdifierFrame> frames;
        bool version2;
        lock (_gate)
        {
            frames = _decoder.Feed(data);
            version2 = _version2;
        }
        if (frames.Count == 0 && Context.Trace is { } trace)
        {
            // Only the head bytes: enough to tell another framing from a partial frame.
            trace.OnBrandFrame(FrameDirection.Received,
                $"EDIFIER no complete frame in {data.Length} bytes, head {Convert.ToHexString(data[..Math.Min(3, data.Length)])}", Now);
        }
        foreach (var raw in frames)
        {
            Touch();
            var frame = Decrypt(raw);
            TraceFrame(FrameDirection.Received, frame.Command, frame.Payload, version2);
            TaskCompletionSource<EdifierFrame>? waiter = null;
            lock (_gate)
            {
                int index = _waiters.FindIndex(w => w.Command == frame.Command);
                if (index >= 0)
                {
                    waiter = _waiters[index].Waiter;
                    _waiters.RemoveAt(index);
                }
            }
            if (waiter is not null)
            {
                waiter.TrySetResult(frame);
            }
            else
            {
                // Late or unsolicited answer: still apply what it says.
                Apply(frame, StateSource.DeviceNotification);
            }
        }
    }

    /// <summary>
    /// Removes the v2 payload XOR. When the model's dialect is unknown, the first TWS state answer
    /// decides: its leading peer byte is 1–3 in plaintext and 0xA4/0xA7/0xA6 when XORed.
    /// </summary>
    private EdifierFrame Decrypt(EdifierFrame frame)
    {
        lock (_gate)
        {
            if (!_version2) return frame;
            if (!_xorKnown && frame.Command == EdifierConstants.QueryTwsState && frame.Payload is [var peer, ..])
            {
                if (peer is 1 or 2 or 3)
                {
                    _xorKnown = true;
                }
                else if ((peer ^ EdifierConstants.PayloadXor) is 1 or 2 or 3)
                {
                    _xor = _xorKnown = true;
                }
            }
            // A headset's battery answer decides the same way: a percentage never has the top bit
            // set in plaintext and always has it once XORed with 0xA5.
            if (!_xorKnown && !Spec.StateBattery && frame.Command == EdifierConstants.QueryBattery && frame.Payload is [var percent, ..])
            {
                if (percent <= 100)
                {
                    _xorKnown = true;
                }
                else if ((percent ^ EdifierConstants.PayloadXor) <= 100)
                {
                    _xor = _xorKnown = true;
                }
            }
            if (!_xor) return frame;
        }
        byte[] payload = [.. frame.Payload];
        EdifierFrameCodec.Xor(payload);
        return frame with { Payload = payload };
    }

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        bool battery = await RefreshBatteryAsync(cancellationToken).ConfigureAwait(false);
        bool noise = CanQueryNoise && await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (Has(EdifierFeatures.GameMode)) await QueryAsync(EdifierConstants.QueryGameMode, [], cancellationToken).ConfigureAwait(false);
        bool noiseExpected = CanQueryNoise && (Spec.StaticNoise && !IsTws || Has(EdifierFeatures.Noise));
        return battery && (noise || !noiseExpected) ? CommandResult.Ok : new CommandResult(CommandStatus.Timeout, "Device did not answer every query.");
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        var profile = CurrentProfile;
        if (profile.NoiseControlOptions.Count == 0) return NotSupported("Noise control has not been confirmed by this device.");
        if (!profile.NoiseControlOptions.Any(o => o.Matches(target))
            || EdifierSettings.EncodeNoise(Spec.Anc, target, Has(EdifierFeatures.AmbientLevel)) is not { } payload)
        {
            return NotSupported($"Noise mode {target.Mode}/{target.LevelId}");
        }
        if (Closed.IsCompleted) return CommandResult.NotConnected;

        var pending = new PendingCommand(Guid.NewGuid(), "noise-control", Now);
        DeviceState? before = null;
        Store.TryUpdate(DeviceId, s =>
        {
            before = s;
            return s with
            {
                NoiseControl = Observed.From(target, Now, StateSource.Optimistic),
                PendingCommands = s.PendingCommands.Add(pending),
                LastError = null,
            };
        });

        CommandResult result;
        try
        {
            var ack = await RequestAsync(EdifierConstants.SetNoiseMode, payload, cancellationToken).ConfigureAwait(false);
            // Even without an ack the write may have landed: trust a matching read-back.
            var actual = await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false);
            if (actual is null && Spec.Anc == EdifierAnc.SlotW860)
            {
                await Task.Delay(PromptRetryDelay, Context.Time, cancellationToken).ConfigureAwait(false);
                actual = await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false);
            }
            if (actual is null)
            {
                result = new CommandResult(CommandStatus.Timeout, ack is null ? "noise-control" : "Read-back");
            }
            else
            {
                result = EdifierSettings.Satisfies(actual, target)
                    ? CommandResult.Ok
                    : new CommandResult(ack is null ? CommandStatus.Timeout : CommandStatus.Rejected, "Device reports a different noise mode.");
            }
        }
        catch (OperationCanceledException)
        {
            result = new CommandResult(CommandStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Noise control command failed");
            result = new CommandResult(CommandStatus.TransportError, ex.Message);
        }

        Store.TryUpdate(DeviceId, s =>
        {
            var next = s with { PendingCommands = s.PendingCommands.RemoveAll(p => p.Id == pending.Id) };
            if (result.IsSuccess) return next;
            if (before is not null && next.NoiseControl?.Source == StateSource.Optimistic) next = next with { NoiseControl = before.NoiseControl };
            return next with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) };
        });
        Logger.LogInformation("Command noise-control -> {Result}", result);
        return result;
    }

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        byte value = enabled ? (byte)1 : (byte)0;
        return toggle switch
        {
            DeviceToggle.GamingMode when CurrentProfile.Offers(EdifierFeatures.GameMode) => WriteSettingAsync(
                $"toggle:{toggle}", EdifierConstants.SetGameMode, [value], EdifierConstants.QueryGameMode, [],
                s => s.Toggles.TryGetValue(toggle, out var on) && on.Value == enabled, cancellationToken),
            DeviceToggle.InEarDetection when CurrentProfile.Offers(EdifierFeatures.InEarSwitch) => WriteSettingAsync(
                $"toggle:{toggle}", EdifierConstants.SetInEarSwitch, [value], EdifierConstants.QueryInEarSwitch, [],
                s => s.Toggles.TryGetValue(toggle, out var on) && on.Value == enabled, cancellationToken),
            _ => Task.FromResult(NotSupported(toggle.ToString())),
        };
    }

    /// <summary>Button control: which noise modes the physical button cycles (one of the official masks).</summary>
    public override Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default)
    {
        if (!CurrentProfile.Offers(EdifierFeatures.KeyControl)
            || !EdifierSettings.KeyControlSlot.Matches(assignment)
            || !EdifierConstants.KeyControlMasks.Contains(assignment.Function))
        {
            return Task.FromResult(NotSupported("This button setting"));
        }
        byte mask = (byte)assignment.Function;
        return WriteSettingAsync("key-function", EdifierConstants.SetKeyControl, [EdifierConstants.KeyControlSlot, mask],
            EdifierConstants.QueryKeyControl, [EdifierConstants.KeyControlSlot],
            s => s.KeyFunctions?.Value.Any(k => EdifierSettings.KeyControlSlot.Matches(k) && k.Function == mask) == true, cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!CurrentProfile.Offers(EdifierFeatures.SoundEffect) || !EdifierSettings.TryParsePresetId(presetId, out byte value))
        {
            return Task.FromResult(NotSupported("This equalizer preset"));
        }
        return WriteSettingAsync("equalizer", EdifierConstants.SetSoundEffect, [value], EdifierConstants.QuerySoundEffect, [],
            s => s.Equalizer?.Value.ActivePresetId == presetId, cancellationToken);
    }

    /// <summary>Id of the power-off timer in <see cref="DeviceState.Options"/> (value in minutes, 0 = off).</summary>
    internal const string PowerOffOption = "edifier-power-off-timer";

    /// <summary>
    /// The power-off timer as a device option: off plus the official durations, and the reported value
    /// when it is not one of them (shown as reported, never offered for writing by the controller).
    /// </summary>
    internal static DeviceOption PowerOffTimerOption(int minutes)
    {
        int[] values = [0, .. EdifierConstants.PowerOffTimerMinutes, .. EdifierConstants.PowerOffTimerMinutes.Contains(minutes) || minutes == 0 ? [] : new[] { minutes }];
        var choices = values.Select(m => m == 0 ? new DeviceOptionChoice(0, "AutoOff_Off")
            : m % 60 == 0 ? new DeviceOptionChoice(m, "PowerOff_Hours", (m / 60).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DeviceOptionChoice(m, "PowerOff_Minutes", m.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return new DeviceOption(PowerOffOption, "PowerOff_Timer", minutes, choices, 10) { DescriptionKey = "PowerOff_Timer_Description" };
    }

    private static int? PowerOffMinutes(DeviceState state) =>
        state.Options.TryGetValue(PowerOffOption, out var option) ? option.Value.Value : null;

    /// <summary>
    /// Power-off timer: zero disables it (never a zero-minute set, which powers off at once); other
    /// values must be one of the official choices. Confirmed by reading the timer back.
    /// </summary>
    public override Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken cancellationToken = default)
    {
        if (id != PowerOffOption || !CurrentProfile.Offers(EdifierFeatures.PowerOffTimer)) return Task.FromResult(NotSupported($"Option {id}"));
        int minutes = value;
        if (minutes != 0 && !EdifierConstants.PowerOffTimerMinutes.Contains(minutes)) return Task.FromResult(NotSupported($"A {minutes}-minute timer"));
        return minutes == 0
            ? WriteSettingAsync("power-off-timer", EdifierConstants.DisablePowerOffTimer, [], EdifierConstants.QueryPowerOffTimer, [],
                s => PowerOffMinutes(s) == 0, cancellationToken)
            : WriteSettingAsync("power-off-timer", EdifierConstants.SetPowerOffTimer, [(byte)(minutes >> 8), (byte)minutes], EdifierConstants.QueryPowerOffTimer, [],
                s => PowerOffMinutes(s) == minutes, cancellationToken);
    }

    /// <summary>
    /// Sends a setting, then reads it back and checks the store; the write's own echo or
    /// acknowledgement is never taken as the new state.
    /// </summary>
    private async Task<CommandResult> WriteSettingAsync(string description, byte command, byte[] payload, byte queryCommand, byte[] queryPayload,
        Func<DeviceState, bool> applied, CancellationToken ct)
    {
        if (Closed.IsCompleted) return CommandResult.NotConnected;
        var pending = new PendingCommand(Guid.NewGuid(), description, Now);
        Store.TryUpdate(DeviceId, s => s with { PendingCommands = s.PendingCommands.Add(pending), LastError = null });
        CommandResult result;
        try
        {
            var ack = await RequestAsync(command, payload, ct).ConfigureAwait(false);
            var readBack = await QueryAsync(queryCommand, queryPayload, ct).ConfigureAwait(false);
            if (readBack is null) result = new CommandResult(CommandStatus.Timeout, ack is null ? description : "Read-back");
            else if (applied(Store.Current)) result = CommandResult.Ok;
            else result = new CommandResult(ack is null ? CommandStatus.Timeout : CommandStatus.Rejected, "The headset kept the previous setting.");
        }
        catch (OperationCanceledException)
        {
            result = new CommandResult(CommandStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Command {Description} failed", description);
            result = new CommandResult(CommandStatus.TransportError, ex.Message);
        }
        Store.TryUpdate(DeviceId, s =>
        {
            var next = s with { PendingCommands = s.PendingCommands.RemoveAll(p => p.Id == pending.Id) };
            return result.IsSuccess || result.Status == CommandStatus.Cancelled
                ? next
                : next with { LastError = new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) };
        });
        Logger.LogInformation("Command {Description} -> {Result}", description, result);
        return result;
    }

    private async Task<bool> RefreshBatteryAsync(CancellationToken cancellationToken)
    {
        var frame = await RequestAsync(Spec.StateBattery ? EdifierConstants.QueryTwsState : EdifierConstants.QueryBattery, [], cancellationToken).ConfigureAwait(false);
        return frame is not null && Apply(frame, StateSource.QueryResponse);
    }

    /// <summary>Queries the noise mode; returns the parsed state, or null without a valid answer.</summary>
    private async Task<NoiseControlState?> RefreshNoiseAsync(CancellationToken cancellationToken)
    {
        var frame = await RequestAsync(EdifierConstants.QueryNoiseMode, [], cancellationToken).ConfigureAwait(false);
        if (frame is null || !Apply(frame, StateSource.QueryResponse)) return null;
        return Store.Current.NoiseControl?.Value;
    }

    /// <summary>Applies an answer to the store; returns whether it was valid for this model.</summary>
    private bool Apply(EdifierFrame frame, StateSource source)
    {
        var now = Now;
        var p = frame.Payload;
        switch (frame.Command)
        {
            case EdifierConstants.QueryTwsState when Spec.StateBattery && ParseTwsBattery(p, now, source) is { } battery:
                Store.TryUpdate(DeviceId, s => s with { Battery = battery, LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryBattery when !Spec.StateBattery && p is [var percent, ..] && percent <= 100:
                // Over-ear/neckband: one reading for the whole headset, shown in the Left slot.
                var reading = Observed.From(new BatteryReading(percent, null), now, source);
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery with { Left = reading }, LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryNoiseMode when CanQueryNoise && (Spec.Anc != EdifierAnc.Probe || p.Length is 1 or 2):
                bool ambient = Spec.Features.HasFlag(EdifierFeatures.AmbientLevel) && p.Length >= 2 && EdifierSettings.AmbientLevel(p[1]) is not null;
                if (EdifierSettings.DecodeNoise(Spec.Anc, p, ambient || Has(EdifierFeatures.AmbientLevel)) is not { } noise) return false;
                Confirm(ambient ? EdifierFeatures.Noise | EdifierFeatures.AmbientLevel : EdifierFeatures.Noise);
                Store.TryUpdate(DeviceId, s => s.NoiseControl?.Source == StateSource.Optimistic && source == StateSource.DeviceNotification
                    ? s
                    : s with { NoiseControl = Observed.From(noise, now, source), LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryGameMode when Spec.Features.HasFlag(EdifierFeatures.GameMode) && p is [var game] && game <= 1:
                Confirm(EdifierFeatures.GameMode);
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.GamingMode, Observed.From(game == 1, now, source)), LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryInEarSwitch when Spec.Features.HasFlag(EdifierFeatures.InEarSwitch) && p is [var inEar] && inEar <= 1:
                Confirm(EdifierFeatures.InEarSwitch);
                Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.InEarDetection, Observed.From(inEar == 1, now, source)), LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryKeyControl when Spec.Features.HasFlag(EdifierFeatures.KeyControl) && p is [EdifierConstants.KeyControlSlot, var mask]:
                // Unknown masks are shown as reported and never rewritten.
                Confirm(EdifierFeatures.KeyControl);
                IReadOnlyList<KeyFunction> keys = [new(EarSide.Both, 0, GestureAction.SingleSqueeze, mask)];
                Store.TryUpdate(DeviceId, s => s with { KeyFunctions = Observed.From(keys, now, source), LastUpdateSource = source });
                return true;
            case EdifierConstants.QuerySoundEffect when Spec.Features.HasFlag(EdifierFeatures.SoundEffect) && p is [var effect]:
                Confirm(EdifierFeatures.SoundEffect);
                string? preset = effect <= 3 ? EdifierSettings.PresetId(effect) : null;
                Store.TryUpdate(DeviceId, s => s with { Equalizer = Observed.From(new EqualizerState(preset, effect), now, source), LastUpdateSource = source });
                return true;
            case EdifierConstants.QueryPowerOffTimer when Spec.Features.HasFlag(EdifierFeatures.PowerOffTimer) && ParsePowerOffTimer(p) is { } minutes:
                Confirm(EdifierFeatures.PowerOffTimer);
                Store.TryUpdate(DeviceId, s => s with { Options = s.Options.SetItem(PowerOffOption, Observed.From(PowerOffTimerOption(minutes), now, source)), LastUpdateSource = source });
                return true;
            default:
                return false;
        }
    }

    /// <summary>Records a newly answered setting and publishes the profile that offers it.</summary>
    private void Confirm(EdifierFeatures features)
    {
        lock (_gate)
        {
            if ((_confirmed & features) == features) return;
            _confirmed |= features;
        }
        var profile = CurrentProfile;
        Store.TryUpdate(DeviceId, s => s.Profile is null || s.Profile.ModelId == profile.ModelId ? s with { Profile = profile } : s);
    }

    // Edifier Connect 8.5.3 CommandParser.parseDeviceState / parseTWSBattery.
    // 0 means offline, 255 means unavailable. Neither is a zero-percent battery.
    // Peer 1/2 means only left/right is connected; do not keep a stale other-ear reading.
    // Byte 4 = 3 marks the case as offline (HyperEars PR #54); its other values are not used.
    internal static BatteryStatus? ParseTwsBattery(byte[] payload, DateTimeOffset now, StateSource source)
    {
        if (payload.Length < 3 || payload[0] is not (1 or 2 or 3)) return null;
        Observed<BatteryReading>? Read(byte value) => value is >= 1 and <= 100
            ? Observed.From(new BatteryReading(value, null), now, source) : null;
        bool caseOffline = payload.Length >= 5 && payload[4] == 3;
        return new BatteryStatus(
            payload[0] == 2 ? null : Read(payload[1]),
            payload[0] == 1 ? null : Read(payload[2]),
            payload.Length >= 4 && !caseOffline ? Read(payload[3]) : null);
    }

    /// <summary><c>[00]</c> = off; <c>[hi][lo]</c> = minutes (a two-byte zero is also off).</summary>
    internal static int? ParsePowerOffTimer(byte[] payload) => payload switch
    {
        [0] => 0,
        [var hi, var lo] => (hi << 8) | lo,
        _ => null,
    };

    /// <summary>Sends a request and waits for the frame echoing its command, or null on timeout.</summary>
    private async Task<EdifierFrame?> RequestAsync(byte command, byte[] payload, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<EdifierFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool version2, xor;
        lock (_gate)
        {
            _waiters.Add((command, waiter));
            version2 = _version2;
            xor = _xor;
        }
        try
        {
            byte[] request = version2 ? EdifierFrameCodec.EncodeV2(command, payload, xor: xor) : EdifierFrameCodec.Encode(command, payload);
            TraceFrame(FrameDirection.Sent, command, payload, version2);
            if (!await WriteAsync(request, cancellationToken).ConfigureAwait(false)) return null;
            return await waiter.Task.WaitAsync(RequestTimeout, Context.Time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate) _waiters.RemoveAll(w => w.Waiter == waiter);
        }
    }

    /// <summary>Commands whose payloads are levels and settings, never names or addresses.</summary>
    private static readonly HashSet<byte> TracedPayloads =
    [
        EdifierConstants.QueryBattery, EdifierConstants.QueryTwsState, EdifierConstants.QueryNoiseMode, EdifierConstants.SetNoiseMode,
        EdifierConstants.QueryGameMode, EdifierConstants.SetGameMode, EdifierConstants.QueryPowerOffTimer, EdifierConstants.SetPowerOffTimer,
        EdifierConstants.DisablePowerOffTimer, EdifierConstants.QueryKeyControl, EdifierConstants.SetKeyControl,
        EdifierConstants.SetSoundEffect, EdifierConstants.QuerySoundEffect, EdifierConstants.QueryInEarSwitch, EdifierConstants.SetInEarSwitch,
    ];

    /// <summary>Raw protocol log: plaintext payloads of known commands; only the length of anything else.</summary>
    private void TraceFrame(FrameDirection direction, byte command, byte[] payload, bool version2)
    {
        if (Context.Trace is not { } trace) return;
        string body = TracedPayloads.Contains(command) ? Convert.ToHexString(payload) : $"<{payload.Length} bytes>";
        trace.OnBrandFrame(direction, $"EDIFIER {(version2 ? "v2" : "v1")} {command:X2} {body}", Now);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            await QueryDeferredSettingsAsync(cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(PollInterval, Context.Time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false) && !Closed.IsCompleted)
            {
                if (Store.Current.PendingCommands.Count > 0) continue;
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "EDIFIER polling stopped");
        }
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        await _pollStop.CancelAsync().ConfigureAwait(false);
        if (_pollTask is { } poll)
        {
            try
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        _pollStop.Dispose();
    }
}
