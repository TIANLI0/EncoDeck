// SPDX-License-Identifier: GPL-3.0-only
// Synthetic fixtures from published EarKit protocol facts, not hardware captures.
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.EarFun;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public class EarFunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Reply(bool gaia, ushort command, byte[] payload, byte status = 0)
    {
        if (gaia) return [255, 4, 0, (byte)(payload.Length + 1), 0, 10, (byte)((command >> 8) | 128), (byte)command, status, .. payload];
        int sum = payload.Length + payload.Sum(v => (int)v);
        return [239, (byte)(command >> 8), (byte)command, (byte)payload.Length, .. payload, (byte)(sum & 255), 254];
    }

    [Theory]
    [InlineData("EarFun Air Pro 4", "earfun-tw600")]
    [InlineData("EarFun Air Pro 4+", "earfun-tw700")]
    [InlineData("EarFun Air Pro 4 Plus", "earfun-tw700")]
    [InlineData("EarFun Air Pro 4+ AI", "earfun-tw700ai")]
    [InlineData("EarFun Air Pro 4i", "earfun-tw230")]
    [InlineData("TW500_3072", "earfun-tw500-3072")]
    [InlineData("EarFun Tune Pro", "earfun-hp220")]
    [InlineData("ATVEL Tune S5 ProX", "earfun-atvel-tw201")]
    public void ExactModelNamesPreserveSuffixes(string name, string id)
    {
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        var profile = Assert.IsType<EarFunProfile>(registry.Match(new("dev", name, new BluetoothAddress(1))));
        Assert.Equal(id, profile.ModelId);
        Assert.Equal("earfun", profile.Brand);
        Assert.Equal(EarFunSettings.NoiseModes(profile.Model).Count, profile.NoiseControlOptions.Count);
        Assert.Empty(profile.EqualizerPresets);
        Assert.True(profile.BandEqualizer.WriteOnly);
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.CustomEqualizer, false));
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.FactoryReset, false));
        // Classic SPP control: a connected LE bond is not LE Audio, and EncoDeck must not keep it up (issue 16).
        Assert.False(profile.ManagesLeAudioLink);
    }

    [Fact]
    public void All23ProfilesResolveWithoutClaimingUnsupportedProducts()
    {
        Assert.Equal(23, EarFunProfiles.Models.Count);
        Assert.Equal(12, EarFunProfiles.Models.Count(m => m.Gaia));
        foreach (var model in EarFunProfiles.Models)
        {
            Assert.Same(model, EarFunProfiles.Named(model.Name));
            Assert.Equal(model.Headphones ? DeviceForm.Headphones : DeviceForm.Earbuds, new EarFunProfile(model).Form);
            Assert.Equal(new Guid("00001101-0000-1000-8000-00805f9b34fb"), new EarFunProfile(model).ControlServiceUuids.Single());
        }
        foreach (string name in new[] { "EarFun Air Pro 4+ 2", "EarFun Wave Pro", "EarFun Free Pro 2", "EarFun Air Pro SV", "EarFun UBOOM", "EarFun", "ATVEL Tune S5 Pro" }) Assert.Null(EarFunProfiles.Named(name));
    }

    [Fact]
    public void EncodersMatchPublishedCommandLiterals()
    {
        Assert.Equal(Convert.FromHexString("FF040000000A0309"), EarFunFrameCodec.Encode(true, 0x0309, []));
        Assert.Equal(Convert.FromHexString("FF040001000A03490F"), EarFunFrameCodec.Encode(true, 0x0349, [15]));
        Assert.Equal(Convert.FromHexString("EF2085010405FE"), EarFunFrameCodec.Encode(false, 0x2085, [4]));
        Assert.Equal(Convert.FromHexString("EF10140000FE"), EarFunFrameCodec.Encode(false, 0x1014, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => EarFunFrameCodec.Encode(false, 1, new byte[256]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecoderHandlesEverySplitNoiseAndCoalescedFrames(bool gaia)
    {
        ushort command = gaia ? (ushort)0x0306 : (ushort)0x1014;
        byte[] stream = [0x12,0x34, .. Reply(gaia, command, [81]), .. Reply(gaia, command, [43])];
        for (int split = 0; split <= stream.Length; split++)
        {
            var codec = new EarFunFrameCodec(gaia);
            var frames = codec.Feed(stream.AsSpan(0, split)).Concat(codec.Feed(stream.AsSpan(split))).ToArray();
            Assert.Equal(2, frames.Length);
            Assert.Equal(new byte[] { 81 }, frames[0].Payload);
            Assert.Equal(new byte[] { 43 }, frames[1].Payload);
        }
    }

    [Fact]
    public void EfChecksumAndGaiaVendorFlagsLengthAndOptionalChecksumAreChecked()
    {
        byte[] ef = Reply(false, 0x1014, [70, 60]); ef[^2] ^= 1;
        Assert.Empty(new EarFunFrameCodec(false).Feed(ef));
        byte[] wrongVendor = Reply(true, 0x0306, [77]); wrongVendor[5] = 0x1D;
        Assert.Empty(new EarFunFrameCodec(true).Feed(wrongVendor));
        byte[] unsupportedFlags = Reply(true, 0x0306, [77]); unsupportedFlags[2] = 0x40;
        Assert.Empty(new EarFunFrameCodec(true).Feed(unsupportedFlags));
        byte[] shortFrame = [255,4,0,0,0,10,0x83,6];
        Assert.Empty(new EarFunFrameCodec(true).Feed(shortFrame));
        // v3 extended-length/checksummed response, payload 77 and success status.
        byte[] extended = [255,3,3,0,2,0,10,0x83,6,0,77];
        byte xor = 0; foreach (byte b in extended) xor ^= b;
        Assert.Equal(new byte[] { 77 }, Assert.Single(new EarFunFrameCodec(true).Feed([.. extended, xor])).Payload);
        Assert.Empty(new EarFunFrameCodec(true).Feed([.. extended, (byte)(xor ^ 1)]));
        Assert.Single(new EarFunFrameCodec(true).Feed([255,4,2,0xFF,0xFF, .. Reply(true, 0x0306, [25])]));
    }

    private sealed class Device(bool gaia) : IDuplexByteChannel
    {
        private readonly FakeByteChannel _inner = new();
        public string Description => "synthetic EarFun";
        public Dictionary<ushort, byte[]> Values { get; } = gaia ? new() {
            [0x0309] = Encoding.ASCII.GetBytes("TW700_20260805_v1.3.7"), [0x0306] = [82], [0x0307] = [74], [0x0317] = [0x80 | 65],
            [0x034D] = [2], [0x0315] = [0], [0x0313] = [0], [0x0326] = [0], [0x034A] = [0], [0x0339] = [0],
            [0x0333] = [0], [0x033B] = [0], [0x033D] = [0], [0x0348] = [255],
            [0x030B] = [1,1,1,2,2,3,2,4,3,5,3,6,17,7,17,0xE1],
            // Call microphone auto; quality: aptX bit 0 and aptX Adaptive on; touch lock off.
            [0x0331] = [0], [0x032F] = [3], [0x0335] = [0],
        } : new() {
            [0x1010] = Encoding.ASCII.GetBytes("TW230_v1.0.0"), [0x1014] = [80,70], [0x1018] = [50], [0x1016] = [10], [0x1020] = [1],
            [0x1024] = [2], [0x2080] = [1], [0x2030] = [1], [0x2140] = [1], [0x2120] = [5], [0x2050] = [0,0],
            [0x2310] = [1], [0x2300] = [1], [0x2130] = [255], [0x2070] = [1,2,3,4,5,6,7,0xE1],
            [0x2100] = [0], [0x2060] = [0,0],
        };
        private static readonly Dictionary<ushort, ushort> Reads = new() {
            [0x0314]=0x0315, [0x0312]=0x0313, [0x0327]=0x0326, [0x0349]=0x034A, [0x0338]=0x0339, [0x0332]=0x0333,
            [0x033A]=0x033B, [0x033C]=0x033D, [0x0347]=0x0348, [0x0330]=0x0331, [0x032E]=0x032F, [0x0334]=0x0335,
            [0x2085]=0x2080, [0x2035]=0x2030, [0x2145]=0x2140, [0x2125]=0x2120, [0x2055]=0x2050,
            [0x2315]=0x2310, [0x2305]=0x2300, [0x2135]=0x2130, [0x2105]=0x2100, [0x2065]=0x2060,
        };
        // Paired hosts as the list reports them: number, name, partial address (3 bytes GAIA, 4 bytes EF), connected.
        public List<(byte Number, string Name, byte[] Mac, bool Connected)> Hosts { get; } = [];
        // Reads of the list before a connect or disconnect shows up.
        public int HostSwitchReads { get; set; }
        private (byte Number, bool Connected, int Reads)? _hostSwitch;
        // Earbuds that restart to apply dual connection: the link drops right after the write.
        public bool RestartOnDual { get; set; }
        public bool ApplyWrites { get; set; } = true;
        public bool Silence { get; set; }
        public byte Status { get; set; }
        // Air Pro 4+ behaviour assumed for issue 16: writes apply without a reply, and a few reads still report the old value.
        public bool AckWrites { get; set; } = true;
        public int SwitchReads { get; set; }
        private readonly Dictionary<ushort, (byte[] Value, int Reads)> _switching = [];
        public List<byte[]> Written { get; } = [];
        public void Push(ushort command, byte[] payload) => _inner.Receive(Reply(gaia, command, payload));
        public void PushRaw(byte[] bytes) => _inner.Receive(bytes);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            byte[] frame = data.ToArray(); Written.Add(frame);
            int start = gaia ? 8 : 4;
            ushort command = gaia ? (ushort)((frame[6] << 8) | frame[7]) : (ushort)((frame[1] << 8) | frame[2]);
            if (gaia && frame[5] != 10 || Silence) return ValueTask.CompletedTask;
            byte[] payload = frame.AsSpan(start, frame[3]).ToArray();
            if (_switching.TryGetValue(command, out var switching))
            {
                if (switching.Reads == 0) { Values[command] = switching.Value; _switching.Remove(command); }
                else _switching[command] = switching with { Reads = switching.Reads - 1 };
            }
            if (RestartOnDual && command == (gaia ? 0x0327 : 0x2145) && payload is [1 or 2])
            {
                if (AckWrites) _inner.Receive(Reply(gaia, command, [], Status));
                _inner.RemoteClose();
                return ValueTask.CompletedTask;
            }
            if (command == (gaia ? 0x032C : 0x2150))
            {
                if (_hostSwitch is { } pending)
                {
                    if (pending.Reads == 0)
                    {
                        int i = Hosts.FindIndex(h => h.Number == pending.Number);
                        Hosts[i] = Hosts[i] with { Connected = pending.Connected };
                        _hostSwitch = null;
                    }
                    else _hostSwitch = pending with { Reads = pending.Reads - 1 };
                }
                foreach (var host in Hosts)
                {
                    byte[] entry = new byte[26];
                    entry[0] = host.Number;
                    Encoding.UTF8.GetBytes(host.Name).CopyTo(entry, 1);
                    host.Mac.CopyTo(entry, gaia ? 22 : 21);
                    entry[25] = host.Connected ? (byte)1 : (byte)0;
                    _inner.Receive(Reply(gaia, command, entry));
                }
                return ValueTask.CompletedTask;
            }
            if (command is 0x0329 or 0x032A or 0x2165 or 0x2175)
            {
                _hostSwitch = (payload[0], command is 0x0329 or 0x2165, HostSwitchReads);
                if (AckWrites) _inner.Receive(Reply(gaia, command, [], Status));
                return ValueTask.CompletedTask;
            }
            if (Values.TryGetValue(command, out var value)) _inner.Receive(Reply(gaia, command, value));
            else if (Reads.TryGetValue(command, out var read))
            {
                if (ApplyWrites && Status == 0) _switching[read] = (payload, SwitchReads);
                if (AckWrites) _inner.Receive(Reply(gaia, command, [], Status));
            }
            else if (command == (gaia ? 0x030A : 0x2075))
            {
                if (ApplyWrites && Status == 0)
                {
                    if (!gaia) Values[0x2070] = payload;
                    else
                    {
                        int side = payload[0] != 0 ? 0 : 1, offset = side == 0 ? 0 : 2;
                        int gesture = payload[offset] == 17 ? 3 : payload[offset] - 1;
                        Values[0x030B][4 * gesture + 2 * side + 1] = payload[offset + 1];
                    }
                }
                if (AckWrites) _inner.Receive(Reply(gaia, command, [], Status));
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(Device device, string code, Func<bool>? experimental = null, IProtocolTrace? trace = null,
        string deviceId = "earfun-test")
    {
        var profile = new EarFunProfile(EarFunProfiles.Models.Single(m => m.Id == code));
        var identity = new DeviceIdentity(deviceId, profile.DisplayName, new BluetoothAddress(1));
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile.CreateController(device, new(identity, store, experimental ?? (() => true), NullLoggerFactory.Instance, TimeProvider.System, trace)), store);
    }

    private sealed class RecordingTrace : IProtocolTrace
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public void OnFrame(FrameDirection direction, OppoFrame frame, DateTimeOffset timestamp) { }
        public void OnRawReceived(ReadOnlySpan<byte> data, DateTimeOffset timestamp) { }
        public void OnBrandFrame(FrameDirection direction, string summary, DateTimeOffset timestamp)
        {
            lock (_lines) _lines.Add($"{(direction == FrameDirection.Sent ? "TX" : "RX")} {summary}");
        }
    }
    private static async Task Wait(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData(true, "TW700")]
    [InlineData(false, "TW230")]
    public async Task StartupReadsBatteryAndModelControlsWorkWithoutExperimentalFeatures(bool gaia, string model)
    {
        var device = new Device(gaia); var (controller, store) = Create(device, model, () => false); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        await Wait(() => store.Current.KeyFunctions is not null);
        Assert.Equal(gaia ? 82 : 80, store.Current.Battery.Left!.Value.Level);
        Assert.Equal(gaia ? 74 : 70, store.Current.Battery.Right!.Value.Level);
        Assert.Equal(gaia ? 65 : 50, store.Current.Battery.Case!.Value.Level);
        Assert.True(store.Current.Battery.Case.Value.IsCharging);
        Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.True(store.Current.Profile.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, false));
        Assert.Null(store.Current.Equalizer);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        Assert.True((await controller.SetPromptVolumeAsync(25, Ct)).IsSuccess);
        Assert.Equal(25, store.Current.PromptVolume!.Value);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).IsSuccess);
        Assert.True(store.Current.Toggles[DeviceToggle.GamingMode].Value);
        Assert.True((await controller.SetOptionAsync("earfun-autooff", 255, Ct)).IsSuccess);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetOptionAsync("earfun-autooff", 17, Ct)).Status);
    }

    [Fact]
    public async Task WritesNeedFreshReadbackAndRejectedWritesCannotPublishOptimisticValues()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        device.ApplyWrites = false;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl!.Value.Mode);
        device.Status = 5;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
        Assert.False(store.Current.Toggles[DeviceToggle.GamingMode].Value);
        device.Push(0x0315, [0xFF]); device.Push(0x0306, [101]); device.Push(0x0307, [255]);
        await Wait(() => store.Current.Battery.Right is null);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl.Value.Mode);
        Assert.Equal(82, store.Current.Battery.Left!.Value.Level);
    }

    [Fact]
    public async Task Tw700WearFlagsAreWrittenAndReadBack()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 15 }, device.Values[0x034A]);
        Assert.False(store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0 }, device.Values[0x034A]);
    }

    [Fact]
    public async Task UnacknowledgedWritesAreConfirmedByLaterReadbackWithoutDroppingTheSession()
    {
        // Issue 16: the Air Pro 4+ switched ANC, but EncoDeck waited for a reply, reported an error and reconnected.
        var device = new Device(true) { AckWrites = false, SwitchReads = 2 };
        var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        var target = store.Current.KeyFunctions!.Value[0] with { Function = 5 };
        Assert.True((await controller.SetKeyFunctionAsync(target, Ct)).IsSuccess);
        Assert.Equal(target, store.Current.KeyFunctions.Value[0]);
        Assert.False(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task EarbudsThatStopAnsweringReadsStillEndTheSession()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        device.Silence = true;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
        Assert.True(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task TrafficLogShowsSettingValuesAndOnlyHeadersOfUnknownFrames()
    {
        var trace = new RecordingTrace();
        var device = new Device(true); var (controller, store) = Create(device, "TW700", trace: trace); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        device.Push(0x032C, Encoding.ASCII.GetBytes("phone name"));
        await Wait(() => trace.Lines.Any(l => l.Contains("032C", StringComparison.Ordinal)));
        device.PushRaw([255, 4, 0, 3, 0, 0x1D, 0x40, 1, 0x70, 0x68, 0x6F]);
        device.PushRaw([0x70, 0x68, 0x6F, 0x6E, 0x65]);
        await Wait(() => trace.Lines.Count(l => l.Contains("no EarFun frame", StringComparison.Ordinal)) == 2);
        Assert.Contains("TX EARFUN GAIA 0314 02", trace.Lines);
        Assert.Contains("RX EARFUN GAIA 0314 status 00 ", trace.Lines);
        Assert.Contains("RX EARFUN GAIA 0315 status 00 02", trace.Lines);
        Assert.Contains("RX EARFUN GAIA 032C status 00 <10 bytes>", trace.Lines);
        Assert.Contains("RX EARFUN no EarFun frame in 11 bytes, head FF040003001D4001", trace.Lines);
        Assert.Contains("RX EARFUN no EarFun frame in 5 bytes", trace.Lines);
        Assert.DoesNotContain(trace.Lines, l => l.Contains("70686F", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "TW700")]
    [InlineData(false, "TW230")]
    public async Task GestureWritesPreserveAllOtherAssignmentsIncludingUnknownFunctions(bool gaia, string model)
    {
        var device = new Device(gaia); var (controller, store) = Create(device, model); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        var before = store.Current.KeyFunctions!.Value.ToArray();
        var target = before[0] with { Function = 5 };
        Assert.True((await controller.SetKeyFunctionAsync(target, Ct)).IsSuccess);
        Assert.Equal(target, store.Current.KeyFunctions.Value[0]);
        Assert.Equal(before.Skip(1), store.Current.KeyFunctions.Value.Skip(1));
        Assert.Equal(0xE1, store.Current.KeyFunctions.Value[^1].Function);
        int written = device.Written.Count;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(target with { Function = 222 }, Ct)).Status);
        Assert.Equal(written, device.Written.Count);
    }

    [Fact]
    public async Task CancelledTransactionEndsSessionAndLateReportsCannotConfirmAnotherWrite()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        device.Silence = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct); cancel.CancelAfter(50);
        Assert.Equal(CommandStatus.Cancelled, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, cancel.Token)).Status);
        Assert.True(controller.Closed.IsCompleted);
        device.Push(0x0313, [1]);
        Assert.Equal(CommandStatus.NotConnected, (await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
        Assert.False(store.Current.Toggles[DeviceToggle.GamingMode].Value);
    }

    [Fact]
    public void ArtworkLookupUsesPublicModelIdentityAndSeparateCacheKeys()
    {
        var key = new DeviceResourceKey("earfun-tw700", null, null, "my private name", "EarFun Air Pro 4+");
        Assert.True(key.CanDownload); Assert.False(key.UsesOplusId);
        Assert.Equal(("earfun-tw700", "EarFun Air Pro 4+"), OfficialArtworkCatalog.Lookup(key));
        Assert.NotEqual(key.CacheIdentity, (key with { ModelId = "earfun-tw600" }).CacheIdentity);
    }

    [Fact]
    public async Task SilentOptionalSettingsDoNotBlockBasicConnectionOrInventValues()
    {
        var device = new Device(true);
        foreach (ushort command in device.Values.Keys.Where(c => c is not (0x0309 or 0x0306)).ToArray()) device.Values.Remove(command);
        var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.False(controller.Closed.IsCompleted);
        Assert.Equal(82, store.Current.Battery.Left!.Value.Level);
        Assert.NotEmpty(store.Current.Profile!.NoiseControlOptions);
        Assert.Null(store.Current.NoiseControl);
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task FeaturesTheModelLacksAreNeverWritten()
    {
        var device = new Device(true); var (controller, _) = Create(device, "TW101S"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        int before = device.Written.Count;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct)).Status);
        Assert.Equal(before, device.Written.Count);
    }

    [Fact]
    public async Task GaiaValuesWithTrailingBytesAreAccepted()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        device.Push(0x0315, [1, 0]);
        device.Push(0x0307, [55, 0, 0]);
        await Wait(() => store.Current.Battery.Right?.Value.Level == 55);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
    }

    [Fact]
    public void HeadphoneAndVariantOptionsNeverBorrowUnsupportedLayouts()
    {
        var head = EarFunProfiles.Models.Single(m => m.Id == "HP201");
        Assert.Contains(EarFunSettings.NoiseModes(head), p => p.State == new NoiseControlState(NoiseControlMode.NoiseCancellation));
        Assert.DoesNotContain(EarFunSettings.NoiseModes(head), p => p.State.LevelId == "anc1");
        Assert.Equal(6, EarFunGestures.Slots(head).Count);
        var clip2 = EarFunProfiles.Models.Single(m => m.Id == "CW200");
        Assert.Equal(12, EarFunGestures.Slots(clip2).Count);
        Assert.Null(EarFunGestures.Decode(clip2, new byte[8]));
        Assert.Null(EarFunSettings.Option(EarFunProfiles.Models.Single(m => m.Id == "TW700"), EarFunSetting.Sub, 7));
        Assert.Empty(EarFunGestures.Slots(EarFunProfiles.Models.Single(m => m.Id == "HP1000")));
    }

    [Theory]
    [InlineData("TW700", new[] { 0, 4, EarFunSettings.ManualSubMode, 1 }, new[] { 6, 5, 3, 2 })]
    [InlineData("HP1000", new[] { 6, 0, EarFunSettings.ManualSubMode, 1 }, new[] { 5, 4, 3, 2 })]
    public void ManualAncLevelsFoldIntoOneManualSubMode(string id, int[] row, int[] levels)
    {
        // Issue 16: ear adaptive, adaptive, manual, wind reduction; levels 1 (weakest) to 4 sit under manual.
        var choices = EarFunSettings.Option(EarFunProfiles.Models.Single(m => m.Id == id), EarFunSetting.Sub, 2)!.Choices;
        Assert.Equal(row, EarFunSettings.SubModeRow(choices).Select(c => c.Value));
        Assert.Equal("EarFun_Manual", EarFunSettings.SubModeRow(choices)[2].LabelKey);
        Assert.Equal(levels, EarFunSettings.ManualLevels(choices).Select(c => c.Value));
    }

    [Fact]
    public void SubModesWithoutManualLevelsStayFlat()
    {
        var choices = EarFunSettings.Option(EarFunProfiles.Models.Single(m => m.Id == "TW600"), EarFunSetting.Sub, 2)!.Choices;
        Assert.Same(choices, EarFunSettings.SubModeRow(choices));
        Assert.Empty(EarFunSettings.ManualLevels(choices));
    }

    [Fact]
    public async Task EarbudCodecReportsCannotOverrideWindowsCodec()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        store.TryUpdate("earfun-test", s => s with { WindowsCodecLogged = true, WindowsCodec = "LDAC" });
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        device.Push(0x034D, [2]);
        device.Push(0x034D, [6]);
        device.Push(0x0317, [0x80 | 60]);
        await Wait(() => store.Current.Battery.Case?.Value.Level == 60);
        Assert.Equal("LDAC", store.Current.WindowsCodec);
        Assert.DoesNotContain(device.Written, frame => frame[6] == 3 && frame[7] == 0x4D);
    }

    [Theory]
    [InlineData("TW700", new[] { DeviceToggle.Ldac, DeviceToggle.AptXAdaptive, DeviceToggle.AptXLossless })]
    [InlineData("TW500", new[] { DeviceToggle.AptXAdaptive, DeviceToggle.AptXLossless })]
    [InlineData("TW230", new[] { DeviceToggle.Ldac })]
    [InlineData("TW205", new DeviceToggle[0])]
    [InlineData("HP220", new DeviceToggle[0])]
    public void CodecSwitchesFollowTheModel(string code, DeviceToggle[] codecs)
    {
        var profile = new EarFunProfile(EarFunProfiles.Models.Single(m => m.Id == code));
        Assert.Equal(codecs.Order(), profile.Toggles.Where(t => t is DeviceToggle.Ldac or DeviceToggle.AptXAdaptive or DeviceToggle.AptXLossless).Order());
        Assert.Equal(codecs.Contains(DeviceToggle.Ldac), profile.Capabilities.IsAvailable(DeviceCapability.Ldac, false));
    }

    [Fact]
    public async Task MicTouchLockAndCodecBitsAreReadAndConfirmedByReadback()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        await Wait(() => store.Current.Options.ContainsKey("earfun-mic") && store.Current.Options.ContainsKey("earfun-touchlock")
            && store.Current.Toggles.ContainsKey(DeviceToggle.AptXLossless));
        Assert.True(store.Current.Toggles[DeviceToggle.AptXAdaptive].Value);
        Assert.False(store.Current.Toggles[DeviceToggle.Ldac].Value);
        Assert.False(store.Current.Toggles[DeviceToggle.AptXLossless].Value);
        Assert.True((await controller.SetOptionAsync("earfun-mic", 2, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 2 }, device.Values[0x0331]);
        Assert.True((await controller.SetOptionAsync("earfun-touchlock", 3, Ct)).IsSuccess);
        Assert.Equal(3, store.Current.Options["earfun-touchlock"].Value.Value);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetOptionAsync("earfun-mic", 3, Ct)).Status);
        // The other bits, including aptX bit 0 that EncoDeck never offers, are kept.
        Assert.True((await controller.SetToggleAsync(DeviceToggle.AptXLossless, true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 3 | 16 }, device.Values[0x032F]);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.AptXAdaptive, false, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1 | 16 }, device.Values[0x032F]);
        Assert.False(store.Current.Toggles[DeviceToggle.AptXAdaptive].Value);
    }

    [Fact]
    public async Task EfTouchLockUsesSideFlagsAndLdacItsOwnBit()
    {
        var device = new Device(false); var (controller, store) = Create(device, "TW230"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        await Wait(() => store.Current.Options.ContainsKey("earfun-touchlock") && store.Current.Toggles.ContainsKey(DeviceToggle.Ldac));
        Assert.False(store.Current.Options.ContainsKey("earfun-mic"));
        Assert.True((await controller.SetOptionAsync("earfun-touchlock", 2, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 1, 0 }, device.Values[0x2060]);
        device.Values[0x2140] = [1];
        Assert.True((await controller.SetToggleAsync(DeviceToggle.Ldac, true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 4 }, device.Values[0x2100]);
    }

    [Fact]
    public async Task LdacAndDualConnectionTurnEachOtherOff()
    {
        var device = new Device(true); device.Values[0x032F] = [8];
        var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.Toggles.ContainsKey(DeviceToggle.Ldac));
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0 }, device.Values[0x032F]);
        Assert.Equal(new byte[] { 1 }, device.Values[0x0326]);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.Ldac, true, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0 }, device.Values[0x0326]);
        Assert.Equal(new byte[] { 8 }, device.Values[0x032F]);
        Assert.False(store.Current.Toggles[DeviceToggle.DualDeviceConnection].Value);
        Assert.True(store.Current.Toggles[DeviceToggle.Ldac].Value);
    }

    [Fact]
    public async Task DualConnectionThatRestartsTheEarbudsIsNotAnError()
    {
        var device = new Device(true) { RestartOnDual = true, AckWrites = false };
        var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.Toggles.ContainsKey(DeviceToggle.DualDeviceConnection));
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct)).IsSuccess);
        Assert.True(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task PairedHostsAreListedAndConnectedOnlyAfterTheListConfirms()
    {
        var device = new Device(true) { HostSwitchReads = 1 };
        device.Hosts.AddRange([((byte)1, "Desk PC", [0xCC, 0xBB, 0xAA], true), ((byte)2, "Phone", [1, 2, 3], false), ((byte)3, "Tablet", [4, 5, 6], false)]);
        var (controller, store) = Create(device, "TW700", deviceId: "Bluetooth#Bluetooth00:11:22:aa:bb:cc-01:02:03:04:05:06");
        await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.SourceDevices?.Value.Count == 3);
        var hosts = store.Current.SourceDevices!.Value;
        // The partial address matches this PC's adapter in reversed byte order.
        Assert.True(hosts[0].IsThisDevice);
        Assert.False(hosts[1].IsThisDevice);
        Assert.Equal("Phone", hosts[1].Name);
        Assert.NotEqual(6, hosts[1].Address.Length);

        Assert.True((await controller.MultiConnectAsync(MultiConnectOperation.Connect, hosts[1].Address, Ct)).IsSuccess);
        Assert.True(store.Current.SourceDevices!.Value.Single(d => d.Name == "Phone").IsConnected);
        int written = device.Written.Count(f => f[7] == 0x29);
        Assert.Equal(CommandStatus.Rejected, (await controller.MultiConnectAsync(MultiConnectOperation.Connect, hosts[2].Address, Ct)).Status);
        Assert.Equal(written, device.Written.Count(f => f[7] == 0x29));
        Assert.Equal(CommandStatus.Rejected, (await controller.MultiConnectAsync(MultiConnectOperation.Disconnect, hosts[0].Address, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.MultiConnectAsync(MultiConnectOperation.Remove, hosts[2].Address, Ct)).Status);
        device.HostSwitchReads = 0;
        Assert.True((await controller.MultiConnectAsync(MultiConnectOperation.Disconnect, hosts[1].Address, Ct)).IsSuccess);
        Assert.False(store.Current.SourceDevices!.Value.Single(d => d.Name == "Phone").IsConnected);
        Assert.False(controller.Closed.IsCompleted);
    }

    [Fact]
    public void EqFramesMatchEarKitLayouts()
    {
        var tw700 = EarFunProfiles.Models.Single(m => m.Id == "TW700");
        var spec = EarFunEqualizer.Spec(tw700);
        Assert.Equal((-12, 12, 2), (spec.MinGain, spec.MaxGain, spec.GainScale));
        Assert.Equal([31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000], EarFunEqualizer.Frequencies(tw700));
        // Rock: EarKit levels 4,5,2,... are +2, +2.5, +1 dB (half-dB steps 4, 5, 2).
        var rock = spec.Presets.Single(p => p.LabelKey == "EarFunEq_Rock");
        Assert.Equal([4, 5, 2, 0, 0, 1, 2, 3, 3, 2], rock.Gains);
        // Level 9 is +5 dB.
        Assert.Equal([4, 10, 0, 0, 0, 0, 0, 0, 0, 0], spec.Presets.Single(p => p.LabelKey == "EarFunEq_BassBoost" && p.Variant == 3).Gains);
        var frames = EarFunEqualizer.Encode(tw700, [4, -6, 0, 0, 0, 0, 0, 0, 0, 12]);
        Assert.Equal(10, frames.Count);
        // Qualcomm vendor 001D command 0E01: band A1, master FE98, 31.5 Hz x3 = 94, +2 dB x60 = 120, Q 0.7 x4096.
        Assert.Equal(Convert.FromHexString("FF040009001D0E01A1FE98005E00780B33"), frames[0]);
        Assert.Equal(Convert.FromHexString("FF040009001D0E01A2FE9800BDFF4C0B33"), frames[1]);
        Assert.Equal(Convert.FromHexString("FF040009001D0E01AAFE98BB8001680B33"), frames[9]);

        // Six bands in whole dB, then EarKit's four fixed shaping bands.
        var free2s = EarFunProfiles.Models.Single(m => m.Id == "TW101S");
        Assert.Equal((-10, 10, 1), (EarFunEqualizer.Spec(free2s).MinGain, EarFunEqualizer.Spec(free2s).MaxGain, EarFunEqualizer.Spec(free2s).GainScale));
        var six = EarFunEqualizer.Encode(free2s, [8, 0, 0, 0, 0, 0]);
        Assert.Equal(10, six.Count);
        Assert.Equal(Convert.FromHexString("FF040009001D0E01A1FE9800BD01E00B33"), six[0]);
        Assert.Equal(Convert.FromHexString("FF040009001D0E01A7FE980063010E0800"), six[6]);

        // EF: count, band, master (by the loudest band), frequency, gain, Q; Air Pro 4i's master for +5 dB is -13 dB.
        var tw230 = EarFunProfiles.Models.Single(m => m.Id == "TW230");
        var ef = EarFunEqualizer.Encode(tw230, [10, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(Convert.FromHexString("EF20950A0A01FCF4005E012C0B33CEFE"), ef[0]);
        Assert.True(EarFunEqualizer.Combined(tw230));
        // Between EarKit's slider positions (+4.5 dB) the master follows the next position up (+5 dB).
        Assert.Equal(EarFunEqualizer.Encode(tw230, [10, 0, 0, 0, 0, 0, 0, 0, 0, 0])[0][4..8], EarFunEqualizer.Encode(tw230, [9, 0, 0, 0, 0, 0, 0, 0, 0, 0])[0][4..8]);
        var hp220 = EarFunProfiles.Models.Single(m => m.Id == "HP220");
        // (int)(-8 - 3.5) * 60 = -660.
        Assert.Equal(new byte[] { 0xFD, 0x6C }, EarFunEqualizer.Encode(hp220, [7, 0, 0, 0, 0, 0, 0, 0, 0, 0])[0][6..8]);
    }

    [Theory]
    [InlineData(true, "TW700")]
    [InlineData(false, "TW230")]
    public async Task EqCurveIsSentWithoutReadbackAndKeptAsSent(bool gaia, string model)
    {
        var device = new Device(gaia); var (controller, store) = Create(device, model); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var flat = Assert.Single(store.Current.CustomEqualizers!.Value);
        Assert.True(flat.IsSelected);
        Assert.All(flat.Bands, b => Assert.Equal(0, b.Gain));
        int before = device.Written.Count;
        var curve = flat with { Bands = flat.Bands.Select((b, i) => b with { Gain = i == 0 ? 6 : 0 }).ToArray() };
        Assert.True((await controller.SaveCustomEqualizerAsync(curve, Ct)).IsSuccess);
        // TW230 takes the whole curve in one write.
        Assert.Equal(gaia ? 10 : 1, device.Written.Count - before);
        Assert.Equal(gaia ? 0x1D : 0x95, gaia ? device.Written[^1][5] : device.Written[^1][2]);
        Assert.Equal(StateSource.Sent, store.Current.CustomEqualizers!.Source);
        Assert.Equal(6, store.Current.CustomEqualizers.Value[0].Bands[0].Gain);
        before = device.Written.Count;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SaveCustomEqualizerAsync(curve with { Bands = curve.Bands.Select(b => b with { Gain = 13 }).ToArray() }, Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SaveCustomEqualizerAsync(curve with { Bands = curve.Bands.Skip(1).ToArray() }, Ct)).Status);
        Assert.Equal(before, device.Written.Count);
        Assert.True((await controller.SelectCustomEqualizerAsync(EarFunEqualizer.CurveId, Ct)).IsSuccess);
        Assert.True(device.Written.Count > before);
        Assert.False(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task EqCurveLastSentSurvivesReconnect()
    {
        var device = new Device(true); var (controller, store) = Create(device, "TW700");
        await using (controller)
        {
            Assert.True(await controller.InitializeAsync(Ct));
            var flat = store.Current.CustomEqualizers!.Value[0];
            Assert.True((await controller.SaveCustomEqualizerAsync(flat with { Bands = flat.Bands.Select(b => b with { Gain = -4 }).ToArray() }, Ct)).IsSuccess);
        }
        var profile = (EarFunProfile)store.Current.Profile!;
        var again = profile.CreateController(new Device(true), new(store.Current.Device!, store, () => true, NullLoggerFactory.Instance, TimeProvider.System));
        await using var owner = again;
        Assert.True(await again.InitializeAsync(Ct));
        Assert.All(store.Current.CustomEqualizers!.Value[0].Bands, b => Assert.Equal(-4, b.Gain));
    }

    [Theory]
    [InlineData(true, "TW700", "0336", "")]
    [InlineData(false, "TW230", "1000", "AFA5")]
    public async Task FactoryResetSendsEarKitsCommand(bool gaia, string model, string command, string payload)
    {
        var device = new Device(gaia); var (controller, _) = Create(device, model); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True((await controller.FactoryResetAsync(Ct)).IsSuccess);
        byte[] frame = device.Written[^1];
        Assert.Equal(command, Convert.ToHexString(gaia ? frame[6..8] : frame[1..3]));
        Assert.Equal(payload, Convert.ToHexString(gaia ? frame[8..] : frame[4..^2]));
    }

    [Fact]
    public async Task Tw700ReportsEarKitReadsAreUnderstood()
    {
        // Issue 16 (beta.6): in-ear detection stayed unavailable, gestures and auto power-off never appeared.
        var device = new Device(true);
        device.Values[0x034A] = [0x30];
        device.Values[0x0348] = [0];
        // Gesture types other than the ones EncoDeck writes, and a trailing byte.
        device.Values[0x030B] = [1, 1, 1, 2, 2, 3, 2, 4, 3, 5, 3, 6, 4, 7, 4, 8, 0];
        var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        await Wait(() => store.Current.KeyFunctions is not null && store.Current.Options.ContainsKey("earfun-autooff")
            && store.Current.Toggles.ContainsKey(DeviceToggle.InEarDetection));
        Assert.True(store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        Assert.Equal(255, store.Current.Options["earfun-autooff"].Value.Value);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], store.Current.KeyFunctions!.Value.Select(k => k.Function));
        device.Push(0x034A, [0x1F]);
        await Wait(() => !store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        // Firmware that reports "never" as 0 confirms EarKit's 255.
        Assert.Equal(255, EarFunSettings.Option(EarFunProfiles.Models.Single(m => m.Id == "TW700"), EarFunSetting.AutoOff, 0)!.Value);
        device.Values[0x0348] = [3];
        await controller.RefreshAsync(Ct);
        Assert.Equal(3, store.Current.Options["earfun-autooff"].Value.Value);
        Assert.True((await controller.SetOptionAsync("earfun-autooff", 255, Ct)).IsSuccess);
    }

    [Fact]
    public async Task UnansweredStartupReadsAreAskedAgainOnRefresh()
    {
        var device = new Device(true);
        byte[] autoOff = device.Values[0x0348];
        device.Values.Remove(0x0348);
        var (controller, store) = Create(device, "TW700"); await using var owner = controller;
        Assert.True(await controller.InitializeAsync(Ct)); await Wait(() => store.Current.KeyFunctions is not null);
        Assert.False(store.Current.Options.ContainsKey("earfun-autooff"));
        device.Values[0x0348] = autoOff;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        await Wait(() => store.Current.Options.ContainsKey("earfun-autooff"));
        // Settings that answered are not asked twice.
        int reads = device.Written.Count(f => f[7] == 0x48);
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(reads + 1, device.Written.Count(f => f[7] == 0x48));
    }
}
