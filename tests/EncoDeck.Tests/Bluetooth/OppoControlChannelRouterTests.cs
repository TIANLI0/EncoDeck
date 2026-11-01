// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Bluetooth;

public class OppoControlChannelRouterTests
{
    [Theory]
    [InlineData(ChannelOpenStatus.Opened)]
    [InlineData(ChannelOpenStatus.DeviceUnreachable)]
    [InlineData(ChannelOpenStatus.ServiceNotFound)]
    [InlineData(ChannelOpenStatus.AccessDenied)]
    [InlineData(ChannelOpenStatus.ConnectFailed)]
    public async Task LeBond_NeverTouchesClassic_EvenWhenGattFails(ChannelOpenStatus status)
    {
        int classicOpens = 0, gattOpens = 0;
        await using var channel = new FakeByteChannel();
        var result = await OppoControlChannelRouter.OpenAsync(
            () => Task.FromResult(true),
            () => { gattOpens++; return Task.FromResult(new ChannelOpenResult(status, status == ChannelOpenStatus.Opened ? channel : null, null, "GATT")); },
            () => { classicOpens++; throw new InvalidOperationException("Classic must not be touched."); });

        Assert.Equal(0, classicOpens);
        Assert.Equal(1, gattOpens);
        Assert.Equal(status is ChannelOpenStatus.AccessDenied or ChannelOpenStatus.ServiceNotFound ? ChannelOpenStatus.ConnectFailed : status, result.Status);
    }

    [Fact]
    public async Task LeBondWithClassicBond_OpensClassicFirst()
    {
        await using var channel = new FakeByteChannel();
        var calls = new List<string>();
        var result = await OppoControlChannelRouter.OpenAsync(
            () => Task.FromResult(true),
            () => throw new InvalidOperationException("GATT must not be opened when RFCOMM works."),
            () => { calls.Add("classic"); return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, channel, null, null)); },
            () => true);

        Assert.Equal(["classic"], calls);
        Assert.Equal(ChannelOpenStatus.Opened, result.Status);
    }

    [Theory]
    [InlineData(ChannelOpenStatus.DeviceUnreachable)]
    [InlineData(ChannelOpenStatus.ServiceNotFound)]
    [InlineData(ChannelOpenStatus.ConnectFailed)]
    public async Task LeBondWithClassicBond_FallsBackToGattWhenRfcommFails(ChannelOpenStatus classicStatus)
    {
        await using var channel = new FakeByteChannel();
        var calls = new List<string>();
        var result = await OppoControlChannelRouter.OpenAsync(
            () => Task.FromResult(true),
            () => { calls.Add("gatt"); return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, channel, null, "GATT")); },
            () => { calls.Add("classic"); return Task.FromResult(ChannelOpenResult.Failed(classicStatus, "RFCOMM")); },
            () => true);

        Assert.Equal(["classic", "gatt"], calls);
        Assert.Equal(ChannelOpenStatus.Opened, result.Status);
    }

    [Fact]
    public async Task LeBondWithoutClassicBond_OpensGattOnly()
    {
        await using var channel = new FakeByteChannel();
        var result = await OppoControlChannelRouter.OpenAsync(
            () => Task.FromResult(true),
            () => Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, channel, null, "GATT")),
            () => throw new InvalidOperationException("Classic must not be touched without its bond."),
            () => false);

        Assert.Equal(ChannelOpenStatus.Opened, result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownPairingState_DoesNotFallBackToClassic(bool comFailure)
    {
        int opens = 0;
        var result = await OppoControlChannelRouter.OpenAsync(
            () => throw (comFailure ? new COMException("Windows is reconnecting") : new InvalidOperationException("Pairing change in progress")),
            () => { opens++; throw new InvalidOperationException(); },
            () => { opens++; throw new InvalidOperationException(); });

        Assert.Equal(0, opens);
        Assert.Equal(ChannelOpenStatus.ConnectFailed, result.Status);
    }

    [Fact]
    public async Task ClassicBond_OpensClassicAfterCheckingLePairing()
    {
        var calls = new List<string>();
        var result = await OppoControlChannelRouter.OpenAsync(
            () => { calls.Add("pairing"); return Task.FromResult(false); },
            () => throw new InvalidOperationException("GATT must not be opened."),
            () => { calls.Add("classic"); return Task.FromResult(ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "Classic")); });

        Assert.Equal(["pairing", "classic"], calls);
        Assert.Equal("Classic", result.Detail);
    }

    [Fact]
    public async Task Cancellation_DoesNotOpenEitherTransport()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => OppoControlChannelRouter.OpenAsync(
            () => throw new OperationCanceledException(),
            () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException()));
    }

    [Fact]
    public void PairingTransition_BlocksRoutingUntilOperationEnds()
    {
        const ulong address = 0x112233445567;
        Assert.False(LeAudioLink.IsPairingChanging(address));
        using (LeAudioLink.BeginPairingChange(address))
        {
            Assert.True(LeAudioLink.IsPairingChanging(address));
            Assert.Throws<InvalidOperationException>(() => LeAudioLink.BeginPairingChange(address));
        }
        Assert.False(LeAudioLink.IsPairingChanging(address));
    }

    [Theory]
    [InlineData("OPPO Enco X4", true)]
    // Swift Pair leaves an LE bond beside this Classic-only pair; it must still open RFCOMM.
    [InlineData("OPPO Enco Air5s", false)]
    public void PartialLeBondGuard_OnlyForModelsSwitchableToLeAudio(string name, bool guarded)
    {
        var profile = new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(new DeviceIdentity("id", name, new BluetoothAddress(0)));
        Assert.NotNull(profile);
        Assert.Equal(guarded, RfcommChannelConnector.GuardsPartialLeBond(profile));
    }
}
