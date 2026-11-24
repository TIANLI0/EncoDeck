// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// BMAP session flow from bosectl and bozo (MIT) as attributed by HeyBuds.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands.Bose;

/// <summary>
/// BMAP controller for Bose headphones: battery, firmware and switching between the fixed
/// Quiet / Aware modes. BMAP has no request ids, so a timeout or a corrupt stream ends the session;
/// only a fresh GET after the START result confirms a mode change. CNC level writes are not ported.
/// </summary>
internal sealed class BoseDeviceController(IDuplexByteChannel channel, ControllerContext context, BoseProfile profile)
    : RequestReplyController(channel, context)
{
    private static readonly byte[] OptionalErrors = [3, 4, 7];
    private readonly BoseFrameDecoder _decoder = new();
    private readonly Dictionary<byte, BoseModeConfig> _modes = [];
    private BoseProfile _profile = profile;
    private int? _modeCount;
    private byte? _currentMode;
    private bool _modeObserved;
    private bool _ready;

    /// <summary>A reply: the payload of a STATUS / RESULT, or the code of an ERROR.</summary>
    private sealed record Reply(byte[] Payload, byte? Error);

    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes)
    {
        var frames = _decoder.Feed(bytes);
        if (_decoder.Failed)
        {
            Logger.LogInformation("Bose BMAP stream is corrupt; ending the session");
            EndSession();
            return [];
        }
        return [.. frames.Select(f => new BrandPacket((ushort)((f.Block << 8) | f.Function), (byte)(f.Route | (byte)f.Operation), f.Payload))];
    }

    private static BoseFrame ToFrame(BrandPacket p) =>
        new((byte)(p.Command >> 8), (byte)p.Command, (BoseOperation)(p.Type & 0x0F), p.Payload, (byte)(p.Type & 0xF0));

    protected override void HandlePacket(BrandPacket packet)
    {
        var frame = ToFrame(packet);
        if (!_ready || frame.Route != 0 || frame.Operation != BoseOperation.Status) return;
        // Pushes may update presentation but never complete a write themselves.
        if (frame.Matches(2, 2) && BosePayload.Battery(frame.Payload) is { } battery) ApplyBattery(battery, StateSource.DeviceNotification);
        else if (frame.Matches(31, 3) && frame.Payload.Length == 1) ApplyCurrentMode(frame.Payload[0], StateSource.DeviceNotification);
    }

    protected override async Task<bool> StartSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_profile.Model == BoseModel.QuietComfort45)
            {
                // The BMAP version GET wakes the QC45 control session. Its answer is neither an
                // identity nor a setting result, so it is not awaited.
                if (!await WriteAsync(BoseFrameCodec.Encode(new(0, 1, BoseOperation.Get, [])), cancellationToken).ConfigureAwait(false)) return false;
            }
            if (await OptionalGet(0, 5, cancellationToken).ConfigureAwait(false) is { } version && BosePayload.Firmware(version) is { } firmware)
                Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From(new FirmwareInfo(firmware, null, new Dictionary<string, string>()), Now, StateSource.QueryResponse) });
            if (!await ReadBattery(cancellationToken).ConfigureAwait(false)) return false;
            if (await OptionalGet(31, 8, cancellationToken).ConfigureAwait(false) is { } favorites)
                _modeCount = BosePayload.ModeCount(favorites, _profile.Model);
            for (int index = 0; index < (_modeCount ?? 0); index++)
                await ReadMode((byte)index, optional: true, cancellationToken).ConfigureAwait(false);
            if (await OptionalGet(31, 3, cancellationToken).ConfigureAwait(false) is [var current])
            {
                _modeObserved = true;
                ApplyCurrentMode(current, StateSource.QueryResponse);
            }
            _ready = true;
            StartPolling();
            return true;
        }
        catch (BmapException ex)
        {
            Logger.LogInformation("Bose BMAP start-up failed: {Reason}", ex.Message);
            return false;
        }
    }

    public override Task<CommandResult> RefreshAsync(CancellationToken cancellationToken) => Serialized(null, async ct =>
    {
        if (!await ReadBattery(ct).ConfigureAwait(false)) return Fail("Bose returned an invalid battery report.");
        if (_modeObserved)
        {
            byte? index = await ReadCurrentMode(ct).ConfigureAwait(false);
            if (index is null) return Fail("Bose returned an invalid current mode.");
            if (index < _modeCount) await ReadMode(index.Value, optional: true, ct).ConfigureAwait(false);
        }
        return CommandResult.Ok;
    }, cancellationToken);

    public override Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        if (!_profile.NoiseConfirmed || target.LevelId is not null
            || target.Mode is not (NoiseControlMode.NoiseCancellation or NoiseControlMode.Transparency))
            return Task.FromResult(NotSupported($"Noise mode {target.Mode}/{target.LevelId}"));
        byte index = target.Mode == NoiseControlMode.NoiseCancellation ? (byte)0 : (byte)1;
        return Serialized(PendingKeys.NoiseControl, async ct =>
        {
            // Re-read the preset first: never start a mode the device no longer reports as Quiet / Aware.
            await ReadMode(index, optional: false, ct).ConfigureAwait(false);
            if (BosePayload.Noise(index, _modes) != target.Mode) return CommandResult.Unsupported("The device no longer reports this preset.");
            var result = await Request(31, 3, BoseOperation.Start, [index, 0], null, ct).ConfigureAwait(false);
            if (result.Error is { } code) return new(CommandStatus.Rejected, $"Bose rejected the mode change (error {code}).");
            for (int attempt = 0; attempt < 4; attempt++)
            {
                // Only a fresh GET after RESULT may confirm, never a push or the cached mode.
                byte? fresh = await ReadCurrentMode(ct).ConfigureAwait(false);
                if (fresh is null) return new(CommandStatus.Rejected, "Bose returned an invalid current mode.");
                if (fresh == index) return CommandResult.Ok;
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(150), Context.Time, ct).ConfigureAwait(false);
            }
            return new(CommandStatus.Rejected, "The headphones did not confirm the requested mode.");
        }, cancellationToken);
    }

    private async Task<bool> ReadBattery(CancellationToken ct)
    {
        var reply = await Request(2, 2, BoseOperation.Get, [], null, ct).ConfigureAwait(false);
        if (reply.Error is not null || BosePayload.Battery(reply.Payload) is not { } battery)
        {
            Store.TryUpdate(DeviceId, s => s with { Battery = BatteryStatus.Unknown });
            return false;
        }
        ApplyBattery(battery, StateSource.QueryResponse);
        return true;
    }

    private void ApplyBattery(IReadOnlyList<BoseBatteryComponent> components, StateSource source)
    {
        // A complete component report replaces earlier readings, including sentinel values.
        // Single-battery headphones use component 0, shown in the Left slot like other over-ears.
        var overall = components.FirstOrDefault(c => c.Id == 0)?.Percent;
        var reading = overall is { } percent ? Observed.From(new BatteryReading(percent, null), Now, source) : null;
        Store.TryUpdate(DeviceId, s => s with { Battery = BatteryStatus.Unknown with { Left = reading }, LastUpdateSource = source, LastResponseAt = Now });
    }

    private async Task<BoseModeConfig?> ReadMode(byte index, bool optional, CancellationToken ct)
    {
        if (index >= (_modeCount ?? 0)) throw new BmapException("Mode index outside the reported mode list.");
        var reply = await Request(31, 6, BoseOperation.Get, [index], index, ct).ConfigureAwait(false);
        if (reply.Error is { } code)
        {
            if (optional && OptionalErrors.Contains(code)) { _modes.Remove(index); PublishNoise(StateSource.QueryResponse); return null; }
            throw new BmapException($"Mode {index} rejected with error {code}.");
        }
        if (BosePayload.Mode(reply.Payload, _profile.Model) is not { } mode || mode.Index != index)
        {
            _modes.Remove(index);
            PublishNoise(StateSource.QueryResponse);
            if (optional) return null;
            throw new BmapException($"Mode {index} has an unknown layout.");
        }
        _modes[index] = mode;
        PublishNoise(StateSource.QueryResponse);
        return mode;
    }

    private async Task<byte?> ReadCurrentMode(CancellationToken ct)
    {
        var reply = await Request(31, 3, BoseOperation.Get, [], null, ct).ConfigureAwait(false);
        if (reply.Error is not null || reply.Payload.Length != 1)
        {
            _currentMode = null;
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = null });
            return null;
        }
        _modeObserved = true;
        ApplyCurrentMode(reply.Payload[0], StateSource.QueryResponse);
        return reply.Payload[0];
    }

    private void ApplyCurrentMode(byte index, StateSource source)
    {
        _currentMode = index;
        PublishNoise(source);
    }

    /// <summary>Publishes the current mode (unknown for user modes) and whether Quiet / Aware can be offered.</summary>
    private void PublishNoise(StateSource source)
    {
        bool confirmed = _modeObserved
            && BosePayload.Noise(0, _modes) == NoiseControlMode.NoiseCancellation
            && BosePayload.Noise(1, _modes) == NoiseControlMode.Transparency;
        _profile = _profile.WithNoiseConfirmed(confirmed);
        var mode = BosePayload.Noise(_currentMode, _modes);
        Store.TryUpdate(DeviceId, s => s with
        {
            Profile = _profile,
            NoiseControl = mode is { } m ? Observed.From(new NoiseControlState(m), Now, source) : null,
            LastUpdateSource = source,
        });
    }

    /// <summary>GET that treats "not supported / not available" errors as an absent register.</summary>
    private async Task<byte[]?> OptionalGet(byte block, byte function, CancellationToken ct)
    {
        var reply = await Request(block, function, BoseOperation.Get, [], null, ct).ConfigureAwait(false);
        if (reply.Error is { } code)
            return OptionalErrors.Contains(code) ? null : throw new BmapException($"[{block}.{function}] rejected with error {code}.");
        return reply.Payload;
    }

    /// <summary>
    /// One request. START waits for RESULT (a STATUS push is not the command result); other
    /// operators wait for STATUS. Without request ids a timeout or cancellation retires the session.
    /// </summary>
    private async Task<Reply> Request(byte block, byte function, BoseOperation op, byte[] payload, byte? index, CancellationToken ct)
    {
        BrandPacket? packet;
        try
        {
            packet = await ExchangeAsync(BoseFrameCodec.Encode(new(block, function, op, payload)), p =>
            {
                var frame = ToFrame(p);
                if (!frame.Matches(block, function)) return false;
                if (frame.Operation == BoseOperation.Error) return true;
                if (op == BoseOperation.Start) return frame.Operation == BoseOperation.Result;
                if (frame.Operation != BoseOperation.Status) return false;
                return index is null || frame.Payload.Length == 0 || frame.Payload[0] == index;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EndSession();
            throw;
        }
        if (packet is null)
        {
            EndSession();
            throw new BmapException($"No answer to [{block}.{function}].");
        }
        var reply = ToFrame(packet);
        if (reply.Operation != BoseOperation.Error) return new(reply.Payload, null);
        if (reply.Payload.Length != 1) throw new BmapException($"Malformed error for [{block}.{function}].");
        return new([], reply.Payload[0]);
    }

    private CommandResult Fail(string detail)
    {
        // A bad refresh must not leave cached state presented as current.
        EndSession();
        return new(CommandStatus.Rejected, detail);
    }

    protected override bool CanRunCommands => base.CanRunCommands && Store.Current.Device?.DeviceId == DeviceId;

    /// <summary>One command at a time; a BMAP error reply is a rejection, or a timeout once the session ended.</summary>
    private Task<CommandResult> Serialized(string? pendingKey, Func<CancellationToken, Task<CommandResult>> action, CancellationToken ct)
    {
        return pendingKey is null ? RunExclusiveAsync(Operations, Body, ct) : RunCommandAsync(pendingKey, Operations, Body, ct);

        async Task<CommandResult> Body(CancellationToken token)
        {
            try { return await action(token).ConfigureAwait(false); }
            catch (BmapException ex) { return Closed.IsCompleted ? TimedOut : new(CommandStatus.Rejected, ex.Message); }
        }
    }

    private sealed class BmapException(string message) : Exception(message);
}
