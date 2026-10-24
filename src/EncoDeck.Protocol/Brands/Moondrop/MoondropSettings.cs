// SPDX-License-Identifier: GPL-3.0-only
// Settings adapted from MiuixMoondrop Gaia.kt, MoondropModels.kt and MoondropLink.kt.
using System.Collections.Immutable;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Moondrop;

public sealed partial class MoondropDeviceController
{
    private static readonly GestureAction[] GestureSlots =
        [GestureAction.SingleSqueeze, GestureAction.DoubleSqueeze, GestureAction.TripleSqueeze, GestureAction.SqueezeAndHold, GestureAction.LongHold];
    private static readonly (int Feature, int Command, DeviceCapability Capability)[] SettingQueries =
        [(15, 1, DeviceCapability.Gain), (22, 2, DeviceCapability.TouchCustomization)];

    private bool CanWrite(DeviceCapability capability, int feature) => _profile.HasFeature(feature) &&
        _profile.Capabilities.IsAvailable(capability, Context.AllowExperimental());

    private static bool ValidVoice(byte[] payload) => payload.Length == 3 && payload[0] <= 1 && payload[1] <= 100;

    private Task<CommandResult> WriteVoiceAsync(bool? enabled, int? volume, CancellationToken cancellationToken) =>
        RunWriteAsync(volume.HasValue ? "prompt-volume" : "prompt-tone", async ct =>
        {
            var current = await RequestAsync(14, 1, [], ct).ConfigureAwait(false);
            if (current is null || !ValidVoice(current.Payload)) return new(CommandStatus.Timeout, "Complete VoiceConf unavailable; nothing written.");
            byte[] target = [(byte)(enabled.HasValue ? enabled.Value ? 1 : 0 : current.Payload[0]), (byte)(volume ?? current.Payload[1]), current.Payload[2]];
            await RequestAsync(14, 2, target, ct).ConfigureAwait(false);
            var actual = await RequestAsync(14, 1, [], ct).ConfigureAwait(false);
            if (actual is null || !Apply(actual, StateSource.QueryResponse)) return new(CommandStatus.Timeout);
            return actual.Payload.AsSpan().SequenceEqual(target) ? CommandResult.Ok
                : new(CommandStatus.Rejected, "Device reports a different VoiceConf.");
        }, cancellationToken);

    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default)
    {
        if (!CanWrite(DeviceCapability.SpatialAudio, 18) || mode == SpatialAudioMode.HeadTracking && !CanWrite(DeviceCapability.HeadTracking, 18))
            return Task.FromResult(NotSupported("Spatial audio"));
        return RunWriteAsync("spatial", async ct =>
        {
            await RequestAsync(18, 2, [mode == SpatialAudioMode.Off ? (byte)0 : (byte)1], ct).ConfigureAwait(false);
            await RequestAsync(18, 4, [mode == SpatialAudioMode.HeadTracking ? (byte)1 : (byte)0], ct).ConfigureAwait(false);
            var spatial = await RequestAsync(18, 1, [], ct).ConfigureAwait(false);
            var tracking = await RequestAsync(18, 3, [], ct).ConfigureAwait(false);
            if (spatial is null || tracking is null || !Apply(spatial, StateSource.QueryResponse) || !Apply(tracking, StateSource.QueryResponse))
                return new(CommandStatus.Timeout);
            bool spatialOn = Store.Current.Toggles[DeviceToggle.SpatialAudio].Value;
            bool trackingOn = Store.Current.Toggles[DeviceToggle.HeadTracking].Value;
            var actual = !spatialOn ? SpatialAudioMode.Off : trackingOn ? SpatialAudioMode.HeadTracking : SpatialAudioMode.Fixed;
            UpdateSpatial();
            return actual == mode ? CommandResult.Ok : new(CommandStatus.Rejected, "Device reports a different spatial audio mode.");
        }, cancellationToken);
    }

    public override Task<CommandResult> SetGainAsync(int level, CancellationToken cancellationToken = default)
    {
        if (!CanWrite(DeviceCapability.Gain, 15)) return Task.FromResult(NotSupported("Gain"));
        if (_profile.EncodeGain(level) is not { } value) return Task.FromResult(new CommandResult(CommandStatus.Rejected, "Unknown gain level."));
        return RunWriteAsync("gain", async ct =>
        {
            await RequestAsync(15, 2, [value], ct).ConfigureAwait(false);
            var actual = await RequestAsync(15, 1, [], ct).ConfigureAwait(false);
            if (actual is null || !Apply(actual, StateSource.QueryResponse)) return new(CommandStatus.Timeout);
            return actual.Payload[0] == value ? CommandResult.Ok : new(CommandStatus.Rejected, "Device reports a different gain.");
        }, cancellationToken);
    }

    public override Task<CommandResult> SetKeyFunctionAsync(KeyFunction key, CancellationToken cancellationToken = default)
    {
        if (!CanWrite(DeviceCapability.TouchCustomization, 22)) return Task.FromResult(NotSupported("Gestures"));
        int slot = Array.IndexOf(GestureSlots, key.Action);
        if (key.Button != 0 || key.Side is not (EarSide.Left or EarSide.Right) || slot < 0 || key.Function is < 0 or > 7)
            return Task.FromResult(new CommandResult(CommandStatus.Unsupported, "Only known TOUCHV2 actions on an individual ear are writable; unknown nibbles are preserved."));
        if (key.Function is 4 or 5 && !Context.AllowExperimental())
            return Task.FromResult(NotSupported("Inferred volume gesture action (experimental)"));
        return RunWriteAsync("key-function", async ct =>
        {
            var current = await RequestAsync(22, 2, [], ct).ConfigureAwait(false);
            if (current is not { Payload.Length: 5 }) return new(CommandStatus.Timeout, "Complete TOUCHV2 configuration unavailable; nothing written.");
            byte[] target = (byte[])current.Payload.Clone();
            void Assign(int index, int function) => target[index] = key.Side == EarSide.Left
                ? (byte)((target[index] & 0x0F) | (function << 4)) : (byte)((target[index] & 0xF0) | function);
            Assign(slot, key.Function);
            if (key.Function != 0 && slot is 3 or 4) Assign(slot == 3 ? 4 : 3, 0);
            await RequestAsync(22, 3, target, ct).ConfigureAwait(false);
            var actual = await RequestAsync(22, 2, [], ct).ConfigureAwait(false);
            if (actual is null || !Apply(actual, StateSource.QueryResponse)) return new(CommandStatus.Timeout);
            return actual.Payload.AsSpan().SequenceEqual(target) ? CommandResult.Ok
                : new(CommandStatus.Rejected, "Device reports a different complete TOUCHV2 configuration.");
        }, cancellationToken);
    }

    private async Task RefreshSpatialAsync(CancellationToken ct)
    {
        if (!CanWrite(DeviceCapability.SpatialAudio, 18)) return;
        var spatial = await RequestAsync(18, 1, [], ct).ConfigureAwait(false);
        var tracking = await RequestAsync(18, 3, [], ct).ConfigureAwait(false);
        if (spatial is null || tracking is null || !Apply(spatial, StateSource.QueryResponse) || !Apply(tracking, StateSource.QueryResponse)) return;
        UpdateSpatial();
    }

    private async Task<bool> RefreshSettingsAsync(CancellationToken ct)
    {
        bool answered = false;
        foreach (var (feature, command, capability) in SettingQueries)
        {
            if (!CanWrite(capability, feature)) continue;
            var frame = await RequestAsync(feature, command, [], ct).ConfigureAwait(false);
            if (frame is not null) answered |= Apply(frame, StateSource.QueryResponse);
        }
        return answered;
    }

    private bool ApplySettings(GaiaFrame frame, StateSource source)
    {
        byte[] p = frame.Payload;
        if (!_profile.HasFeature(frame.Feature)) return false;
        if (frame.Feature == 15 && frame.Command == 1 && p.Length == 1 && _profile.DecodeGain(p[0]) is { } gain)
        {
            Store.TryUpdate(DeviceId, s => s with { Gain = Observed.From(gain, Now, source), LastUpdateSource = source });
            return true;
        }
        if (frame.Feature == 19 && frame.Command == 1 && p.Length == 1 && p[0] <= 1)
        {
            Store.TryUpdate(DeviceId, s => s with { Toggles = s.Toggles.SetItem(DeviceToggle.IndicatorLight, Observed.From(p[0] == 1, Now, source)), LastUpdateSource = source });
            return true;
        }
        if (frame.Feature == 22 && frame.Command == 2 && p.Length == 5)
        {
            var assignments = ImmutableArray.CreateBuilder<KeyFunction>(10);
            for (int i = 0; i < GestureSlots.Length; i++)
            {
                assignments.Add(new(EarSide.Left, 0, GestureSlots[i], p[i] >> 4));
                assignments.Add(new(EarSide.Right, 0, GestureSlots[i], p[i] & 15));
            }
            Store.TryUpdate(DeviceId, s => s with { KeyFunctions = Observed.From<IReadOnlyList<KeyFunction>>(assignments.MoveToImmutable(), Now, source), LastUpdateSource = source });
            return true;
        }
        return false;
    }

}
