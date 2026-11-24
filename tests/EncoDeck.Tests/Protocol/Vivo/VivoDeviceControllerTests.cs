// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Vivo;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Vivo;

public class VivoDeviceControllerTests
{
    private static readonly DeviceIdentity Tws4 = new("dev-vivo", "vivo TWS 4", new BluetoothAddress(0x112233445566));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Emulates vivo earbuds on a <see cref="FakeByteChannel"/>: decodes each write and answers.</summary>
    private sealed class FakeVivoDevice : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeByteChannel Channel { get; } = new();

        public byte Noise { get; set; } = VivoConstants.NoiseModeOff;

        public List<VivoFrame> Received { get; } = [];

        public FakeVivoDevice() => _loop = Task.Run(LoopAsync);

        private async Task LoopAsync()
        {
            int seen = 0;
            var decoder = new VivoFrameDecoder();
            while (!_stop.IsCancellationRequested)
            {
                var written = Channel.Written;
                for (; seen < written.Count; seen++)
                {
                    foreach (var frame in decoder.Feed(written[seen]))
                    {
                        lock (Received) Received.Add(frame);
                        foreach (var reply in Answer(frame)) Channel.Receive(reply.Encode(4));
                    }
                }
                try { await Task.Delay(5, _stop.Token); } catch (OperationCanceledException) { }
            }
        }

        private IEnumerable<VivoFrame> Answer(VivoFrame f)
        {
            switch (f.Command)
            {
                case VivoCommands.Handshake: yield return new(VivoCommands.HandshakeResponse, [0]); break;
                case VivoCommands.QueryBattery: yield return new(VivoCommands.ReportBattery, [0, 40, 90, 255, 1]); break;
                case VivoCommands.QueryWearState: yield return new(VivoCommands.ReportWearState, [0, 0x0C]); break;
                case VivoCommands.QueryNoiseMode: yield return new(VivoCommands.ReportNoiseMode, [0, Noise, 4]); break;
                case VivoCommands.QueryAudioEffect: yield return new(VivoCommands.ReportAudioEffect, [0, 0]); break;
                case VivoCommands.QueryFirmware: yield return new(VivoCommands.ReportFirmware, [0, 0x31, 0x2E, 0x32]); break;
                case VivoCommands.SetNoiseMode:
                    // Echo first (not a confirmation), then the real report a little later.
                    yield return new(VivoCommands.AckNoiseMode, f.Payload);
                    Noise = f.Payload[0];
                    yield return new(VivoCommands.ReportNoiseMode, [0, Noise, 4]);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop;
        }
    }

    private static (VivoDeviceController Controller, DeviceStateStore Store) Create(FakeVivoDevice device)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = Tws4, Connection = DeviceConnectionState.ControlConnecting });
        var profile = (VivoDeviceProfile)new DeviceProfileRegistry(VivoProfiles.CreateBuiltIn()).Match(Tws4)!;
        var context = new ControllerContext(Tws4, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return ((VivoDeviceController)profile.CreateController(device.Channel, context), store);
    }

    [Fact]
    public async Task Initialize_HandshakesRegistersAndPopulatesState()
    {
        await using var device = new FakeVivoDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));

        var s = store.Current;
        Assert.Equal(new BatteryReading(40, true), s.Battery.Left!.Value);
        Assert.Equal(new BatteryReading(90, false), s.Battery.Right!.Value);
        Assert.Null(s.Battery.Case);
        Assert.Equal(StateSource.QueryResponse, s.Battery.Left.Source);
        Assert.True(s.Wear!.Value.LeftInEar);
        Assert.True(s.Wear.Value.RightInEar);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), s.NoiseControl!.Value);
        Assert.Equal("vivo-eq-0", s.Equalizer!.Value.ActivePresetId);
        Assert.Equal("1.2", s.Firmware!.Value.Version);

        List<ushort> commands;
        lock (device.Received) commands = device.Received.Select(f => f.Command).ToList();
        Assert.Equal([VivoCommands.Handshake, 0x0202, 0x0203, 0x0204, 0x0205, 0x0206, VivoCommands.QueryBattery], commands.Take(7));
        Assert.All(device.Channel.Written, w => Assert.Equal(0xFF, w[0]));
    }

    [Fact]
    public async Task SetNoiseControl_IsConfirmedByTheDeviceReport()
    {
        await using var device = new FakeVivoDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation), store.Current.NoiseControl!.Value);
        Assert.Equal(StateSource.CommandConfirmation, store.Current.NoiseControl.Source);
        VivoFrame set;
        lock (device.Received) set = device.Received.Single(f => f.Command == VivoCommands.SetNoiseMode);
        Assert.Equal(new byte[] { 0x00, 0x03, 0x01 }, set.Payload);
    }

    [Fact]
    public async Task Initialize_FailsWhenTheHandshakeIsNotAnswered()
    {
        var channel = new FakeByteChannel();
        var store = new DeviceStateStore();
        var profile = new VivoDeviceProfile(VivoCatalog.Generic, isGeneric: true);
        var context = new ControllerContext(Tws4, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        await using var controller = profile.CreateController(channel, context);
        Assert.False(await controller.InitializeAsync(Ct));
    }

    [Fact]
    public async Task UnsupportedCommandsAreRefused()
    {
        await using var device = new FakeVivoDevice();
        var (controller, _) = Create(device);
        await using var _c = controller;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetSpatialAudioAsync(SpatialAudioMode.Off, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.HeadTracking, true, Ct)).Status);
    }

    /// <summary>Records whether any state change showed <paramref name="key"/> as pending.</summary>
    private static Func<bool> WatchPending(DeviceStateStore store, string key)
    {
        bool seen = false;
        store.StateChanged += (_, e) => seen |= e.Current.IsPending(key);
        return () => seen;
    }

    [Fact]
    public async Task Writes_ArePendingUntilConfirmed()
    {
        await using var device = new FakeVivoDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var seen = WatchPending(store, PendingKeys.NoiseControl);

        Assert.True((await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);

        Assert.True(seen());
        Assert.Empty(store.Current.PendingCommands);
        Assert.Null(store.Current.LastError);
    }

    [Fact]
    public async Task Writes_ClearThePendingMarkerWhenTheDeviceKeepsItsSetting()
    {
        await using var device = new FakeVivoDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var seen = WatchPending(store, PendingKeys.Equalizer);

        // The emulator never applies EQ writes and keeps reporting preset 0.
        var result = await controller.SetEqualizerPresetAsync("vivo-eq-1", Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.True(seen());
        Assert.Empty(store.Current.PendingCommands);
        Assert.Equal(CommandStatus.Rejected, store.Current.LastError!.Status);
        Assert.Equal("vivo-eq-0", store.Current.Equalizer!.Value.ActivePresetId);
    }

    [Fact]
    public async Task Writes_ClearThePendingMarkerWhenCancelled()
    {
        await using var device = new FakeVivoDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var write = controller.SetEqualizerPresetAsync("vivo-eq-1", cts.Token);
        while (!store.Current.IsPending(PendingKeys.Equalizer)) await Task.Delay(5, Ct);
        await cts.CancelAsync();

        Assert.Equal(CommandStatus.Cancelled, (await write).Status);
        Assert.Empty(store.Current.PendingCommands);
        Assert.Equal(CommandStatus.Cancelled, store.Current.LastError!.Status);
    }

    [Fact]
    public async Task Writes_ClearThePendingMarkerWhenTheChannelFails()
    {
        await using var device = new FakeVivoDevice();
        var (controller, store) = Create(device);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var seen = WatchPending(store, "toggle:GamingMode");
        device.Channel.FailWritesWith = new IOException("link lost");

        var result = await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct);

        Assert.Equal(CommandStatus.TransportError, result.Status);
        Assert.True(seen());
        Assert.Empty(store.Current.PendingCommands);
        Assert.Equal(CommandStatus.TransportError, store.Current.LastError!.Status);
        // Once the session is gone a write is refused before anything is marked.
        Assert.Equal(CommandStatus.NotConnected, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
        Assert.Empty(store.Current.PendingCommands);
    }
}
