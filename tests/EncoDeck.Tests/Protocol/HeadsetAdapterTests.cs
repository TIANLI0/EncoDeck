// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Brands.Usb;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed class HeadsetAdapterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static DeviceIdentity Identity(string name, DeviceTransport transport = DeviceTransport.Bluetooth) => new("adapter-test", name, new BluetoothAddress(0)) { Transport = transport };

    [Theory]
    [InlineData("AKG N5 HYBRID", "akg-n5hybrid")]
    [InlineData("AKG N5", "akg-n5hybrid")]
    [InlineData("AKG N9 Hybrid", "akg-n9hybrid")]
    [InlineData("MOMENTUM 4", "sennheiser-m4")]
    [InlineData("Sennheiser ACCENTUM Plus", "sennheiser-acp1")]
    [InlineData("MOMENTUM TW4", "sennheiser-tw4")]
    [InlineData("CX Plus True Wireless", "sennheiser-cx400ii")]
    public void ExactModelSelectsControllableProfile(string name, string id)
    {
        var profile = new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(Identity(name));
        Assert.IsAssignableFrom<IControllableProfile>(profile);
        Assert.Equal(id, profile!.ModelId);
        Assert.NotEmpty(profile.ControlServiceUuids);
    }

    [Theory]
    [InlineData("AKG N5 Hybrid", true)]
    [InlineData("AKG N9 Hybrid", false)]
    public async Task BuiltInAkgHybridAdvancesFromAudioConnectedToControlReady(string name, bool earbuds)
    {
        // 1.4.0 selected an artwork-only profile and remained AudioConnected without an error.
        var identity = Identity(name);
        var profile = Assert.IsType<AkgHybridProfile>(new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile, Connection = DeviceConnectionState.AudioConnected });
        var device = new SimulatedChannel(false);
        var connector = new HybridConnector(identity.DeviceId, device);
        using var link = new ConnectedLink();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StateChanged += (_, e) =>
        {
            if (e.Current.Connection == DeviceConnectionState.ControlReady) ready.TrySetResult();
        };
        await using var supervisor = new ControlChannelSupervisor(identity, profile, link, connector, store, linkSettleDelay: TimeSpan.Zero);
        supervisor.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.NotNull(supervisor.Controller);
        Assert.Null(store.Current.LastError);
        Assert.Equal(90, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(earbuds ? 80 : (int?)null, store.Current.Battery.Right?.Value.Level);
        // AKG Hybrid noise control is experimental opt-in; the supervisor here does not opt in.
        Assert.Null(store.Current.NoiseControl);
        Assert.Null(device.LastAncWrite);
    }

    private sealed class ConnectedLink : ILinkStateSource
    {
        public bool IsConnected => true;
        public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class HybridConnector(string deviceId, IDuplexByteChannel channel) : IChannelConnector
    {
        public Task<ChannelOpenResult> OpenAsync(string selectedId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
        {
            Assert.Equal(deviceId, selectedId);
            Assert.Contains(Guid.Parse("00000000-0000-0000-0099-aabbccddeeff"), serviceUuids);
            Assert.Contains(Guid.Parse("00001101-0000-1000-8000-00805f9b34fb"), serviceUuids);
            return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, channel, serviceUuids[0], null));
        }
    }

    [Fact]
    public void RaceAndGaiaFramingSurviveFragmentationNoiseAndRejectWrongVendorsChecksumsAndFlags()
    {
        var race = new RaceFrameCodec();
        var decoded = new List<BrandPacket>();
        var raceBytes = RaceFrameCodec.Encode(0x0CD6, [0, 1, 80], 0x5D);
        foreach (byte b in new byte[] { 1, 2, 3 }.Concat(raceBytes).Concat(raceBytes)) decoded.AddRange(race.Feed([b]));
        Assert.Equal(2, decoded.Count);
        Assert.All(decoded, p => Assert.Equal(new byte[] { 0, 1, 80 }, p.Payload));
        var gaia = new SennheiserFrameCodec();
        var valid = SennheiserFrameCodec.Encode(0x0703, [85]);
        byte[] wrongVendor = (byte[])valid.Clone(); wrongVendor[5] = 0x94;
        byte[] badFlags = (byte[])valid.Clone(); badFlags[2] = 0x80;
        byte[] badChecksum = [.. valid, 0]; badChecksum[2] = 1;
        byte xor = 0; foreach (byte b in badChecksum) xor ^= b;
        badChecksum[^1] = (byte)(xor ^ 1);
        var packets = new List<BrandPacket>();
        foreach (byte b in wrongVendor.Concat(badFlags).Concat(badChecksum).Concat(valid)) packets.AddRange(gaia.Feed([b]));
        Assert.Equal(new byte[] { 85 }, Assert.Single(packets).Payload);
        Assert.Equal(new byte[] { 0xFF, 3, 0, 0, 4, 0x95, 6, 3 }, SennheiserFrameCodec.Encode(0x0603, []));
    }

    [Fact]
    public void SennheiserBleBridgePreservesPduAndHandlesExtendedNotifications()
    {
        var command = SennheiserFrameCodec.Encode(0x0603, []);
        Assert.Equal(new byte[] { 4, 0x95, 6, 3 }, SennheiserGattFraming.UnwrapCommand(command));
        byte[] payload = Enumerable.Repeat((byte)42, 300).ToArray();
        byte[] frame = SennheiserGattFraming.WrapNotification([4, 0x95, 7, 3, .. payload]);
        Assert.Equal(payload, Assert.Single(new SennheiserFrameCodec().Feed(frame)).Payload);
        Assert.Throws<InvalidDataException>(() => SennheiserGattFraming.WrapNotification([0, 0x1D, 7, 3]));
        Assert.Throws<InvalidDataException>(() => SennheiserGattFraming.UnwrapCommand([0xFF, 3, 0, 8, 4, 0x95, 6, 3]));
    }

    [Theory]
    [InlineData(false, "AB1585AM")]
    [InlineData(true, "AB1585AM")]
    [InlineData(false, "AB1552")]
    [InlineData(true, "AB1552")]
    public async Task AkgHybridReadsCorrectPhysicalSidesAndOffersAncOnlyWhenExperimental(bool agentRight, string chip)
    {
        bool modern = chip.StartsWith("AB158", StringComparison.Ordinal);
        var identity = Identity("AKG N5 Hybrid");
        var profile = new AkgHybridProfile(true);
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new SimulatedChannel(false) { AgentRight = agentRight, ChipName = chip };
        bool experimental = false;
        await using var controller = profile.CreateController(device, new(identity, store, () => experimental, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(agentRight ? 80 : 90, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(agentRight ? 90 : 80, store.Current.Battery.Right?.Value.Level);
        Assert.Equal(60, store.Current.Battery.Case?.Value.Level);
        Assert.False(store.Current.Battery.Left?.Value.IsCharging);
        // Without the experimental opt-in nothing is offered, read or written, and the profile says why.
        Assert.Null(store.Current.NoiseControl);
        Assert.Equal(CapabilitySupport.Experimental, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Empty(store.Current.Profile.NoiseControlOptions);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Null(device.LastAncWrite);
        Assert.DoesNotContain(device.SentCommands, c => c is 0x0901 or 0x0E06 or 0x0A00);

        experimental = true;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
        Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Contains(store.Current.Profile.NoiseControlOptions, o => o.Mode == NoiseControlMode.Transparency);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(modern ? new byte[] { 0, 0x0A, 0x0A, 4 } : [0, 0x0A, 6], device.LastAncWrite);
        Assert.Equal(modern ? (byte)10 : (byte)6, device.Filter);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0, 0x0B }, device.LastAncWrite);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(modern ? new byte[] { 0, 0x0A, 1, 0 } : [0, 0x0A, 1], device.LastAncWrite);
        device.RejectSet = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
        // The chip name is read once per session.
        Assert.Single(device.SentCommands, c => c == 0x0A00);
        device.AgentRight = !agentRight;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(agentRight ? 90 : 80, store.Current.Battery.Left?.Value.Level);
    }

    [Fact]
    public async Task AkgHybridOffersAmbientOnlyOnceThePassThroughNumberingIsKnown()
    {
        var identity = Identity("AKG N9 Hybrid");
        var profile = new AkgHybridProfile(false);
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new SimulatedChannel(false) { ChipName = null };
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(90, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
        Assert.Equal([NoiseControlMode.Off, NoiseControlMode.NoiseCancellation], store.Current.Profile!.NoiseControlOptions.Select(o => o.Mode));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Null(device.LastAncWrite);
        // Without a chip name the classic ANC payload carries no mode byte.
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).IsSuccess);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0, 0x0A, 1 }, device.LastAncWrite);

        // Ambient Aware selected on the headset itself (filter 10) reveals the AB156x numbering.
        device.Filter = 10;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
        Assert.Contains(store.Current.Profile!.NoiseControlOptions, o => o.Mode == NoiseControlMode.Transparency);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0, 0x0A, 1, 0 }, device.LastAncWrite);
    }

    [Theory]
    [InlineData((byte)2, (byte)1, "status 0x02")]
    [InlineData((byte)0, (byte)8, "unrecognized ANC status 05000008")]
    public async Task AkgHybridExplainsAnUnusableAncReplyInsteadOfHidingNoiseControlSilently(byte status, byte filter, string reason)
    {
        var identity = Identity("AKG N9 Hybrid");
        var profile = new AkgHybridProfile(false);
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new SimulatedChannel(false) { ChipName = "AB1585", AncStatus = status, Filter = filter };
        await using var controller = profile.CreateController(device, new(identity, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(90, store.Current.Battery.Left?.Value.Level);
        Assert.Null(store.Current.NoiseControl);
        var entry = store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation);
        Assert.Equal(CapabilitySupport.Unsupported, entry.Support);
        Assert.Contains(reason, entry.Evidence, StringComparison.Ordinal);
        Assert.Empty(store.Current.Profile.NoiseControlOptions);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
        Assert.Null(device.LastAncWrite);

        // A later answer recovers without reconnecting.
        device.AncStatus = 0; device.Filter = 0;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl?.Value.Mode);
        Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
    }

    [Fact]
    public void AkgRaceFramesAndRepliesFollowTheAirohaLayout()
    {
        Assert.Equal(new byte[] { 5, 0x5A, 4, 0, 0x01, 0x09, 5, 0 }, RaceFrameCodec.Encode(AkgRace.MmiGetEnum, AkgRace.AncStatusRequest));
        Assert.Equal(new byte[] { 5, 0x5A, 6, 0, 0x00, 0x0A, 0x02, 0x10, 0xE8, 0x03 }, RaceFrameCodec.Encode(AkgRace.NvKeyRead, AkgRace.ChipNameRequest));
        Assert.Equal(new byte[] { 5, 0x5A, 4, 0, 0x06, 0x0E, 0, 0x0B }, RaceFrameCodec.Encode(AkgRace.Anc, AkgRace.AncPayload(NoiseControlMode.Off, AirohaAncLayout.Unknown)!));

        // ANC status: [05 00 status filter (gain16)], with or without the gain.
        Assert.True(AkgRace.TryParseAncStatus([5, 0, 0, 10, 0x64, 0], out byte filter));
        Assert.Equal(10, filter);
        Assert.True(AkgRace.TryParseAncStatus([5, 0, 0, 1], out filter));
        Assert.Equal(1, filter);
        Assert.False(AkgRace.TryParseAncStatus([5, 0, 2, 1], out _));
        Assert.False(AkgRace.TryParseAncStatus([7, 0, 0, 1], out _));
        Assert.False(AkgRace.TryParseAncStatus([5, 0, 0], out _));
        Assert.True(AkgRace.IsAncStatusReply([5, 0, 2]));

        var layout = AirohaAncLayout.Unknown;
        Assert.Equal(NoiseControlMode.Off, AkgRace.ModeOf(0, ref layout));
        Assert.Equal(NoiseControlMode.NoiseCancellation, AkgRace.ModeOf(4, ref layout));
        Assert.Equal(AirohaAncLayout.Unknown, layout);
        Assert.Null(AkgRace.ModeOf(8, ref layout));
        Assert.Null(AkgRace.ModeOf(12, ref layout));
        Assert.Equal(NoiseControlMode.Transparency, AkgRace.ModeOf(6, ref layout));
        Assert.Equal(AirohaAncLayout.Ab155x, layout);
        Assert.Null(AkgRace.ModeOf(10, ref layout));
        layout = AirohaAncLayout.Unknown;
        Assert.Equal(NoiseControlMode.Transparency, AkgRace.ModeOf(9, ref layout));
        Assert.Equal(AirohaAncLayout.Ab156x, layout);
        Assert.Null(AkgRace.ModeOf(5, ref layout));

        Assert.Equal(new byte[] { 0, 0x0A, 1 }, AkgRace.AncPayload(NoiseControlMode.NoiseCancellation, AirohaAncLayout.Ab155x));
        Assert.Equal(new byte[] { 0, 0x0A, 1, 0 }, AkgRace.AncPayload(NoiseControlMode.NoiseCancellation, AirohaAncLayout.Ab156x));
        Assert.Equal(new byte[] { 0, 0x0A, 6 }, AkgRace.AncPayload(NoiseControlMode.Transparency, AirohaAncLayout.Ab155x));
        Assert.Equal(new byte[] { 0, 0x0A, 10, 4 }, AkgRace.AncPayload(NoiseControlMode.Transparency, AirohaAncLayout.Ab156x));
        Assert.Null(AkgRace.AncPayload(NoiseControlMode.Transparency, AirohaAncLayout.Unknown));
        Assert.Null(AkgRace.AncPayload(NoiseControlMode.Adaptive, AirohaAncLayout.Ab156x));
        Assert.True(AkgRace.IsAncReply([0, 0x0A, 1], [0, 0x0A]));
        Assert.False(AkgRace.IsAncReply([0, 0x0A, 1], [0, 0x0B]));
        Assert.False(AkgRace.IsAncReply([0, 0x0B], [0]));

        Assert.True(AkgRace.TryParseChipName([8, 0, .. "AB1585AM"u8], out string name));
        Assert.Equal("AB1585AM", name);
        Assert.True(AkgRace.TryParseChipName([7, 0, .. "AB1552\0"u8], out name));
        Assert.Equal("AB1552", name);
        Assert.False(AkgRace.TryParseChipName([0, 0], out _));
        Assert.False(AkgRace.TryParseChipName([9, 0, .. "AB1585AM"u8], out _));
        Assert.False(AkgRace.TryParseChipName([2, 0, 0x01, 0x41], out _));
        Assert.Equal(AirohaAncLayout.Ab156x, AkgRace.Classify("AB1585AM"));
        Assert.Equal(AirohaAncLayout.Ab156x, AkgRace.Classify("ab1568_dual_chip"));
        Assert.Equal(AirohaAncLayout.Ab156x, AkgRace.Classify("MT2833"));
        Assert.Equal(AirohaAncLayout.Ab155x, AkgRace.Classify("AB1552"));
        Assert.Equal(AirohaAncLayout.Unknown, AkgRace.Classify("AB1562E"));
        Assert.Equal(AirohaAncLayout.Unknown, AkgRace.Classify("BES2700"));

        // Indications without a response (5C) are framed too.
        var codec = new RaceFrameCodec();
        var packets = codec.Feed(RaceFrameCodec.Encode(AkgRace.Anc, [0, 0x0A], 0x5C));
        Assert.Equal(0x5C, Assert.Single(packets).Type);
    }

    [Fact]
    public async Task SennheiserUsesGaiaV3ResponseBitsAndNeverOffersAncOffOrAcceptsAnErrorAsSuccess()
    {
        var identity = Identity("MOMENTUM 4");
        var profile = (SennheiserProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new SimulatedChannel(true);
        await using var controller = profile.CreateController(device, Context(identity, store));
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(85, store.Current.Battery.Left?.Value.Level);
        Assert.Equal("3.38.3", store.Current.Firmware?.Value.Version);
        Assert.DoesNotContain(store.Current.Profile!.NoiseControlOptions, n => n.Mode == NoiseControlMode.Off);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(100, device.Transparency);
        device.RejectSet = true;
        Assert.False((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
    }

    [Theory]
    [InlineData("Speakers (RAPOO VH800)", "rapoo-usb-vh800")]
    [InlineData("Speakers (RAPOO VH800Pro)", "rapoo-usb-vh800-pro")]
    [InlineData("扬声器 (HECATE G4S)", "edifier-usb-g4s")]
    [InlineData("DAREU EH925 ULTRA", "dareu-usb-eh925-ultra")]
    public void UsbProfilesRequireUsbAndKeepSimilarModelsSeparate(string name, string expected)
    {
        var registry = new DeviceProfileRegistry(UsbHeadsetProfiles.Create());
        Assert.Null(registry.Match(Identity(name)));
        var profile = registry.Match(Identity(name, DeviceTransport.Usb));
        Assert.Equal(expected, profile?.ModelId);
        Assert.Empty(profile!.NoiseControlOptions);
        Assert.Empty(profile.ControlServiceUuids);
        Assert.Null(registry.Match(Identity(profile.DisplayName + "not-a-headset", DeviceTransport.Usb)));
    }

    [Theory]
    [InlineData(6, 32, 0, 100, true)]
    [InlineData(133, 100, 0, 100, true)]
    [InlineData(133, 102, 0, 65535, false)]
    [InlineData(65280, 32, 0, 100, false)]
    [InlineData(6, 32, 0, 255, false)]
    public void UsbBatteryAcceptsOnlyDescriptorDeclaredStandardPercentages(int page, int usage, int min, int max, bool expected) =>
        Assert.Equal(expected, UsbHidBattery.IsPercentage((ushort)page, (ushort)usage, min, max));

    [Fact]
    public void RapooHardwareRecognitionRequiresTheExactOfficialDriverVidPid()
    {
        Assert.Equal("rapoo-usb-vh800-pro", UsbHeadsetProfiles.MatchHardware([@"USB\VID_24AE&PID_7205&MI_00"])?.ModelId);
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_24AE&PID_72050"]));
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_24AE&PID_7206"]));
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"BTHENUM\VID_24AE&PID_7205"]));
    }

    private static ControllerContext Context(DeviceIdentity identity, IDeviceStateStore store) => new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);

    private sealed class SimulatedChannel(bool sennheiser) : IDuplexByteChannel
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        public string Description => "Simulated official protocol device";
        public bool AgentRight { get; set; }
        public string? ChipName { get; set; }
        public byte AncStatus { get; set; }
        public List<ushort> SentCommands { get; } = [];
        public byte Filter { get; set; } = 1;
        public byte Transparency { get; private set; }
        public bool RejectSet { get; set; }
        public byte[]? LastAncWrite { get; private set; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            byte[] bytes = data.ToArray();
            if (sennheiser)
            {
                ushort command = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6));
                byte[] payload = command switch
                {
                    0x0603 => [85], 0x0602 => [0], 0x1201 => [0, 3, 0, 38, 0, 3],
                    0x1206 => "M4AEBT"u8.ToArray(), 0x1A03 => [Transparency], _ => [],
                };
                bool error = command == 0x1A02 && RejectSet;
                if (command == 0x1A02 && !error) Transparency = bytes[8];
                _responses.Writer.TryWrite(SennheiserFrameCodec.Encode((ushort)(command | (error ? 0x180 : 0x100)), error ? [1] : payload));
            }
            else
            {
                ushort command = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
                SentCommands.Add(command);
                byte type = command is 0x0CD6 or 0x000B or 0x0009 ? (byte)0x5D : (byte)0x5B;
                byte[] payload = command switch
                {
                    0x0CD4 => [0, AgentRight ? (byte)1 : (byte)0],
                    0x0CD6 => [0, bytes[6], bytes[6] == 0 ? (byte)90 : (byte)80],
                    0x000B => [0, 60],
                    0x0009 => [0, bytes[6], 0],
                    // NV key 0x1002: [length u16 LE, ASCII chip name]; length 0 when unknown.
                    0x0A00 => ChipName is null ? [0, 0] : [(byte)ChipName.Length, 0, .. System.Text.Encoding.ASCII.GetBytes(ChipName)],
                    // MMI get enum, module 5: [05 00 status filter gain16].
                    0x0901 => [5, 0, AncStatus, Filter, 0x64, 0],
                    0x0E06 => [RejectSet ? (byte)1 : (byte)0, bytes[7]],
                    _ => throw new InvalidOperationException($"Unexpected command {command:X4}"),
                };
                if (command == 0x0E06)
                {
                    LastAncWrite = bytes[6..];
                    if (!RejectSet) Filter = bytes[7] == 0x0B ? (byte)0 : bytes[8];
                }
                var response = RaceFrameCodec.Encode(command, payload, type);
                // Split a real RACE header across reads to exercise the session deframer too.
                _responses.Writer.TryWrite(response[..3]); _responses.Writer.TryWrite(response[3..]);
            }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            try { var response = await _responses.Reader.ReadAsync(ct); response.CopyTo(buffer); return response.Length; }
            catch (ChannelClosedException) { return 0; }
        }
        public ValueTask DisposeAsync() { _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
