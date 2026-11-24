// SPDX-License-Identifier: GPL-3.0-only
// Headband mode sequences adapted from Ariazonaa/momentum4-control (MIT, Copyright (c) 2026 Ariazonaa);
// the MIT notice is reproduced in THIRD_PARTY_NOTICES.md.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sennheiser;

internal sealed partial class SennheiserController
{
    private bool? _ancEnabled, _transparencyEnabled, _adaptiveEnabled;
    private byte? _wind;

    private static bool Boolean(byte[] p) => p is [0 or 1];
    private static bool AncModes(byte[] p)
    {
        if (p.Length != 6 || !p.Where((_, i) => i % 2 == 0).Order().SequenceEqual(new byte[] { 1, 2, 3 })) return false;
        for (int i = 0; i < p.Length; i += 2) if (p[i + 1] > (p[i] == 1 ? 2 : 1)) return false;
        return true;
    }

    private async Task<bool> ReadStatusNoiseAsync(CancellationToken ct)
    {
        // 0x1803 reports the saved transparency *level*, even while transparency is off.
        // Only these independent status queries can establish the three-state selector.
        var anc = await Query(0x1A05, [], Boolean, ct).ConfigureAwait(false);
        var transparency = await Query(0x1805, [], Boolean, ct).ConfigureAwait(false);
        return anc is not null && transparency is not null;
    }

    private bool ModesWanted => Has(SennheiserTraits.AntiWind) || Has(SennheiserTraits.Adaptive);

    private async Task RefreshNoiseAsync(CancellationToken ct)
    {
        bool status = Has(SennheiserTraits.StatusNoise) && await ReadStatusNoiseAsync(ct).ConfigureAwait(false);
        bool modes = status && ModesWanted && await Query(0x1A01, [], AncModes, ct).ConfigureAwait(false) is not null;
        if (!modes) { _wind = null; _adaptiveEnabled = null; }
        // The headband level endpoint stays the fallback for firmware without the status reads.
        _sliderReady = !status && Has(SennheiserTraits.Slider) && await Query(0x1A03, [], p => p is [<= 100], ct).ConfigureAwait(false) is not null;
        _live = _live with { Noise = status || _sliderReady, StatusNoise = status,
            Wind = modes && Has(SennheiserTraits.AntiWind), Adaptive = modes && Has(SennheiserTraits.Adaptive) };
        if (status) PublishNoise(StateSource.QueryResponse);
    }

    private async Task RefreshControlsAsync(CancellationToken ct)
    {
        bool smartPause = Has(SennheiserTraits.SmartPause) && await Query(0x080D, [], Boolean, ct).ConfigureAwait(false) is not null;
        bool lowLatency = Has(SennheiserTraits.LowLatency) && await Query(0x0818, [], Boolean, ct).ConfigureAwait(false) is not null;
        bool gestures = false;
        if (Has(SennheiserTraits.Gestures))
            foreach (var side in new[] { EarSide.Left, EarSide.Right })
            foreach (var action in SennheiserGestures.Actions)
                gestures |= await ReadGestureAsync(side, action, ct).ConfigureAwait(false) is not null;
        _live = _live with { SmartPause = smartPause, LowLatency = lowLatency, Gestures = gestures };
    }

    private void PublishNoise(StateSource source)
    {
        if (_ancEnabled is not bool anc || _transparencyEnabled is not bool transparency) return;
        bool adaptive = Has(SennheiserTraits.Adaptive) && _adaptiveEnabled == true;
        // Headbands: transparent hearing stays flagged after ANC off but is not heard; TW4 reports transparency first.
        var mode = Has(SennheiserTraits.TransparencyNeedsAnc)
            ? !anc ? NoiseControlMode.Off : transparency ? NoiseControlMode.Transparency : adaptive ? NoiseControlMode.Adaptive : NoiseControlMode.NoiseCancellation
            : transparency ? NoiseControlMode.Transparency : !anc ? NoiseControlMode.Off : adaptive ? NoiseControlMode.Adaptive : NoiseControlMode.NoiseCancellation;
        // Anti-wind 2 ("auto") has no selectable option: shown as its raw value, never rewritten.
        string? level = mode == NoiseControlMode.NoiseCancellation && Has(SennheiserTraits.AntiWind) && _wind is { } wind
            ? wind switch { 0 => "normal", 1 => "anti-wind", _ => "wind:" + wind } : null;
        Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(mode, level), Now, source) });
    }

    private bool HandleNoisePacket(BrandPacket packet, ushort command)
    {
        if (!Has(SennheiserTraits.StatusNoise)) return false;
        byte[] p = packet.Payload;
        var source = packet.Type == 1 ? StateSource.DeviceNotification : StateSource.QueryResponse;
        if (command == 0x1A05 && Boolean(p)) _ancEnabled = p[0] == 1;
        else if (command == 0x1805 && Boolean(p)) _transparencyEnabled = p[0] == 1;
        // Observed TW4 transparency notifications use the ANC feature's level notification.
        // Never apply this rule to a GET level or to the headband slider protocol.
        else if (profile.IsTw4 && packet.Type == 1 && command == 0x1A03 && p is [<= 100]) _transparencyEnabled = p[0] > 0;
        else if (command == 0x1A01 && AncModes(p))
        {
            for (int i = 0; i < p.Length; i += 2)
            {
                if (p[i] == 1) _wind = p[i + 1];
                else if (p[i] == 3) _adaptiveEnabled = p[i + 1] == 1;
            }
        }
        else return false;
        PublishNoise(source);
        return true;
    }

    private bool HandleControlPacket(BrandPacket packet, ushort command)
    {
        byte[] p = packet.Payload;
        var source = packet.Type == 1 ? StateSource.DeviceNotification : StateSource.QueryResponse;
        if ((command == 0x080D && Has(SennheiserTraits.SmartPause) || command == 0x0818 && Has(SennheiserTraits.LowLatency)) && Boolean(p))
        {
            var toggle = command == 0x080D ? DeviceToggle.InEarDetection : DeviceToggle.GamingMode;
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(toggle, Observed.From(p[0] == 1, Now, source)) });
            return true;
        }
        if (Has(SennheiserTraits.Gestures) && packet.Type == 2 && command == 0x1601 && SennheiserGestures.ValidSlot(p))
        {
            var key = new KeyFunction(p[0] == 1 ? EarSide.Left : EarSide.Right, 0, (GestureAction)p[1], p[2]);
            Store.TryUpdate(DeviceId, s => s with
            {
                KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(
                    [.. (s.KeyFunctions?.Value ?? []).Where(k => k.Side != key.Side || k.Action != key.Action), key], Now, source),
            });
            return true;
        }
        return false;
    }

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_live.StatusNoise) return await SetSliderNoiseAsync(target, cancellationToken).ConfigureAwait(false);
        if (target.Mode is not (NoiseControlMode.Off or NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency or NoiseControlMode.Adaptive)
            || target.Mode == NoiseControlMode.Adaptive && (!_live.Adaptive || target.LevelId is not null)
            || (target.LevelId is not null && (target.Mode != NoiseControlMode.NoiseCancellation || !_live.Wind || target.LevelId is not ("normal" or "anti-wind"))))
            return NotSupported("Sennheiser noise mode");
        return await RunCommandAsync(PendingKeys.NoiseControl, Operations, async ct =>
        {
            var result = await WriteNoiseAsync(target, ct).ConfigureAwait(false);
            // Even a partial failure can change the device. Render its actual state, never the request/ACK.
            bool read = await ReadStatusNoiseAsync(ct).ConfigureAwait(false);
            if (_live.Wind || _live.Adaptive) read &= await Query(0x1A01, [], AncModes, ct).ConfigureAwait(false) is not null;
            if (!result.IsSuccess) return result;
            if (!read) return TimedOut;
            bool matches = Store.Current.NoiseControl?.Value.Mode == target.Mode
                && (target.Mode != NoiseControlMode.NoiseCancellation || !_live.Wind || _wind == (target.LevelId == "anti-wind" ? 1 : 0));
            return matches ? CommandResult.Ok : new(CommandStatus.Rejected, "Noise-control readback did not match.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> WriteNoiseAsync(NoiseControlState target, CancellationToken ct)
    {
        if (target.Mode == NoiseControlMode.Transparency)
        {
            // Headbands: switching ANC on clears transparent hearing, so ANC first, then transparency.
            if (Has(SennheiserTraits.TransparencyNeedsAnc) && _ancEnabled != true)
            {
                var on = await SetValueAsync(0x1A04, [1], ct).ConfigureAwait(false);
                if (!on.IsSuccess) return on;
            }
            return await SetValueAsync(0x1804, [1], ct).ConfigureAwait(false);
        }
        var result = await SetValueAsync(0x1804, [0], ct).ConfigureAwait(false);
        if (!result.IsSuccess) return result;
        if (target.Mode == NoiseControlMode.Adaptive)
        {
            result = await SetValueAsync(0x1A04, [1], ct).ConfigureAwait(false);
            // Single (mode, value) pairs: some firmware rejects the full table.
            return result.IsSuccess ? await SetValueAsync(0x1A00, [3, 1], ct).ConfigureAwait(false) : result;
        }
        if (target.Mode == NoiseControlMode.NoiseCancellation && _live.Adaptive)
            result = await SetValueAsync(0x1A00, [3, 0], ct).ConfigureAwait(false);
        if (result.IsSuccess && target.Mode == NoiseControlMode.NoiseCancellation && _live.Wind)
            result = await SetValueAsync(0x1A00, [1, target.LevelId == "anti-wind" ? (byte)1 : (byte)0], ct).ConfigureAwait(false);
        if (result.IsSuccess)
            result = await SetValueAsync(0x1A04, [target.Mode == NoiseControlMode.Off ? (byte)0 : (byte)1], ct).ConfigureAwait(false);
        return result;
    }

    private async Task<CommandResult> SetSliderNoiseAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_sliderReady || target.LevelId is not null || target.Mode is not (NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            return CommandResult.Unsupported("This model exposes confirmed ANC/transparency endpoints only; ANC off is not inferred.");
        return await RunCommandAsync(PendingKeys.NoiseControl, Operations, async ct =>
        {
            byte level = target.Mode == NoiseControlMode.Transparency ? (byte)100 : (byte)0;
            if (await Query(0x1A02, [level], p => p.Length == 0, ct).ConfigureAwait(false) is null) return TimedOut;
            var readback = await Query(0x1A03, [], p => p is [<= 100], ct).ConfigureAwait(false);
            return readback is null ? TimedOut : readback.Payload[0] == level ? CommandResult.Ok : new(CommandStatus.Rejected, "Transparency readback did not match.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        if (!(toggle == DeviceToggle.InEarDetection && _live.SmartPause || toggle == DeviceToggle.GamingMode && _live.LowLatency))
            return NotSupported(toggle.ToString());
        return await RunCommandAsync(PendingKeys.Toggle(toggle), Operations, async ct =>
        {
            ushort set = toggle == DeviceToggle.InEarDetection ? (ushort)0x080C : (ushort)0x0817;
            var result = await SetValueAsync(set, [enabled ? (byte)1 : (byte)0], ct).ConfigureAwait(false);
            var read = await Query((ushort)(set + 1), [], Boolean, ct).ConfigureAwait(false);
            return !result.IsSuccess ? result : read is null ? TimedOut : (read.Payload[0] == 1) == enabled
                ? CommandResult.Ok : new(CommandStatus.Rejected, "Setting readback did not match.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<BrandPacket?> ReadGestureAsync(EarSide side, GestureAction action, CancellationToken ct) =>
        Query(0x1601, [SennheiserGestures.WireSide(side), (byte)action],
            p => SennheiserGestures.ValidSlot(p) && p[0] == SennheiserGestures.WireSide(side) && p[1] == (byte)action, ct);

    public override async Task<CommandResult> SetKeyFunctionAsync(KeyFunction assignment, CancellationToken cancellationToken = default)
    {
        if (!_live.Gestures || assignment.Side is not (EarSide.Left or EarSide.Right) || assignment.Button != 0
            || !SennheiserGestures.Functions(assignment.Action).Contains(assignment.Function)) return NotSupported("TW4 gesture");
        return await RunCommandAsync(PendingKeys.KeyFunction, Operations, async ct =>
        {
            // Re-read this exact slot before allowing a write. Unsupported/unknown slots stay untouched.
            if (await ReadGestureAsync(assignment.Side, assignment.Action, ct).ConfigureAwait(false) is null) return TimedOut;
            var result = await SetValueAsync(0x1600, [SennheiserGestures.WireSide(assignment.Side), (byte)assignment.Action, (byte)assignment.Function], ct).ConfigureAwait(false);
            var read = await ReadGestureAsync(assignment.Side, assignment.Action, ct).ConfigureAwait(false);
            return !result.IsSuccess ? result : read is null ? TimedOut : read.Payload[2] == assignment.Function
                ? CommandResult.Ok : new(CommandStatus.Rejected, "TW4 gesture readback did not match.");
        }, cancellationToken).ConfigureAwait(false);
    }
}
