// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sony;

internal sealed partial class SonyMdrController
{
    private byte? _eqType;
    private IReadOnlyList<EqualizerPreset> _equalizers = [];

    private async Task RefreshEqualizer(CancellationToken ct)
    {
        byte type = _eqType!.Value;
        if (_equalizers.Count == 0)
        {
            var capability = await Query(0x50, 0x51, type, ct, [type, 1]).ConfigureAwait(false);
            if (capability is null || !SonyEqualizer.TryPresets(capability, out var presets, !_v2)) return;
            _equalizers = presets.Where(p => SonySettings.IsPresetAccepted(Model, byte.Parse(p.Id.AsSpan(5)))).ToArray();
            if (_equalizers.Count == 0) { _eqType = null; return; }
        }
        var value = await Query(0x56, 0x57, type, ct).ConfigureAwait(false);
        if (value is null || !SonyEqualizer.TryParameter(value, out var current, out var bands, !_v2)) return;
        _profile = _profile.WithEqualizers(_equalizers);
        PublishProfile();
        // The report carries the band values of whichever preset is active, so custom curves show up
        // exactly as the headset stored them instead of a guessed flat line.
        Store.TryUpdate(DeviceId, s => s with
        {
            Equalizer = Observed.From(new EqualizerState($"sony-{current}", current, bands), Now, StateSource.QueryResponse),
        });
    }

    public override async Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        return await Exclusive(PendingKeys.Equalizer, async ct =>
        {
            if (!_eqType.HasValue || !_equalizers.Any(p => p.Id == presetId)
                || !byte.TryParse(presetId.AsSpan(5), out byte id)) return NotSupported("EQ preset has not been reported by this device.");
            // A preset is selected with a zero-length band vector; band steps would define a custom EQ
            // instead (WF-LC900 2.0.3 then switches to CUSTOM, mos9527/SonyHeadphonesClient notes).
            if (!await Send(0x58, [_eqType.Value, id, 0], ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
            var value = await Query(0x56, 0x57, _eqType.Value, ct).ConfigureAwait(false);
            if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            bool parsed = false;
            byte actual = 0;
            int[]? bands = null;
            if (value is not null && SonyEqualizer.TryParameter(value, out var readPreset, out var readBands, !_v2))
            {
                parsed = true;
                actual = readPreset;
                bands = readBands;
            }
            var result = value is null ? new CommandResult(CommandStatus.Timeout)
                : parsed && actual == id ? CommandResult.Ok
                : new CommandResult(CommandStatus.Rejected, "The device kept a different EQ preset.");
            Store.TryUpdate(DeviceId, s => s with
            {
                Equalizer = result.IsSuccess
                    ? Observed.From(new EqualizerState(presetId, id, bands), Now, StateSource.CommandConfirmation)
                    : s.Equalizer,
                LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now),
            });
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandResult> SetEqualizerBandsAsync(IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        return await Exclusive(PendingKeys.Equalizer, async ct =>
        {
            if (!_eqType.HasValue) return NotSupported("Equalizer");
            if (bands.Count == 0) return new(CommandStatus.Rejected, "No band values were supplied.");
            if (Store.Current.Equalizer?.Value?.RawPresetValue is not { } preset
                || !SonyEqualizer.EditablePreset((byte)preset))
                return new(CommandStatus.Unsupported, "The active EQ preset does not expose editable bands.");

            var payload = new byte[3 + bands.Count];
            payload[0] = _eqType.Value;
            payload[1] = (byte)preset;
            payload[2] = (byte)bands.Count;
            SonyEqualizer.ToBandBytes(bands).CopyTo(payload, 3);
            if (!await Send(0x58, payload, ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);

            var value = await Query(0x56, 0x57, _eqType.Value, ct).ConfigureAwait(false);
            if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
            if (value is null) return new(CommandStatus.Timeout);
            if (!SonyEqualizer.TryParameter(value, out var reported, out var applied, !_v2))
                return new(CommandStatus.Rejected, "The device sent an unexpected equalizer report.");

            Store.TryUpdate(DeviceId, s => s with
            {
                Equalizer = Observed.From(new EqualizerState($"sony-{reported}", reported, applied), Now, StateSource.CommandConfirmation),
            });
            if (applied.Length != bands.Count) return new(CommandStatus.Rejected, "The device reported a different number of bands.");
            for (int i = 0; i < applied.Length; i++)
            {
                if (applied[i] != Math.Clamp(bands[i], -6, 6)) return new(CommandStatus.Rejected, "The device kept different band values.");
            }
            return CommandResult.Ok;
        }, cancellationToken).ConfigureAwait(false);
    }
}
