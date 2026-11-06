// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Probe;

/// <summary>
/// Diagnostic: probes a Sony MDR RFCOMM channel that Windows' SDP reports without a channel number.
/// Sends only the read-only protocol-version request (command 0x00, subtype 0x00) and prints replies.
/// </summary>
internal static class SonyProbeCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        var resolved = await ProbeSession.ResolveAsync(options, ct);
        if (resolved is null) return 3;
        var (picked, profile) = resolved.Value;
        if (profile is not (null or SonyAkgProfile))
        {
            Console.Error.WriteLine($"{picked.Name} is model '{profile.ModelId}', not a Sony MDR model; the Sony probe frame is not sent to it.");
            return 2;
        }

        int channel = options.GetInt("channel", 9);
        using var device = await BluetoothDevice.FromIdAsync(picked.DeviceId).AsTask(ct);
        if (device is null) { Console.Error.WriteLine("Device not found."); return 3; }
        Console.WriteLine($"Device: {device.Name}; classic link {device.ConnectionStatus}; probing RFCOMM channel {channel}");

        var opened = await BluetoothSocketByteChannel.OpenAsync(device.BluetoothAddress, (uint)channel, ct, "Sony MDR").ConfigureAwait(false);
        if (opened.Status != ChannelOpenStatus.Opened)
        {
            Console.WriteLine($"connect failed: {opened.Status} {opened.Detail}");
            return 4;
        }
        Console.WriteLine("RFCOMM connected.");
        await using var link = opened.Channel!;

        var codec = new SonyFrameCodec();
        byte[] probe = SonyFrameCodec.Encode(0x0c, 0x00, [0x00, 0x00]);
        Console.WriteLine($"TX {Convert.ToHexString(probe)}");
        await link.WriteAsync(probe, ct).ConfigureAwait(false);

        byte[] buffer = new byte[512];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.GetInt("seconds", 5)));
        while (true)
        {
            int read;
            try { read = await link.ReadAsync(buffer, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (read <= 0) break;
            Console.WriteLine($"RX raw {Convert.ToHexString(buffer.AsSpan(0, read))}");
            foreach (var frame in codec.Feed(buffer.AsSpan(0, read)))
            {
                Console.WriteLine($"  frame type=0x{frame.Type:X2} seq={frame.Sequence} payload={Convert.ToHexString(frame.Payload)}");
                if (frame.Type == 0x01)
                {
                    Console.WriteLine($"  => MDR ACK on channel {channel}; channel is usable");
                    return 0;
                }
            }
        }
        Console.WriteLine("No ACK within the timeout.");
        return 5;
    }
}
