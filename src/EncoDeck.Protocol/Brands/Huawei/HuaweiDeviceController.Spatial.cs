// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Huawei;

internal sealed partial class HuaweiDeviceController
{
    // Feature 24 (independent spatial audio): TLV 2 = mode, 3 = scene.
    private static readonly byte[] SpatialQuery = [1, 1, HuaweiCommands.FeatureSpatialAudio, 2, 0, 3, 0];
    private readonly SemaphoreSlim _operations = new(1, 1);

    /// <summary>Strict TLV parse of a feature report for <paramref name="feature"/>; result codes and other features yield null.</summary>
    internal static Dictionary<byte, byte[]>? ParseFeature(ReadOnlySpan<byte> payload, byte feature)
    {
        var fields = new Dictionary<byte, byte[]>();
        while (!payload.IsEmpty)
        {
            if (payload.Length < 2 || payload.Length < 2 + payload[1]) return null;
            int length = payload[1];
            if (!fields.TryAdd(payload[0], payload.Slice(2, length).ToArray())) return null;
            payload = payload[(length + 2)..];
        }
        if (fields.ContainsKey(HuaweiCommands.ErrorTlv) || !fields.TryGetValue(1, out var id) || id.Length != 1 || id[0] != feature) return null;
        return fields;
    }

    internal static SpatialAudioMode? ParseSpatial(ReadOnlySpan<byte> payload)
    {
        if (ParseFeature(payload, HuaweiCommands.FeatureSpatialAudio) is not { } fields
            || !fields.TryGetValue(2, out var state) || state.Length != 1) return null;
        return state[0] switch { 0 => SpatialAudioMode.Off, 1 => SpatialAudioMode.HeadTracking, 2 => SpatialAudioMode.Fixed, _ => null };
    }

    private void ApplySpatial(byte[] payload, StateSource source)
    {
        if (ParseSpatial(payload) is not { } mode) return;
        Profile = Profile.WithSpatial();
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = Profile, Spatial = Observed.From(mode, Now, source), LastUpdateSource = source,
        });
    }

    private async Task<CommandResult> SerializedAsync(Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct)
    {
        bool entered = false;
        try
        {
            await _operations.WaitAsync(ct).ConfigureAwait(false); entered = true;
            return Closed.IsCompleted ? CommandResult.NotConnected : await action(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(CommandStatus.Cancelled); }
        finally { if (entered) _operations.Release(); }
    }

    public override Task<CommandResult> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken = default) => SerializedAsync(async ct =>
    {
        if (!Profile.SpatialConfirmed) return NotSupported("Independent spatial audio has not been reported by this device.");
        byte value;
        switch (mode)
        {
            case SpatialAudioMode.Off: value = 0; break;
            case SpatialAudioMode.HeadTracking: value = 1; break;
            case SpatialAudioMode.Fixed: value = 2; break;
            default: return NotSupported("Spatial mode");
        }
        if (!await WriteAsync(_codec.Encode(HuaweiCommands.FeatureState, FeatureWrite(HuaweiCommands.FeatureSpatialAudio, 2, value)), ct).ConfigureAwait(false))
            return new CommandResult(CommandStatus.TransportError);
        await Task.Delay(ReadBackDelay, Context.Time, ct).ConfigureAwait(false);
        var (frame, status) = await QueryAsync(HuaweiCommands.FeatureState, SpatialQuery, ct).ConfigureAwait(false);
        if (Store.Current.Device?.DeviceId != DeviceId) return CommandResult.NotConnected;
        var actual = frame is { } response ? ParseSpatial(response.Payload) : null;
        var result = status != CommandStatus.Success ? new CommandResult(status)
            : actual == mode ? CommandResult.Ok : new CommandResult(CommandStatus.Rejected, "The device did not confirm the requested spatial mode.");
        // Whatever the earbuds read back is the current state (a mismatch rolls the buttons back).
        if (actual is not null) ApplySpatial(frame!.Value.Payload, result.IsSuccess ? StateSource.CommandConfirmation : StateSource.QueryResponse);
        if (result.Status == CommandStatus.Rejected)
        {
            // Not confirmed or refused: hide this mode for the rest of the connection; the others stay usable.
            Logger.LogInformation("Spatial mode {Mode} was not confirmed; hiding it for this connection", mode);
            Profile = Profile.WithFailedSpatial(mode);
            Store.TryUpdate(DeviceId, s => s with { Profile = Profile });
        }
        Store.TryUpdate(DeviceId, s => s with { LastError = result.IsSuccess ? null : new DeviceError(result.Detail ?? result.Status.ToString(), result.Status, Now) });
        return result;
    }, cancellationToken);
}
