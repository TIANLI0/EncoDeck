// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Message ids and handshake from Budsy (MIT) and GalaxyBudsClient (GPL-3.0), as attributed by HeyBuds.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Samsung;

/// <summary>
/// Galaxy Buds controller over the modern SPP protocol: battery, noise control, presets, the
/// Buds4 Pro ANC level and the Buds2 / Buds2 Pro touch lock. Every setting is confirmed only by a
/// fresh extended status; generic ACKs, set echoes and notifications never confirm a write.
/// </summary>
internal sealed class SamsungDeviceController(IDuplexByteChannel channel, ControllerContext context, SamsungProfile profile)
    : RequestReplyController(channel, context)
{
    internal const byte Ack = 0x51, StatusUpdated = 0x60, ExtendedStatus = 0x61, NoiseChanged = 0x77, SetNoise = 0x78;
    internal const byte SetAncLevel = 0x83, SetEqualizer = 0x86, HostHello = 0x88, LockTouch = 0x90;
    private static readonly TimeSpan ReadbackDelay = TimeSpan.FromMilliseconds(200);
    private const int ReadbackAttempts = 6;
    private readonly SamsungFrameDecoder _decoder = new();
    private SamsungProfile _profile = profile;
    private SamsungModel Model => _profile.Model;

    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) =>
        [.. _decoder.Feed(bytes).Select(f => new BrandPacket(f.Command, f.Response ? (byte)1 : (byte)0, f.Payload))];

    protected override void HandlePacket(BrandPacket packet)
    {
        switch (packet.Command)
        {
            case ExtendedStatus:
                // Every extended status is acknowledged (reference host behaviour in Budsy / HeyBuds).
                WriteAsync(SamsungFrameCodec.Encode(Ack, [ExtendedStatus, 0]), CancellationToken.None).GetAwaiter().GetResult();
                if (SamsungProfiles.ParseStatus(Model, packet.Payload) is { } status) Apply(status, StateSource.DeviceNotification);
                break;
            case StatusUpdated when packet.Payload.Length >= 7:
                // Short battery update: charging flags are not part of it.
                Store.TryUpdate(DeviceId, s => s with
                {
                    Battery = new BatteryStatus(Reading(packet.Payload[1], null), Reading(packet.Payload[2], null), Reading(packet.Payload[6], null)),
                    LastResponseAt = Now,
                    LastUpdateSource = StateSource.DeviceNotification,
                });
                break;
            case NoiseChanged when packet.Payload is [var value] && value <= (Model == SamsungModel.Buds4Pro ? 3 : 2) && _profile.StatusConfirmed:
                var mode = value switch { 0 => NoiseControlMode.Off, 1 => NoiseControlMode.NoiseCancellation, 2 => NoiseControlMode.Transparency, _ => NoiseControlMode.Adaptive };
                Store.TryUpdate(DeviceId, s =>
                {
                    // Keep the ANC level last read with the status; this event carries the mode only.
                    string? level = mode == NoiseControlMode.NoiseCancellation && s.NoiseControl?.Value.Mode == mode ? s.NoiseControl.Value.LevelId : null;
                    return s with { NoiseControl = Observed.From(new NoiseControlState(mode, level), Now, StateSource.DeviceNotification), LastResponseAt = Now };
                });
                break;
        }
    }

    private Observed<BatteryReading>? Reading(byte level, bool? charging) =>
        level <= 100 ? Observed.From(new BatteryReading(level, charging), Now, StateSource.QueryResponse) : null;

    private void Apply(SamsungStatus status, StateSource source)
    {
        _profile = _profile.WithStatus(status);
        var noise = new NoiseControlState(status.Noise,
            status.Noise == NoiseControlMode.NoiseCancellation && status.AncLevel is { } level ? $"level{level + 1}" : null);
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = _profile,
            Battery = new BatteryStatus(Reading(status.Left, status.Charging?.Left), Reading(status.Right, status.Charging?.Right), Reading(status.Case, status.Charging?.Case)),
            NoiseControl = Observed.From(noise, Now, source),
            Equalizer = status.Equalizer is { } eq ? Observed.From(new EqualizerState(eq.ToString(), eq), Now, source) : null,
            Toggles = Model == SamsungModel.Buds4Pro ? s.Toggles : s.Toggles.SetItem(DeviceToggle.TouchControls, Observed.From(!status.TouchLocked, Now, source)),
            LastResponseAt = Now,
            LastUpdateSource = source,
        });
    }

    /// <summary>A malformed fresh status withdraws every control it would have confirmed.</summary>
    private void Withdraw()
    {
        _profile = _profile.WithStatus(null);
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = _profile,
            NoiseControl = null,
            Equalizer = null,
            Toggles = s.Toggles.Remove(DeviceToggle.TouchControls),
        });
    }

    private enum ReadResult { Valid, Malformed, TimedOut }

    /// <summary>Requests a fresh extended status. Without request ids a timeout retires the session.</summary>
    private async Task<(ReadResult Result, SamsungStatus? Status)> ReadStatusAsync(CancellationToken ct)
    {
        var packet = await ExchangeAsync(SamsungFrameCodec.Encode(ExtendedStatus, []), p => p.Command == ExtendedStatus, ct).ConfigureAwait(false);
        if (packet is null)
        {
            Logger.LogInformation("Galaxy Buds status request timed out; ending the session");
            EndSession();
            return (ReadResult.TimedOut, null);
        }
        if (SamsungProfiles.ParseStatus(Model, packet.Payload) is not { } status)
        {
            Withdraw();
            return (ReadResult.Malformed, null);
        }
        Apply(status, StateSource.QueryResponse);
        return (ReadResult.Valid, status);
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        // Known host handshake only; no time, firmware, account or serial-number operations.
        if (!await WriteAsync(SamsungFrameCodec.Encode(Ack, [0]), cancellationToken).ConfigureAwait(false)) return false;
        if (!await WriteAsync(SamsungFrameCodec.Encode(HostHello, [1, 1, 0, 1]), cancellationToken).ConfigureAwait(false)) return false;
        var (result, _) = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (result != ReadResult.Valid)
        {
            Logger.LogInformation("Galaxy Buds did not return a valid extended status");
            return false;
        }
        StartPolling();
        return true;
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => RunExclusiveAsync(Operations, async ct =>
    {
        var (result, _) = await ReadStatusAsync(ct).ConfigureAwait(false);
        return result switch
        {
            ReadResult.Valid => CommandResult.Ok,
            ReadResult.Malformed => Malformed,
            _ => TimedOut,
        };
    }, cancellationToken);

    private static CommandResult Malformed => new(CommandStatus.Rejected, "Galaxy Buds returned a malformed extended status.");

    protected override bool CanRunCommands => base.CanRunCommands && Store.Current.Device?.DeviceId == DeviceId;

    /// <summary>Sends the writes, then polls fresh status until it shows the requested setting.</summary>
    private async Task<CommandResult> WriteAndConfirm(IReadOnlyList<byte[]> writes, Func<SamsungStatus, bool> confirmed, CancellationToken ct)
    {
        foreach (var frame in writes)
            if (!await WriteAsync(frame, ct).ConfigureAwait(false)) return new(CommandStatus.TransportError);
        for (int attempt = 0; attempt < ReadbackAttempts; attempt++)
        {
            var (result, status) = await ReadStatusAsync(ct).ConfigureAwait(false);
            if (result == ReadResult.TimedOut) return TimedOut;
            if (result == ReadResult.Malformed) return Malformed;
            if (confirmed(status!)) return CommandResult.Ok;
            if (attempt < ReadbackAttempts - 1) await Task.Delay(ReadbackDelay, Context.Time, ct).ConfigureAwait(false);
        }
        return new(CommandStatus.Rejected, "The earbuds did not confirm the requested setting.");
    }

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_profile.NoiseControlOptions.Any(o => o.Mode == target.Mode && (target.LevelId is null || o.LevelId == target.LevelId))
            || SamsungProfiles.NoiseByte(Model, target.Mode) is not { } mode)
            return Task.FromResult(NotSupported("Noise control"));
        byte? level = target.LevelId is { } id ? (byte)(int.Parse(id.AsSpan("level".Length)) - 1) : null;
        return RunCommandAsync(PendingKeys.NoiseControl, Operations, ct =>
        {
            var current = Store.Current.NoiseControl?.Value;
            List<byte[]> writes = [];
            if (level is null || current?.Mode != target.Mode) writes.Add(SamsungFrameCodec.Encode(SetNoise, [mode]));
            if (level is { } value) writes.Add(SamsungFrameCodec.Encode(SetAncLevel, [value]));
            return WriteAndConfirm(writes, s => s.Noise == target.Mode && (level is null || s.AncLevel == level), ct);
        }, cancellationToken);
    }

    public override Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!_profile.EqualizerPresets.Any(p => p.Id == presetId) || !byte.TryParse(presetId, out byte value) || value > 5)
            return Task.FromResult(NotSupported("EQ preset has not been confirmed by this device."));
        return RunCommandAsync(PendingKeys.Equalizer, Operations, ct => WriteAndConfirm([SamsungFrameCodec.Encode(SetEqualizer, [value])], s => s.Equalizer == value, ct), cancellationToken);
    }

    public override Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken)
    {
        // The shared toggle is "touch controls on"; the earbuds store the inverse (touch locked).
        if (toggle != DeviceToggle.TouchControls || !_profile.Toggles.Contains(toggle)) return Task.FromResult(NotSupported(toggle.ToString()));
        bool locked = !enabled;
        return RunCommandAsync(PendingKeys.Toggle(toggle), Operations, async ct =>
        {
            // Build the payload from fresh flags so gestures changed on the phone are preserved.
            var (result, fresh) = await ReadStatusAsync(ct).ConfigureAwait(false);
            if (result == ReadResult.TimedOut) return TimedOut;
            if (fresh is null || SamsungProfiles.TouchLockPayload(Model, locked, fresh) is not { } payload) return Malformed;
            return await WriteAndConfirm([SamsungFrameCodec.Encode(LockTouch, payload)], s => s.TouchLocked == locked, ct).ConfigureAwait(false);
        }, cancellationToken);
    }
}
