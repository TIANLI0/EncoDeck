// SPDX-License-Identifier: GPL-3.0-only
// Feature 5 PEQ layout and write/readback flow adapted from FEAKEuser/moondrop-control (GPL-3.0-or-later)
// and bqj6666/FxxkMoondrop MoondropEq.kt / MoondropSpp.kt (GPL-3.0), cross-checked with the Space Travel 2
// notes of herikk04/moondrop-pc (MIT, Copyright (c) 2026 Herik Assis; the MIT notice is reproduced in
// THIRD_PARTY_NOTICES.md).
using System.Buffers.Binary;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Moondrop;

/// <summary>One user PEQ band: frequency in Hz, Q ×4096, filter type, gain in 1/60 dB.</summary>
public sealed record MoondropPeqBand(int FrequencyHz, ushort Q, byte FilterType, short Gain);

/// <summary>
/// Feature 5 user curve <c>[start][end][pre-gain s16]</c> + per band <c>[freq u16][Q u16][type][gain s16]</c>, all big endian.
/// Bytes 2–3 (pre-gain on some firmware, always zero on EDGE) are preserved as read.
/// </summary>
public sealed record MoondropPeq(byte Start, byte End, short PreGain, IReadOnlyList<MoondropPeqBand> Bands)
{
    /// <summary>Gain resolution on the wire: 60 steps per dB.</summary>
    public const int GainPerDb = 60;

    public static MoondropPeq? Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < 11 || (p.Length - 4) % 7 != 0 || p[1] < p[0] || p[1] - p[0] + 1 != (p.Length - 4) / 7) return null;
        var bands = new MoondropPeqBand[(p.Length - 4) / 7];
        for (int i = 0; i < bands.Length; i++)
        {
            var b = p.Slice(4 + i * 7, 7);
            // A non-zero filter byte is a layout this client does not understand: the curve stays read-only.
            if (b[4] != 0) return null;
            bands[i] = new(BinaryPrimitives.ReadUInt16BigEndian(b), BinaryPrimitives.ReadUInt16BigEndian(b[2..]), b[4], BinaryPrimitives.ReadInt16BigEndian(b[5..]));
        }
        return new(p[0], p[1], BinaryPrimitives.ReadInt16BigEndian(p[2..]), bands);
    }

    public byte[] Encode()
    {
        var result = new byte[4 + Bands.Count * 7];
        result[0] = Start; result[1] = End;
        BinaryPrimitives.WriteInt16BigEndian(result.AsSpan(2), PreGain);
        for (int i = 0; i < Bands.Count; i++)
        {
            var b = result.AsSpan(4 + i * 7, 7);
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Bands[i].FrequencyHz);
            BinaryPrimitives.WriteUInt16BigEndian(b[2..], Bands[i].Q);
            b[4] = Bands[i].FilterType;
            BinaryPrimitives.WriteInt16BigEndian(b[5..], Bands[i].Gain);
        }
        return result;
    }

    /// <summary>Gain shown in tenths of a dB.</summary>
    public static int Tenths(short gain) => (int)Math.Round(gain / (GainPerDb / 10.0), MidpointRounding.AwayFromZero);
}

public sealed partial class MoondropDeviceController
{
    /// <summary>Preset id of the user curve in the feature 5 preset list.</summary>
    internal const byte UserCurvePreset = 0x3F;
    /// <summary>Gain limits of the community editors (−12 … +3 dB), widened to whatever the device already holds.</summary>
    private const int MinCurveGain = -120, MaxCurveGain = 30;
    private IReadOnlyList<byte>? _eqPresets;
    private MoondropPeq? _peq;
    private byte? _activePreset;
    private int _peqBands;

    /// <summary>Reads the preset list, the active preset and (single-packet layouts only) the user curve.</summary>
    private async Task<bool> RefreshEqualizerAsync(CancellationToken ct)
    {
        if (!_profile.HasFeature(5)) return false;
        bool answered = false;
        if (_eqPresets is null && await RequestAsync(5, 1, [], ct).ConfigureAwait(false) is { } list && ValidPresetList(list.Payload))
            _eqPresets = list.Payload[1..];
        if (_eqPresets is null) return false;
        if (await RequestAsync(5, 2, [], ct).ConfigureAwait(false) is { Payload: [var active] })
        {
            _activePreset = active;
            answered = true;
        }
        if (_eqPresets.Contains(UserCurvePreset))
        {
            if (_peqBands == 0 && await RequestAsync(5, 4, [], ct).ConfigureAwait(false) is { Payload: [>= 1 and <= 7 and var count] }) _peqBands = count;
            // One request covers at most seven bands; longer curves (and their split reads) are not supported.
            if (_peqBands > 0) answered |= await ReadCurveAsync(ct).ConfigureAwait(false) is not null;
        }
        PublishEqualizer(StateSource.QueryResponse);
        return answered;
    }

    private static bool ValidPresetList(byte[] p) => p.Length >= 2 && p[0] == p.Length - 1 && p[1..].All(id => id <= 31 || id == UserCurvePreset)
        && p[1..].Distinct().Count() == p.Length - 1;

    private async Task<MoondropPeq?> ReadCurveAsync(CancellationToken ct)
    {
        var frame = await RequestAsync(5, 5, [0, (byte)(_peqBands - 1)], ct).ConfigureAwait(false);
        var peq = frame is null ? null : MoondropPeq.Decode(frame.Payload);
        if (peq is null || peq.Start != 0 || peq.Bands.Count != _peqBands) return null;
        _peq = peq;
        return peq;
    }

    private void PublishEqualizer(StateSource source)
    {
        if (_eqPresets is null) return;
        var factory = _eqPresets.Where(id => id != UserCurvePreset).ToArray();
        int[] gains = _peq is null ? [] : [.. _peq.Bands.Select(b => MoondropPeq.Tenths(b.Gain))];
        var spec = _peq is null ? null : new BandEqualizerSpec(Math.Min(MinCurveGain, gains.Min()), Math.Max(MaxCurveGain, gains.Max()));
        _profile = _profile.WithEqualizer(new(factory, _eqPresets.Contains(UserCurvePreset), spec));
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = _profile,
            // Unknown active ids (not in the list) are shown by raw value only.
            Equalizer = _activePreset is { } active ? Observed.From(new EqualizerState(factory.Contains(active) ? MoondropProfile.FactoryPresetId(active) : null, active), Now, source) : s.Equalizer,
            CustomEqualizers = spec is null || _peq is null ? s.CustomEqualizers : Observed.From<IReadOnlyList<CustomEqualizer>>(
                [new(UserCurvePreset, string.Empty, spec.MinGain, spec.MaxGain, _activePreset == UserCurvePreset,
                    [.. _peq.Bands.Select((b, i) => new EqualizerBand(b.FrequencyHz, gains[i]))])], Now, source),
            LastUpdateSource = source,
        });
    }

    private bool CanUseEqualizer(DeviceCapability capability) => _profile.HasFeature(5) && _eqPresets is not null
        && _profile.Capabilities.IsAvailable(capability, Context.AllowExperimental());

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        // Nullable: a missing id must not fall back to byte 0 (a real factory preset).
        byte? preset = _eqPresets?.Where(id => id != UserCurvePreset && MoondropProfile.FactoryPresetId(id) == presetId)
            .Select(id => (byte?)id).FirstOrDefault();
        if (!CanUseEqualizer(DeviceCapability.Equalizer) || preset is not { } id) return Task.FromResult(NotSupported("EQ preset"));
        return RunWriteAsync(PendingKeys.Equalizer, ct => SelectPresetAsync(id, ct), cancellationToken);
    }

    public override Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken cancellationToken = default) =>
        id != UserCurvePreset || !CanUseEqualizer(DeviceCapability.CustomEqualizer) || _profile.BandEqualizer is null
            ? Task.FromResult(NotSupported("Custom EQ")) : RunWriteAsync(PendingKeys.CustomEqualizer, ct => SelectPresetAsync(UserCurvePreset, ct), cancellationToken);

    private async Task<CommandResult> SelectPresetAsync(byte id, CancellationToken ct)
    {
        // Some firmware acknowledges the selection, some does not; only the active-preset readback decides.
        await RequestAsync(5, 3, [id], ct).ConfigureAwait(false);
        var actual = await RequestAsync(5, 2, [], ct).ConfigureAwait(false);
        if (actual is not { Payload: [var active] }) return new(CommandStatus.Timeout);
        _activePreset = active;
        PublishEqualizer(StateSource.QueryResponse);
        return active == id ? CommandResult.Ok : new(CommandStatus.Rejected, "Device reports a different EQ preset.");
    }

    public override Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken cancellationToken = default)
    {
        if (preset.Id != UserCurvePreset || !CanUseEqualizer(DeviceCapability.CustomEqualizer) || _profile.BandEqualizer is not { } range)
            return Task.FromResult(NotSupported("Custom EQ"));
        return RunWriteAsync(PendingKeys.CustomEqualizer, async ct =>
        {
            // Always rebuild from a fresh read: frequency, Q, filter and pre-gain bytes are written back unchanged.
            var current = await ReadCurveAsync(ct).ConfigureAwait(false);
            if (current is null) return new(CommandStatus.Timeout, "Complete PEQ curve unavailable; nothing written.");
            if (preset.Bands.Count != current.Bands.Count || preset.Bands.Where((b, i) => b.FrequencyHz != current.Bands[i].FrequencyHz).Any())
            {
                PublishEqualizer(StateSource.QueryResponse);
                return new(CommandStatus.Rejected, "The device PEQ layout changed; nothing written.");
            }
            var bands = current.Bands.Select((b, i) =>
            {
                int gain = preset.Bands[i].Gain;
                // Unchanged bands keep their exact raw value; changed ones are written in 0.1 dB steps.
                return gain == MoondropPeq.Tenths(b.Gain) ? b : b with { Gain = (short)(gain * (MoondropPeq.GainPerDb / 10)) };
            }).ToArray();
            if (bands.Where((b, i) => b != current.Bands[i] && (preset.Bands[i].Gain < range.MinGain || preset.Bands[i].Gain > range.MaxGain)).Any())
                return new(CommandStatus.Rejected, "PEQ gain is outside the supported range.");
            var target = current with { Bands = bands };
            byte[] payload = target.Encode();
            MoondropPeq? actual = null;
            // SET has no acknowledgement and a write is occasionally dropped: write, read back, retry once.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await RequestAsync(5, 6, payload, ct).ConfigureAwait(false);
                actual = await ReadCurveAsync(ct).ConfigureAwait(false);
                if (actual is not null && actual.Encode().AsSpan().SequenceEqual(payload)) break;
            }
            PublishEqualizer(StateSource.QueryResponse);
            if (actual is null) return new(CommandStatus.Timeout);
            return actual.Encode().AsSpan().SequenceEqual(payload) ? CommandResult.Ok : new(CommandStatus.Rejected, "Device reports a different PEQ curve.");
        }, cancellationToken);
    }
}
