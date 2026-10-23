// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.DokiDoki;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.DokiDoki;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using EncoDeck.Tests.Protocol;

namespace EncoDeck.Tests.DokiDoki;

public sealed class DokiDokiProfileTests
{
    private static DeviceIdentity Named(string name) => new("BluetoothLE#dokidoki-test", name, new BluetoothAddress(0xC0FFEE123456));

    [Theory]
    [InlineData("DK-META2", DokiDokiFamily.Meta2)]
    [InlineData("tf-meta2", DokiDokiFamily.Meta2)]
    [InlineData("DK-BH2PRO", DokiDokiFamily.Bh2Pro)]
    public void ForName_ResolvesKnownFamilies(string name, DokiDokiFamily family)
    {
        var profile = DokiDokiProfile.ForName(name);
        Assert.NotNull(profile);
        Assert.Equal(family, profile.Family);
        Assert.Equal(100, profile.MatchScore(Named(name)));
    }

    [Theory]
    [InlineData("TRYFUN-X1")]
    [InlineData("DK-UNKNOWN")]
    [InlineData(null)]
    public void ForName_DoesNotGuessAFamily(string? name)
    {
        Assert.Null(DokiDokiProfile.ForName(name));
        Assert.Equal(0, new DokiDokiProfile(DokiDokiFamily.Meta2).MatchScore(Named(name ?? string.Empty)));
    }

    [Fact]
    public void Profile_ExposesNoHeadsetCapabilities()
    {
        var profile = new DokiDokiProfile(DokiDokiFamily.Meta2);
        Assert.All(profile.Capabilities.All, c => Assert.Equal(CapabilitySupport.Unsupported, c.Value.Support));
        Assert.Empty(profile.NoiseControlOptions);
        Assert.Equal([DokiDokiConstants.ServiceUuid], profile.ControlServiceUuids);
        Assert.Equal([ActuatorChannel.Vibration, ActuatorChannel.Linear, ActuatorChannel.Rotary], profile.Channels);
    }

    [Fact]
    public void Profile_IsNotRegisteredAsAHeadset()
    {
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        Assert.DoesNotContain(registry.Profiles, p => p is DokiDokiProfile);
        Assert.Null(registry.Match(Named("DK-META2")));
    }

    [Fact]
    public void Profile_IsACupBrandAndHeadsetsStayHeadsets()
    {
        IDeviceProfile doki = new DokiDokiProfile(DokiDokiFamily.Bh2Pro);
        Assert.Equal(DeviceCategory.Cup, doki.Category);
        Assert.Equal(DokiDokiProfile.BrandId, ((ICupProfile)doki).Brand);
        Assert.All(BuiltInProfiles.Create(), p => Assert.Equal(DeviceCategory.Headset, p.Category));
    }

    [Theory]
    [InlineData(DokiDokiFamily.Bh2Pro, new[] { ActuatorChannel.Vibration })]
    [InlineData(DokiDokiFamily.Meta2, new ActuatorChannel[0])]
    public void ChannelsNeedingSleeve_OnlyBh2ProVibration(DokiDokiFamily family, ActuatorChannel[] expected) =>
        Assert.Equal(expected, ((ICupProfile)new DokiDokiProfile(family)).ChannelsNeedingSleeve);

    [Fact]
    public void Models_CoverEveryFamily() =>
        Assert.Equal(Enum.GetValues<DokiDokiFamily>(), DokiDokiProfile.Models.Select(m => m.Family));

    [Theory]
    [InlineData("01 02 00 03 EF 01 10", true)]
    [InlineData("0F 02 00 03 EF 01 10", true)]
    [InlineData("01 02 00 03 08 14 E4", false)]
    [InlineData("01 02 00 03 EF", false)]
    public void IsHeartbeatFrame_RoutesOnlyHeartbeats(string hex, bool heartbeat) =>
        Assert.Equal(heartbeat, DokiDokiCommands.IsHeartbeatFrame(Convert.FromHexString(hex.Replace(" ", string.Empty))));

    [Fact]
    public void Advertisement_MatchesByNamePrefixOrService()
    {
        Assert.True(DokiDokiAdvertisementWatcher.Matches("TRYFUN-X1", []));
        Assert.True(DokiDokiAdvertisementWatcher.Matches(null, [DokiDokiConstants.ServiceUuid]));
        Assert.False(DokiDokiAdvertisementWatcher.Matches("OPPO Enco X4", [Guid.NewGuid()]));
    }

    [Fact]
    public void Advertisement_KeepsANameSeenInAnEarlierPacket()
    {
        var now = DateTimeOffset.UnixEpoch;
        var first = DokiDokiAdvertisementWatcher.Merge(null, 0xC0FFEE123456, true, "DK-META2\0", -60, now);
        var second = DokiDokiAdvertisementWatcher.Merge(first, 0xC0FFEE123456, true, null, -55, now.AddSeconds(1));

        Assert.Equal("DK-META2", second.Name);
        Assert.Equal(DokiDokiFamily.Meta2, second.Family);
        Assert.Equal(-55, second.Rssi);
        Assert.True(second.IsRandomAddress);
    }

    private sealed class AlwaysUp : ILinkStateSource
    {
        public bool IsConnected => true;
        public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class FakeConnector : IChannelConnector
    {
        public FakeByteChannel? Last { get; private set; }

        public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
        {
            Last = new FakeByteChannel();
            return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, Last, serviceUuids[0], null));
        }
    }

    [Fact]
    public async Task Supervisor_ReachesReady_AndStopsOnDispose()
    {
        var identity = Named("DK-BH2PRO");
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity });
        var connector = new FakeConnector();
        var supervisor = new ControlChannelSupervisor(identity, new DokiDokiProfile(DokiDokiFamily.Bh2Pro), new AlwaysUp(), connector, store,
            new ReconnectPolicy(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromMilliseconds(40), 3), () => true);
        supervisor.Start();

        await OppoDeviceControllerTests.WaitUntil(() => store.Current.Connection == DeviceConnectionState.ControlReady);
        var actuator = Assert.IsAssignableFrom<IActuatorCommands>(supervisor.Controller);
        Assert.True((await actuator.SetIntensityAsync(ActuatorChannel.Vibration, 20, TestContext.Current.CancellationToken)).IsSuccess);

        await supervisor.DisposeAsync();

        var last = connector.Last!.Written.Where(f => !DokiDokiCommands.IsHeartbeatFrame(f)).Last();
        Assert.Equal(DokiDokiConstants.Bh2ProStopCommand, last[4]);
        Assert.Equal(0, last[5]);
        Assert.True(connector.Last.IsDisposed);
    }
}
