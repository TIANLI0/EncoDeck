// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Tests.Bluetooth;

/// <summary>Earbuds BlueCaster streams to: followed as connected, while connecting and disconnecting them stays BlueCaster's.</summary>
public sealed class DeviceManagerBridgeTests
{
    private static readonly DeviceIdentity Earbuds = new("A2DPWB#0011223344AA", "EDIFIER W830NB", new BluetoothAddress(0x0011223344AA));

    private static DeviceManager Create() =>
        new(new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn()), new DeviceStateStore(), new NeverConnector(), () => null, () => false);

    [Fact]
    public async Task EarbudsBlueCasterStreamsToAreConnectedThroughIt()
    {
        await using var manager = Create();
        manager.SetBridgeDevices([new PairedDevice(Earbuds, IsConnected: false, PairedDevice.AudioVideoMajorClass)]);
        var idle = Assert.Single(manager.KnownDevices, d => d.Identity.DeviceId == Earbuds.DeviceId);
        Assert.False(idle.IsConnected);
        Assert.False(idle.IsViaBridge);

        // BlueCaster connects them (started from its window, the tray or with Windows): EncoDeck follows.
        manager.SetBridgeDevices([new PairedDevice(Earbuds, IsConnected: true, PairedDevice.AudioVideoMajorClass)]);
        var streaming = Assert.Single(manager.KnownDevices, d => d.Identity.DeviceId == Earbuds.DeviceId);
        Assert.True(streaming.IsConnected);
        Assert.True(streaming.IsViaBridge);
    }

    [Fact]
    public async Task ConnectingAndDisconnectingThemIsLeftToBlueCaster()
    {
        await using var manager = Create();
        manager.SetBridgeDevices([new PairedDevice(Earbuds, IsConnected: true, PairedDevice.AudioVideoMajorClass)]);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(CommandStatus.Unsupported, (await manager.DisconnectDeviceAsync(Earbuds.DeviceId, cancellationToken: ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await manager.ConnectDeviceAsync(Earbuds.DeviceId, ct)).Status);
    }

    private sealed class NeverConnector : IChannelConnector
    {
        public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken) =>
            Task.FromResult(ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "test"));
    }
}
