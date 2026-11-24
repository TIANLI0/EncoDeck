// SPDX-License-Identifier: GPL-3.0-only
// Paired-host list and the guarded connect flow adapted from Ariazonaa/momentum4-control
// (MIT, Copyright (c) 2026 Ariazonaa) and the verify-then-act checks of Zhengyang-Liu/m4-companion
// (MIT, Copyright (c) 2026 Zhengyang Liu); the MIT notice is reproduced in THIRD_PARTY_NOTICES.md.
// The disconnect (0x1403) and delete (0x1405 / 0x1406) commands are never sent: on some firmware
// "disconnect" erases the host's pairing.
using System.Buffers.Binary;
using System.Text;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sennheiser;

internal sealed partial class SennheiserController
{
    private sealed record PairedHost(int Index, byte Priority, bool Connected, string Name);
    private sealed record PairedSnapshot(IReadOnlyList<PairedHost> Hosts, int Own, int MaxConnections);

    private static readonly TimeSpan FindAutoStop = TimeSpan.FromSeconds(50);
    private readonly bool[] _ringing = new bool[2];
    private CancellationTokenSource? _findStop;

    /// <summary>Reads count, every entry, the controlling host's index and the connection limit; null when any answer is invalid.</summary>
    private async Task<PairedSnapshot?> ReadPairedAsync(CancellationToken ct)
    {
        var count = await Query(0x1400, [], p => p.Length == 2 && BinaryPrimitives.ReadUInt16BigEndian(p) <= 16, ct).ConfigureAwait(false);
        var own = await Query(0x1407, [], p => p.Length == 1, ct).ConfigureAwait(false);
        var max = await Query(0x1409, [], p => p is [>= 1 and <= 8], ct).ConfigureAwait(false);
        if (count is null || own is null || max is null) return null;
        var hosts = new List<PairedHost>();
        for (int i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(count.Payload); i++)
        {
            byte index = (byte)i;
            var entry = await Query(0x1401, [index], p => p.Length >= 3 && p[0] == index, ct).ConfigureAwait(false);
            if (entry is null) return null;
            byte[] p = entry.Payload;
            hosts.Add(new(index, p[1], p[2] != 0, Encoding.UTF8.GetString(p.AsSpan(3).TrimEnd((byte)0))));
        }
        return new(hosts, own.Payload[0], max.Payload[0]);
    }

    private async Task RefreshPairedDevicesAsync(CancellationToken ct)
    {
        if (!Has(SennheiserTraits.PairedDevices)) return;
        var snapshot = await ReadPairedAsync(ct).ConfigureAwait(false);
        _live = _live with { PairedDevices = snapshot is not null };
        if (snapshot is not null) PublishPaired(snapshot, StateSource.QueryResponse);
    }

    private void PublishPaired(PairedSnapshot snapshot, StateSource source)
    {
        // Entries are addressed by index only (no Bluetooth address); the index travels as a one-byte key.
        var devices = snapshot.Hosts.Select(h => new SourceDevice([(byte)h.Index], h.Name, h.Connected ? 2 : 0, h.Index == snapshot.Own, false, 0)).ToArray();
        Store.TryUpdate(DeviceId, s => s with { SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>(devices, Now, source) });
    }

    private bool HandleDevicePacket(BrandPacket packet, ushort command)
    {
        byte[] p = packet.Payload;
        // Connection-status change of one listed host: [index, 0/1].
        if (Has(SennheiserTraits.PairedDevices) && command == 0x1404 && p is [_, 0 or 1])
        {
            Store.TryUpdate(DeviceId, s => s.SourceDevices is not { } list ? s : s with
            {
                SourceDevices = Observed.From<IReadOnlyList<SourceDevice>>([.. list.Value.Select(d => d.Address is [var index] && index == p[0]
                    ? d with { State = p[1] == 1 ? 2 : 0 } : d)], Now, packet.Type == 1 ? StateSource.DeviceNotification : StateSource.QueryResponse),
            });
            return true;
        }
        // Ring state of one earbud: [ringing, side], as a notification (or a response on other firmware).
        if (Has(SennheiserTraits.Find) && command == 0x2C03 && p is [0 or 1, 0 or 1])
        {
            _ringing[p[1]] = p[0] == 1;
            Store.TryUpdate(DeviceId, s => s with { IsFindingEarbuds = _ringing[0] || _ringing[1] });
            return true;
        }
        return false;
    }

    public override async Task<CommandResult> MultiConnectAsync(MultiConnectOperation operation, byte[] address, CancellationToken cancellationToken = default)
    {
        if (operation != MultiConnectOperation.Connect || !profile.WithLive(_live).CanConnectPairedDevices || address is not [var index])
            return NotSupported("Remote disconnect or removal");
        string? expected = Store.Current.SourceDevices?.Value.FirstOrDefault(d => d.Address is [var i] && i == index)?.Name;
        return await RunCommandAsync(PendingKeys.MultiConnect, Operations, async cancellationToken =>
        {
            // Verify on a fresh list: same index and name, unique, not this PC, and a free connection slot.
            var before = await ReadPairedAsync(cancellationToken).ConfigureAwait(false);
            if (before is null) return TimedOut;
            PublishPaired(before, StateSource.QueryResponse);
            var target = before.Hosts.FirstOrDefault(h => h.Index == index);
            if (target is null || target.Name != expected || before.Hosts.Count(h => h.Name == target.Name) != 1 || target.Index == before.Own
                || before.Hosts.FirstOrDefault(h => h.Index == before.Own) is not { Connected: true })
                return new(CommandStatus.Rejected, "The paired-device list changed; nothing was sent.");
            if (target.Connected) return CommandResult.Ok;
            if (before.Hosts.Count(h => h.Connected) >= before.MaxConnections)
                return new(CommandStatus.Rejected, "All connection slots are in use; disconnect a device from its own Bluetooth settings first.");
            var result = await SetValueAsync(0x1402, [index], cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            // The ACK precedes the link; poll the host's status (about 3 s on hardware).
            bool connected = false;
            for (int attempt = 0; attempt < 27 && !connected; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), Context.Time, cancellationToken).ConfigureAwait(false);
                var status = await Query(0x1404, [index], p => p is [_, 0 or 1] && p[0] == index, cancellationToken).ConfigureAwait(false);
                connected = status?.Payload[1] == 1;
            }
            var after = await ReadPairedAsync(cancellationToken).ConfigureAwait(false);
            if (after is null) return TimedOut;
            PublishPaired(after, StateSource.QueryResponse);
            return connected && after.Hosts.Any(h => h.Index == index && h.Name == target.Name && h.Connected)
                && after.Hosts.Any(h => h.Index == after.Own && h.Connected)
                ? CommandResult.Ok : new(CommandStatus.Rejected, "The device did not report the host as connected.");
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- find earbuds (TW4) ----------------------------------------------------------------------

    /// <summary>Wire sides: 0 = right, 1 = left. The state query answers as notification 0x2C83.</summary>
    private Task<BrandPacket?> ReadRingAsync(byte side, CancellationToken ct) =>
        ExchangeAsync(SennheiserFrameCodec.Encode(0x2C03, [side]), p => p.Command == (0x2C03 | 0x180)
            || p.Command is 0x2C83 or 0x2D03 && p.Payload is [0 or 1, var s] && s == side, ct);

    private async Task RefreshFindAsync(CancellationToken ct)
    {
        if (!Has(SennheiserTraits.Find) || _live.Find) return;
        var state = await ReadRingAsync(0, ct).ConfigureAwait(false);
        _live = _live with { Find = state is { Type: 1 or 2 } };
    }

    public override async Task<CommandResult> FindEarbudsAsync(bool ring, CancellationToken cancellationToken = default)
    {
        if (!_live.Find) return NotSupported("Find earbuds");
        CommandResult result = CommandResult.Ok;
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            foreach (byte side in new byte[] { 0, 1 })
            {
                var sent = await SetValueAsync(ring ? (ushort)0x2C02 : (ushort)0x2C01, [side], cancellationToken).ConfigureAwait(false);
                if (!sent.IsSuccess) result = sent;
                // Render the reported ring state, never the request.
                if (await ReadRingAsync(side, cancellationToken).ConfigureAwait(false) is not { Type: 1 or 2 }) result = result.IsSuccess ? TimedOut : result;
            }
        }
        finally { Operations.Release(); }
        if (ring && !result.IsSuccess) await FindEarbudsAsync(false, CancellationToken.None).ConfigureAwait(false);
        bool active = ring && result.IsSuccess && (_ringing[0] || _ringing[1]);
        if (ring && result.IsSuccess && !active) result = new(CommandStatus.Rejected, "The earbuds did not report ringing.");
        _findStop?.Cancel();
        if (active)
        {
            var stop = new CancellationTokenSource();
            _findStop = stop;
            _ = Task.Delay(FindAutoStop, Context.Time, stop.Token).ContinueWith(
                t => { if (!t.IsCanceled && !Closed.IsCompleted) _ = FindEarbudsAsync(false); }, TaskScheduler.Default);
        }
        return result;
    }
}
