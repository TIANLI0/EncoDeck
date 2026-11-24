// SPDX-License-Identifier: GPL-3.0-only
// User EQ and sound-mode protocol adapted from Ariazonaa/momentum4-control (MIT, Copyright (c) 2026 Ariazonaa)
// and andr3-santos/momentum-menu (MIT, Copyright (c) 2026 André Santos; firmware 3.x gate);
// the MIT notice is reproduced in THIRD_PARTY_NOTICES.md.
using System.Buffers.Binary;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sennheiser;

internal sealed partial class SennheiserController
{
    /// <summary>The single user curve's id in <see cref="DeviceState.CustomEqualizers"/>; sound mode 1 plays it.</summary>
    internal const int UserCurveId = 1;
    private const byte CurveMode = 1;
    private int[] _eqFrequencies = [];
    private int[]? _eqGains;
    private byte? _soundMode;
    private BandEqualizerSpec? _eqRange;

    /// <summary><c>0x1000</c> → <c>[bands, min i8, max i8, …]</c>, gains in 0.1 dB; trailing bytes are not interpreted.</summary>
    private static bool ValidEqConfig(byte[] p) => p.Length >= 3 && p[0] is >= 1 and <= 16 && (sbyte)p[1] < 0 && (sbyte)p[2] > 0;

    private async Task RefreshEqualizerAsync(CancellationToken ct)
    {
        // Firmware 2.x rejects the user-EQ queries or answers them with a different (unverified) EQ generation.
        if (!Has(SennheiserTraits.Equalizer) || _firmwareMajor is not >= 3)
        {
            _live = _live with { Equalizer = null };
            return;
        }
        var config = await Query(0x1000, [], ValidEqConfig, ct).ConfigureAwait(false);
        bool ready = false;
        if (config is not null)
        {
            int bands = config.Payload[0];
            if (_eqFrequencies.Length != bands)
            {
                // Band frequencies are fixed per model (M4 90…6500 Hz, M5 eight bands); read once per session.
                var frequencies = new int[bands];
                int read = 0;
                for (; read < bands; read++)
                {
                    byte band = (byte)read;
                    var reply = await Query(0x100B, [band], p => p.Length == 3 && p[0] == band, ct).ConfigureAwait(false);
                    if (reply is null) break;
                    frequencies[read] = BinaryPrimitives.ReadUInt16BigEndian(reply.Payload.AsSpan(1));
                }
                _eqFrequencies = read == bands ? frequencies : [];
                _eqGains = null;
            }
            _eqRange = new((sbyte)config.Payload[1], (sbyte)config.Payload[2]);
            // 0x1003 needs one argument byte; an empty request is rejected.
            bool gains = _eqFrequencies.Length == bands && await Query(0x1003, [0], p => p.Length == bands, ct).ConfigureAwait(false) is not null;
            bool mode = gains && await Query(0x0804, [], p => p is [0, _], ct).ConfigureAwait(false) is not null;
            ready = gains && mode;
        }
        _live = _live with { Equalizer = ready ? _eqRange : null };
        if (ready) PublishEqualizer(StateSource.QueryResponse);
    }

    private bool HandleEqualizerPacket(BrandPacket packet, ushort command)
    {
        if (!Has(SennheiserTraits.Equalizer)) return false;
        byte[] p = packet.Payload;
        var source = packet.Type == 1 ? StateSource.DeviceNotification : StateSource.QueryResponse;
        // The whole curve: GET 0x1003 response, or notification 0x1082 after any band change.
        if ((command == 0x1003 && packet.Type == 2 || command == 0x1002 && packet.Type == 1) && p.Length == _eqFrequencies.Length && p.Length > 0)
            _eqGains = [.. p.Select(b => (int)(sbyte)b)];
        else if (command == 0x0804 && p is [0, _]) _soundMode = p[1];
        else return false;
        PublishEqualizer(source);
        return true;
    }

    private void PublishEqualizer(StateSource source)
    {
        if (_eqGains is not { } gains || _soundMode is not byte mode || _eqRange is not { } range) return;
        // Unknown modes (personalisation, parametric EQ, …) are shown by raw value and never rewritten.
        string? preset = mode is 0 || mode == 2 && Has(SennheiserTraits.Podcast) ? SennheiserProfile.SoundModePreset(mode) : null;
        var curve = new CustomEqualizer(UserCurveId, string.Empty, Math.Min(range.MinGain, gains.Min()), Math.Max(range.MaxGain, gains.Max()),
            mode == CurveMode, [.. _eqFrequencies.Select((hz, i) => new EqualizerBand(hz, gains[i]))]);
        Store.TryUpdate(DeviceId, s => s with
        {
            Equalizer = Observed.From(new EqualizerState(preset, mode), Now, source),
            CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>([curve], Now, source),
        });
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        int mode = profile.WithLive(_live).EqualizerPresets.Any(p => p.Id == presetId) ? presetId[^1] - '0' : -1;
        return mode < 0 ? Task.FromResult(NotSupported("Sound mode")) : SetSoundModeAsync(PendingKeys.Equalizer, (byte)mode, cancellationToken);
    }

    public override Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) =>
        _live.Equalizer is null || id != UserCurveId ? Task.FromResult(NotSupported("Custom EQ")) : SetSoundModeAsync(PendingKeys.CustomEqualizer, CurveMode, cancellationToken);

    private Task<CommandResult> SetSoundModeAsync(string pendingKey, byte mode, CancellationToken cancellationToken) =>
        RunCommandAsync(pendingKey, Operations, async ct =>
        {
            var result = await SetValueAsync(0x0803, [0, mode], ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            // The mode change is applied asynchronously; poll the readback briefly before deciding.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(250), Context.Time, ct).ConfigureAwait(false);
                var read = await Query(0x0804, [], p => p is [0, _], ct).ConfigureAwait(false);
                if (read is null) return TimedOut;
                if (read.Payload[1] == mode) return CommandResult.Ok;
            }
            return new(CommandStatus.Rejected, "Sound-mode readback did not match.");
        }, cancellationToken);

    public override async Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default)
    {
        if (_live.Equalizer is not { } range || preset.Id != UserCurveId) return NotSupported("Custom EQ");
        return await RunCommandAsync(PendingKeys.CustomEqualizer, Operations, async cancellationToken =>
        {
            if (_eqGains is not { } current || preset.Bands.Count != current.Length
                || preset.Bands.Select(b => b.FrequencyHz).Where((hz, i) => hz != _eqFrequencies[i]).Any())
                return new(CommandStatus.Rejected, "The device EQ layout changed; nothing written.");
            // Values the device already holds may lie outside the reported range; they are kept, never clamped.
            if (preset.Bands.Where((b, i) => b.Gain != current[i] && (b.Gain < range.MinGain || b.Gain > range.MaxGain)).Any())
                return new(CommandStatus.Rejected, "EQ gain is outside the device range.");
            var result = CommandResult.Ok;
            for (int band = 0; band < current.Length && result.IsSuccess; band++)
                if (preset.Bands[band].Gain != current[band])
                    result = await SetValueAsync(0x1001, [(byte)band, unchecked((byte)(sbyte)preset.Bands[band].Gain)], cancellationToken).ConfigureAwait(false);
            var read = await Query(0x1003, [0], p => p.Length == current.Length, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            if (read is null) return TimedOut;
            return read.Payload.Select(b => (int)(sbyte)b).SequenceEqual(preset.Bands.Select(b => b.Gain))
                ? CommandResult.Ok : new(CommandStatus.Rejected, "EQ readback did not match.");
        }, cancellationToken).ConfigureAwait(false);
    }
}
