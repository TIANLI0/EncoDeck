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
        } : new() {
            [0x1010] = Encoding.ASCII.GetBytes("TW230_v1.0.0"), [0x1014] = [80,70], [0x1018] = [50], [0x1016] = [10], [0x1020] = [1],
            [0x1024] = [2], [0x2080] = [1], [0x2030] = [1], [0x2140] = [1], [0x2120] = [5], [0x2050] = [0,0],
            [0x2310] = [1], [0x2300] = [1], [0x2130] = [255], [0x2070] = [1,2,3,4,5,6,7,0xE1],
        };
        private static readonly Dictionary<ushort, ushort> Reads = new() {
            [0x0314]=0x0315, [0x0312]=0x0313, [0x0327]=0x0326, [0x0349]=0x034A, [0x0338]=0x0339, [0x0332]=0x0333,
            [0x033A]=0x033B, [0x033C]=0x033D, [0x0347]=0x0348,
            [0x2085]=0x2080, [0x2035]=0x2030, [0x2145]=0x2140, [0x2125]=0x2120, [0x2055]=0x2050,
            [0x2315]=0x2310, [0x2305]=0x2300, [0x2135]=0x2130,
        };
        public bool ApplyWrites { get; set; } = true;
        public bool Silence { get; set; }
        public byte Status { get; set; }
        public List<byte[]> Written { get; } = [];
        public void Push(ushort command, byte[] payload) => _inner.Receive(Reply(gaia, command, payload));
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            byte[] frame = data.ToArray(); Written.Add(frame);
            int start = gaia ? 8 : 4;
            ushort command = gaia ? (ushort)((frame[6] << 8) | frame[7]) : (ushort)((frame[1] << 8) | frame[2]);
            if (gaia && frame[5] != 10 || Silence) return ValueTask.CompletedTask;
            byte[] payload = frame.AsSpan(start, frame[3]).ToArray();
            if (Values.TryGetValue(command, out var value)) _inner.Receive(Reply(gaia, command, value));
            else if (Reads.TryGetValue(command, out var read))
            {
                if (ApplyWrites && Status == 0) Values[read] = payload;
                _inner.Receive(Reply(gaia, command, [], Status));
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
                _inner.Receive(Reply(gaia, command, [], Status));
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(Device device, string code, Func<bool>? experimental = null)
    {
        var profile = new EarFunProfile(EarFunProfiles.Models.Single(m => m.Id == code));
        var identity = new DeviceIdentity("earfun-test", profile.DisplayName, new BluetoothAddress(1));
        var store = new DeviceStateStore(); store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile.CreateController(device, new(identity, store, experimental ?? (() => true), NullLoggerFactory.Instance, TimeProvider.System)), store);
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
}
