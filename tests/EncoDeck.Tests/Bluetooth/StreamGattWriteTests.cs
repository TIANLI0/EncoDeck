// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Xiaomi;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace EncoDeck.Tests.Bluetooth;

public class StreamGattWriteTests
{
    private const GattCharacteristicProperties Both = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;

    [Theory]
    [InlineData(Both, false, GattWriteOption.WriteWithResponse)]
    [InlineData(Both, true, GattWriteOption.WriteWithoutResponse)]
    [InlineData(GattCharacteristicProperties.Write, true, GattWriteOption.WriteWithResponse)]
    [InlineData(GattCharacteristicProperties.Write, false, GattWriteOption.WriteWithResponse)]
    [InlineData(GattCharacteristicProperties.WriteWithoutResponse, false, GattWriteOption.WriteWithoutResponse)]
    [InlineData(GattCharacteristicProperties.WriteWithoutResponse, true, GattWriteOption.WriteWithoutResponse)]
    public void ChooseWriteOption_PrefersUnacknowledgedOnlyWhenAsked(GattCharacteristicProperties properties, bool prefer, GattWriteOption expected) =>
        Assert.Equal(expected, StreamGattByteChannel.ChooseWriteOption(properties, prefer));

    [Fact]
    public void OnlyXiaomiMma_PrefersUnacknowledgedWrites()
    {
        Assert.True(GattStreamEndpoints.Find(XiaomiProtocol.MmaGattServiceUuid)!.PreferUnacknowledgedWrites);
        Assert.All(GattStreamEndpoints.All.Where(e => e.Service != XiaomiProtocol.MmaGattServiceUuid), e => Assert.False(e.PreferUnacknowledgedWrites));
    }

    [Fact]
    public void StalledIdentity_IsTriedLast_UntilItAnswersOrTheMemoryExpires()
    {
        const ulong earbuds = 0xAABBCCDD1DD0;
        var primary = new LeTarget(earbuds, false);
        var secondary = new LeTarget(0x5A0000001234, true);
        var registry = new GattWriteStallRegistry();
        Assert.Null(registry.RecentlyStalled(earbuds, 1_000));

        registry.MarkStalled(earbuds, primary, 1_000);
        var stalled = registry.RecentlyStalled(earbuds, 5_000);
        Assert.Equal(primary, stalled);
        Assert.Equal([secondary, primary], GattWriteStallRegistry.Order([primary, secondary], stalled));
        // A lone identity is still tried.
        Assert.Equal([primary], GattWriteStallRegistry.Order([primary], stalled));
        Assert.Null(registry.RecentlyStalled(earbuds, 1_000 + (long)GattWriteStallRegistry.Memory.TotalMilliseconds));
        Assert.Null(registry.RecentlyStalled(0x112233445566, 5_000));

        registry.MarkHealthy(earbuds, secondary);
        Assert.Equal(primary, registry.RecentlyStalled(earbuds, 5_000));
        registry.MarkHealthy(earbuds, primary);
        Assert.Null(registry.RecentlyStalled(earbuds, 5_000));
        Assert.Equal([primary, secondary], GattWriteStallRegistry.Order([primary, secondary], null));
    }
}
