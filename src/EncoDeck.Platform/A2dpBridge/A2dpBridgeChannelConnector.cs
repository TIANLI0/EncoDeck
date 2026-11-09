// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>
/// Opens the control channel through BlueCaster: an RFCOMM channel from the bridge's own adapter to the
/// earbuds it streams to, for when Windows has no link to them. The services are tried in order; the core finds
/// each one with its own SDP query.
/// </summary>
/// <param name="monitor">The current monitor (the companion that owns it can be replaced).</param>
public sealed class A2dpBridgeChannelConnector(Func<A2dpBridgeMonitor> monitor, ILogger? logger = null) : IChannelConnector
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
    {
        var bridge = monitor();
        // Only to the device being streamed to: the caller matched it to deviceId by address.
        if (bridge.Status is not { IsConnected: true, Rfcomm: true, Snapshot.ActiveStreamProfile: { } profile })
            return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "BlueCaster is not streaming to the earbuds.");

        ChannelOpenResult? failure = null;
        foreach (var uuid in serviceUuids)
        {
            try
            {
                var channel = await bridge.OpenRfcommAsync(profile.DeviceAddress, uuid, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("RFCOMM through BlueCaster opened: {Service}, channel {Channel}, MTU {Mtu}", uuid, channel.Id, channel.Mtu);
                return new ChannelOpenResult(ChannelOpenStatus.Opened, channel, uuid, null);
            }
            catch (BridgeRequestException ex) when (ex.Code == "rfcomm.service_not_found")
            {
                _logger.LogInformation("RFCOMM through BlueCaster: no {Service} in the earbuds' SDP records", uuid);
                failure ??= ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "BlueCaster found no control service.");
            }
            catch (BridgeRequestException ex) when (ex.Code == "rfcomm.not_connected")
            {
                // The A2DP connection is not complete yet, or just ended: the stream state follows.
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "BlueCaster is not connected to the earbuds.");
            }
            catch (BridgeRequestException ex)
            {
                // rfcomm.open_failed (refused, or out of time), rfcomm.busy (too many channels or opens).
                string detail = $"BlueCaster {ex.Code} {string.Join(' ', ex.BridgeMessage.Args)}".TrimEnd();
                _logger.LogInformation("RFCOMM through BlueCaster to {Service} failed: {Detail}", uuid, detail);
                failure = ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, detail);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or NotSupportedException)
            {
                // The core went away, did not answer, or is too old for RFCOMM.
                _logger.LogInformation("RFCOMM through BlueCaster unavailable: {Error}", ex.Message);
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, ex.Message);
            }
        }
        return failure ?? ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "No RFCOMM control service to open.");
    }
}
