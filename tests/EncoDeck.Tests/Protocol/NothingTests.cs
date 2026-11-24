// SPDX-License-Identifier: GPL-3.0-only
// Test vectors ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6,
// GPL-3.0-or-later. Wire literals marked as captures come from the public sources HeyBuds attributes
// (omarchy-headphones CMF Buds 2 capture, openNothing golden event, swift-nothing-ear fixtures).
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Nothing;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public class NothingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static NothingModel EarA => Model(NothingModelKind.EarA);
    private static NothingModel Headphone1 => Model(NothingModelKind.Headphone1);
    private static NothingModel CmfBuds2 => Model(NothingModelKind.CmfBuds2);
    private static NothingModel CmfBudsPro2 => Model(NothingModelKind.CmfBudsPro2);
    private static NothingModel Model(NothingModelKind kind) => NothingModel.All.Single(m => m.Kind == kind);

    /// <summary>Synthetic receive builder with its own CRC loop, independent of the codec under test.</summary>
    private static byte[] Rx(ushort command, byte[] payload, byte sequence = 9, bool payloadCrc = false, bool evt = false)
    {
        var bytes = new List<byte> { 0x55, evt ? (byte)0 : (byte)0x60, 1, (byte)command, (byte)(command >> 8), (byte)payload.Length, (byte)(payload.Length >> 8), sequence };
        bytes.AddRange(payload);
        if (!evt)
        {
            int crc = 65535;
            foreach (byte b in payloadCrc ? payload : bytes.ToArray())
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    int remainder = crc % 2;
                    crc /= 2;
                    if (remainder == 1) crc ^= 40961;
                }
            }
            bytes.Add((byte)(crc % 256));
            bytes.Add((byte)(crc / 256));
        }
        return [.. bytes];
    }

    private static void AssertFrames(IReadOnlyList<NothingFrame> actual, params (ushort Command, byte Sequence, byte[] Payload)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Command, actual[i].Command);
            Assert.Equal(expected[i].Sequence, actual[i].Sequence);
            Assert.Equal(expected[i].Payload, actual[i].Payload);
        }
    }

    [Fact]
    public void ExactProfilesNeverMatchOtherGenerationsOrSuffixes()
    {
        Assert.Equal(new[] { "nothing-ear-a", "nothing-headphone-1", "nothing-cmf-buds-2", "nothing-cmf-buds-pro-2" }, NothingModel.All.Select(m => m.ModelId));
        Assert.Equal(new[] { "B162", "B170", "B179", "B172" }, NothingModel.All.Select(m => m.ModelCode));
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        foreach (var model in NothingModel.All)
        {
            foreach (string alias in model.Aliases)
            {
                Assert.Same(model, NothingModel.Named(alias));
                Assert.Same(model, NothingModel.Named($" {alias.ToUpperInvariant()}\n"));
                Assert.Null(NothingModel.Named(alias + " Pro"));
                var profile = Assert.IsType<NothingProfile>(registry.Match(new("dev", alias, new BluetoothAddress(0x112233445566))));
                Assert.Equal(model.ModelId, profile.ModelId);
                Assert.Equal("nothing", profile.Brand);
                Assert.Equal(new[] { NothingModel.ServiceId }, profile.ControlServiceUuids);
            }
        }
        Assert.Equal(new Guid("AEAC4A03-DFF5-498F-843A-34487CF133EB"), NothingModel.ServiceId);
        foreach (string unknown in new[] { "Nothing", "Ear", "B162", "Ear (1)", "Nothing Ear (2024)", "Nothing Ear (a) 2",
                     "Nothing Headphone (1) Pro", "CMF Buds 2a", "CMF Buds 2 Plus", "CMF Buds Pro",
                     "my CMF Buds Pro 2", "CMF Buds Pro 2 (1234)", "CMF-Buds-2" })
        {
            Assert.Null(NothingModel.Named(unknown));
            Assert.IsNotType<NothingProfile>(registry.Match(new("dev", unknown, new BluetoothAddress(0x112233445566))));
        }
        Assert.Equal(DeviceForm.Headphones, new NothingProfile(Headphone1).Form);
        Assert.Equal(DeviceForm.Earbuds, new NothingProfile(CmfBudsPro2).Form);
    }

    [Fact]
    public void RequestDescriptorsAreModelSpecificAndWritesHaveNoResponse()
    {
        foreach (var model in NothingModel.All)
        {
            Assert.Equal((ushort)0xC007, model.BatteryRead.Command);
            Assert.Equal((ushort)0x4007, model.BatteryRead.ResponseCommand);
            Assert.Equal((ushort)0x4042, model.FirmwareRead.ResponseCommand);
            Assert.Equal((ushort)0x401E, model.NoiseRead.ResponseCommand);
        }
        var write = NothingModel.NoiseWrite(7)!;
        Assert.Equal((ushort)0xF00F, write.Command);
        Assert.Equal(new byte[] { 1, 7, 0 }, write.Payload);
        Assert.Null(write.ResponseCommand);
        Assert.False(write.Matches(new(0x700F, 9, [0]), 9));
        Assert.Null(NothingModel.NoiseWrite(6));
        Assert.Equal((ushort)0xC006, EarA.IdentityRead.Command);
        Assert.Equal((ushort)0x401C, CmfBudsPro2.IdentityRead.ResponseCommand);
        Assert.Equal(new byte[] { 3 }, CmfBudsPro2.NoiseRead.Payload);
        Assert.Empty(CmfBuds2.NoiseRead.Payload);
        Assert.Equal((ushort)0xC01F, EarA.EqRead.Command);
        Assert.Equal((ushort)0xC050, CmfBudsPro2.EqRead.Command);
        Assert.Equal((ushort)0xC050, CmfBuds2.EqRead.Command);
        Assert.True(EarA.BatteryRead.Matches(new(0x4007, 8, []), 8));
        Assert.False(EarA.BatteryRead.Matches(new(0x4007, 7, []), 8));
        Assert.False(EarA.BatteryRead.Matches(new(0xE001, 8, []), 8));
    }

    [Fact]
    public void EncoderMatchesFixedWireFactsAndLengthBounds()
    {
        // swift-nothing-ear battery request fixture; openNothing captured ANC request.
        Assert.Equal(new byte[] { 0x55, 0x60, 1, 7, 0xC0, 0, 0, 1, 0xAC, 0xDF }, NothingFrameCodec.Encode(EarA.BatteryRead, 1));
        Assert.Equal(new byte[] { 0x55, 0x60, 1, 0x1E, 0xC0, 1, 0, 13, 3, 0x99, 0x89 }, NothingFrameCodec.Encode(CmfBudsPro2.NoiseRead, 13));
        Assert.Equal(new byte[] { 0x55, 0x60, 1, 0x0F, 0xF0, 3, 0, 0xCB, 1, 7, 0, 0xC5, 0xAF }, NothingFrameCodec.Encode(NothingModel.NoiseWrite(7)!, 0xCB));
        Assert.Equal(0x4B37, NothingFrameCodec.Checksum("123456789"u8));
        var maximum = NothingFrameCodec.Encode(0xC006, 1, new byte[1024]);
        Assert.Equal(1034, maximum.Length);
        Assert.Equal(0, maximum[5]);
        Assert.Equal(4, maximum[6]);
        Assert.Throws<ArgumentOutOfRangeException>(() => NothingFrameCodec.Encode(0xC006, 1, new byte[1025]));
        Assert.Throws<ArgumentOutOfRangeException>(() => NothingFrameCodec.Encode(0xC006, 1, new byte[1024], maxPayload: 10));
    }

    [Fact]
    public void FullFrameCrcReplaysCmfBuds2CaptureAtEverySplit()
    {
        // omarchy-headphones docs/captures/nothing-cmf-buds-2.txt battery / ANC replies (non-identifying).
        byte[] battery = [0x55, 0x60, 1, 7, 0x40, 5, 0, 1, 2, 2, 100, 3, 100, 0x32, 0x89];
        byte[] noise = [0x55, 0x60, 1, 0x1E, 0x40, 6, 0, 1, 1, 5, 0, 2, 1, 0, 0xB6, 0x2A];
        byte[] stream = [.. battery, .. noise];
        for (int split = 0; split <= stream.Length; split++)
        {
            var decoder = new NothingFrameDecoder(CmfBuds2);
            var frames = decoder.Feed(stream.AsSpan(0, split)).Concat(decoder.Feed(stream.AsSpan(split))).ToList();
            AssertFrames(frames, (0x4007, 1, [2, 2, 100, 3, 100]), (0x401E, 1, [1, 5, 0, 2, 1, 0]));
            Assert.Equal(0, decoder.BufferedCount);
        }
        var bytewise = new NothingFrameDecoder(CmfBuds2);
        Assert.Equal(2, stream.SelectMany(b => bytewise.Feed([b])).Count());
        Assert.Equal((byte)5, CmfBuds2.ParseNoise([1, 5, 0, 2, 1, 0]));
    }

    [Fact]
    public void DirectionalCrcDoesNotTryAnotherRegionOrTrustEchoedRequests()
    {
        byte[] payload = [2, 2, 71, 3, 82];
        byte[] full = Rx(0x4007, payload), partial = Rx(0x4007, payload, payloadCrc: true);
        foreach (var model in new[] { EarA, Headphone1, CmfBuds2 })
        {
            var decoder = new NothingFrameDecoder(model);
            Assert.Empty(decoder.Feed(partial));
            AssertFrames(decoder.Feed(full), (0x4007, 9, payload));
        }
        var b172 = new NothingFrameDecoder(CmfBudsPro2);
        Assert.Empty(b172.Feed(full));
        AssertFrames(b172.Feed(partial), (0x4007, 9, payload));
        Assert.Empty(b172.Feed(NothingFrameCodec.Encode(CmfBudsPro2.BatteryRead, 9)));
        Assert.Empty(b172.Feed(Rx(0x401F, [1], payloadCrc: true))); // B172 uses 4050, not 401F.
    }

    [Fact]
    public void CrcFreeEventsBelongToB172OnlyAndCannotSatisfyReads()
    {
        // openNothing tests/protocol/test_golden.py: B172 ANC event.
        byte[] evt = [0x55, 0, 1, 3, 0xE0, 3, 0, 0, 1, 7, 0];
        foreach (var model in new[] { EarA, Headphone1, CmfBuds2 }) Assert.Empty(new NothingFrameDecoder(model).Feed(evt));
        var decoder = new NothingFrameDecoder(CmfBudsPro2);
        var frames = decoder.Feed(evt);
        AssertFrames(frames, (0xE003, 0, [1, 7, 0]));
        Assert.False(CmfBudsPro2.NoiseRead.Matches(frames[0], 1));
        Assert.Empty(decoder.Feed(Rx(0x401E, [1, 7, 0], sequence: 0, evt: true)));
        Assert.Empty(decoder.Feed(Rx(0xE003, [1, 7, 0], sequence: 1, evt: true)));
        Assert.Empty(decoder.Feed(Rx(0xE002, [0], sequence: 0, evt: true)));
    }

    [Fact]
    public void CorruptionOversizedLengthsAndUnknownControlsRecoverBoundedly()
    {
        byte[] valid = Rx(0x4007, [1, 2, 0]);
        byte[] corrupt = [.. valid];
        corrupt[10] ^= 1;
        byte[][] bad =
        [
            corrupt, [0x55, 0x60, 1, 7, 0x40, 0xFF, 0xFF, 1], [0x55, 0x61, 1, 7, 0x40, 0, 0, 1], [0x55, 0x60, 2, 7, 0x40, 0, 0, 1],
            Enumerable.Repeat((byte)0x55, 20_000).ToArray(), new byte[20_000],
        ];
        foreach (var data in bad)
        {
            var decoder = new NothingFrameDecoder(EarA);
            Assert.Empty(decoder.Feed(data));
            Assert.True(decoder.BufferedCount <= 1034);
            AssertFrames(decoder.Feed(valid), (0x4007, 9, [1, 2, 0]));
        }
        var truncated = new NothingFrameDecoder(EarA);
        Assert.Empty(truncated.Feed(valid.AsSpan(0, valid.Length - 1))); // the CRC is mandatory
        truncated.Reset();
        Assert.Equal(0, truncated.BufferedCount);
        Assert.Single(truncated.Feed(valid));
        byte[] longReply = Rx(0x4042, Enumerable.Repeat((byte)49, 256).ToArray());
        Assert.Equal(256, truncated.Feed(longReply).Single().Payload.Length); // 16-bit little-endian length
        var bounded = new NothingFrameDecoder(EarA, maxPayload: 16);
        Assert.Empty(bounded.Feed(longReply));
        Assert.True(bounded.BufferedCount <= 26);
    }

    [Fact]
    public void BatteryDistinguishesZeroUnknownAbsentAndMalformed()
    {
        var reading = EarA.ParseBattery([3, 2, 0, 3, 0xE4, 4, 0xFF])!;
        Assert.Equal(new BatteryReading(0, false), reading[BatteryComponent.Left]);
        Assert.Equal(new BatteryReading(100, true), reading[BatteryComponent.Right]);
        Assert.False(reading.ContainsKey(BatteryComponent.Case));
        Assert.Empty(EarA.ParseBattery([0])!);
        Assert.Empty(EarA.ParseBattery([1, 4, 0x7F])!);
        foreach (byte[] malformed in new byte[][] { [], [1], [1, 2], [1, 2, 50, 0], [2, 2, 1, 2, 2], [1, 6, 70], [4, 2, 1, 3, 2, 4, 3, 4, 4] })
            Assert.Null(EarA.ParseBattery(malformed));
        Assert.Equal(88, Headphone1.ParseBattery([0, 0, 0xD8])![BatteryComponent.Left].Level);
        Assert.Equal(0, Headphone1.ParseBattery([1, 6, 0])![BatteryComponent.Left].Level);
        Assert.Empty(Headphone1.ParseBattery([0, 0, 0xFF])!);
        Assert.Null(Headphone1.ParseBattery([1, 2, 50]));
        Assert.Null(Headphone1.ParseBattery([0, 0, 50, 0]));
        foreach (byte caseId in new byte[] { 4, 5 })
        {
            // B172 case id is ambiguous between sources: keep the buds, never guess the case.
            var b172 = CmfBudsPro2.ParseBattery([3, 2, 65, 3, 70, caseId, 90])!;
            Assert.Equal(2, b172.Count);
            Assert.Equal(65, b172[BatteryComponent.Left].Level);
            Assert.Equal(70, b172[BatteryComponent.Right].Level);
            Assert.Null(CmfBudsPro2.ParseBattery([2, caseId, 1, caseId, 2]));
        }
        Assert.Null(CmfBudsPro2.ParseBattery([1, 6, 50]));
    }

    [Fact]
    public void NoiseUsesModeGroupRatherThanAncLevelAndRejectsWrongLayouts()
    {
        Assert.Equal((byte)5, CmfBudsPro2.ParseNoise([2, 1, 0, 1, 5, 0]));
        Assert.Equal((byte)7, CmfBuds2.ParseNoise([1, 7, 0, 2, 1, 0]));
        Assert.Equal((byte)7, EarA.ParseNoise([1, 7, 0]));
        Assert.Equal((byte)1, Headphone1.ParseNoise([0, 1]));
        Assert.Null(Headphone1.ParseNoise([1, 1, 0]));
        Assert.Null(CmfBudsPro2.ParseNoise([0, 1]));
        foreach (byte value in new byte[] { 1, 2, 3, 4, 5, 7 })
        {
            Assert.Equal(value, EarA.ParseNoise([0, value]));
            Assert.Equal(new byte[] { 1, value, 0 }, NothingModel.NoiseWrite(value)!.Payload);
        }
        foreach (byte[] malformed in new byte[][] { [], [1], [0, 0], [0, 6], [1, 5, 1], [1, 5, 0, 1, 7, 0], [3, 5, 0], [2, 5, 0], [1, 5, 0, 0] })
            Assert.Null(CmfBudsPro2.ParseNoise(malformed));
    }

    [Fact]
    public void PresetsAndAdvancedGuardExposeNoCustomWrites()
    {
        foreach (var model in NothingModel.All)
        {
            foreach (byte id in model.PresetIds)
            {
                Assert.Equal(id, model.ParseEq([id]));
                Assert.Equal(new byte[] { id, 0 }, model.EqWrite(id)!.Payload);
                Assert.NotNull(model.PresetLabel(id));
            }
            foreach (byte id in new byte[] { 6, 7, 8, 0xFF })
            {
                Assert.Null(model.ParseEq([id]));
                Assert.Null(model.EqWrite(id));
            }
            Assert.Null(model.ParseEq([]));
            Assert.Null(model.ParseEq([0, 1]));
        }
        Assert.Equal(new string?[] { "Balanced", "Voice", "More Treble", "More Bass" }, EarA.PresetIds.Select(id => EarA.PresetLabel(id)));
        Assert.Equal(new string?[] { "Balanced", "Rock", "Electronic", "Pop", "Enhance Vocals", "Classical" }, CmfBuds2.PresetIds.Select(id => CmfBuds2.PresetLabel(id)));
        Assert.Equal((ushort)0xF010, EarA.EqWrite(3)!.Command);
        Assert.Equal((ushort)0xF01D, CmfBuds2.EqWrite(3)!.Command);
        Assert.Equal((ushort)0x404C, Headphone1.AdvancedEqRead!.ResponseCommand);
        Assert.False(Headphone1.ParseAdvancedEq([0]));
        Assert.True(Headphone1.ParseAdvancedEq([1]));
        foreach (byte[] malformed in new byte[][] { [], [2], [0, 0], [1, 0] }) Assert.Null(Headphone1.ParseAdvancedEq(malformed));
        Assert.Null(EarA.AdvancedEqRead);
        Assert.Null(EarA.ParseAdvancedEq([0]));
        var profile = new NothingProfile(CmfBuds2, eqConfirmed: true);
        Assert.Equal(new[] { "Nothing_Eq_Balanced", "Nothing_Eq_Rock", "Nothing_Eq_Electronic", "Nothing_Eq_Pop", "Nothing_Eq_EnhanceVocals", "Nothing_Eq_Classical" },
            profile.EqualizerPresets.Select(p => p.ResourceKey));
        Assert.Equal(new[] { "Nothing_Eq_Balanced", "Nothing_Eq_Voice", "Nothing_Eq_MoreTreble", "Nothing_Eq_MoreBass" },
            new NothingProfile(EarA, eqConfirmed: true).EqualizerPresets.Select(p => p.ResourceKey));
        Assert.Empty(new NothingProfile(CmfBuds2).EqualizerPresets);
        Assert.Empty(new NothingProfile(CmfBuds2).NoiseControlOptions);
    }

    [Fact]
    public void FirmwareGateIsNumericStrictAndNeverAssumesMissingVersion()
    {
        foreach (string version in new[] { "1.0.1.50", "1.0.1.100", "1.0.2.0", "2.0.0.0" }) Assert.True(CmfBuds2.AllowsEq(version));
        foreach (string? version in new[] { null, "", "1.0.1.9", "1.0.1.49", "1.0.1", "1.0.1.50-beta", "1.0.1.50.1", "+1.0.1.50", "1.0.1.65536" })
            Assert.False(CmfBuds2.AllowsEq(version));
        Assert.Equal("1.0.1.50", NothingModel.ParseFirmware([.. "1.0.1.50"u8, 0, 0]));
        Assert.Equal("2.4.5", NothingModel.ParseFirmware("2.4.5"u8));
        foreach (byte[] malformed in new byte[][] { [.. "1..50"u8], [.. "1.0\u00001.50"u8], [0xFF], [.. "release 1.0.1.50"u8], Enumerable.Repeat((byte)49, 33).ToArray() })
            Assert.Null(NothingModel.ParseFirmware(malformed));
        Assert.True(EarA.AllowsEq(null));
    }

    [Fact]
    public void IdentityReturnsOnlyAllowlistedModelCodeWithoutNameFallback()
    {
        // Deliberately synthetic SKU-bearing values, never an actual device identity.
        (string Serial, string Code)[] examples =
        [
            ("SH0063TEST0000", "B162"), ("SH0073TEST0000", "B162"), ("M3A603TEST0000", "B170"), ("M3A606TEST0000", "B170"),
            ("SH0099TEST0000", "B179"), ("130098TEST0000", "B179"), ("SH0076TEST0000", "B172"),
        ];
        foreach (var (serial, code) in examples)
        {
            byte[] payload = Encoding.ASCII.GetBytes($"2,4,{serial}\n3,4,{serial}\n6,6,000000000000\n");
            Assert.Equal(code, EarA.IdentityCode(payload));
            Assert.Equal(code, EarA.IdentityCode([4, .. payload]));
        }
        Assert.Equal("B172", CmfBudsPro2.IdentityCode([.. "B172"u8, 0]));
        Assert.Equal("B179", CmfBudsPro2.IdentityCode("B179"u8)); // the session compares with its own model
        Assert.Null(CmfBudsPro2.IdentityCode("unknown B172"u8));
        foreach (string text in new[] { "", "2,4,UNKNOWN0000\n", "2,4,SH0084TEST0000\n", "2,4,SH0063TEST0000\n3,4,M3A603TEST0000\n",
                     "6,6,000000000000\n", "2,4,SH0063TEST0000,extra\n", "2,4,SH0063TEST0000\nmalformed\n" })
            Assert.Null(EarA.IdentityCode(Encoding.ASCII.GetBytes(text)));
        Assert.Null(EarA.IdentityCode(Enumerable.Repeat((byte)65, 1025).ToArray()));
    }

    /// <summary>Synthetic Nothing device. The generic ACK always arrives before any read-back.</summary>
    private sealed class Device(string name) : IDuplexByteChannel
    {
        private readonly FakeByteChannel _inner = new();
        public string Description => "Nothing RFCOMM fixture";
        public string Name { get; } = name;
        public List<NothingFrame> Requests { get; } = [];
        public byte Noise = 5, Eq, Advanced;
        public string Firmware = "1.0.1.50";
        public string? SerialOverride;
        public bool ApplyWrites = true, WrongSequence;
        public HashSet<ushort> Missing { get; } = [];
        public Func<NothingFrame, byte[]?>? Replacement;
        public Action<NothingFrame>? BeforeResponse;

        public void Emit(ushort command, byte sequence, byte[] payload) =>
            _inner.Receive(Rx(command, payload, sequence, payloadCrc: Name == "CMF Buds Pro 2"));

        public void EmitRaw(byte[] bytes) => _inner.Receive(bytes);

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _inner.WriteAsync(data, ct);
            byte[] b = data.ToArray();
            Assert.Equal(new byte[] { 0x55, 0x60, 1 }, b[..3]);
            int count = b[5] | b[6] << 8;
            Assert.Equal(count + 10, b.Length);
            Assert.Equal(NothingFrameCodec.Checksum(b.AsSpan(0, b.Length - 2)), (ushort)(b[^2] | b[^1] << 8));
            var request = new NothingFrame((ushort)(b[3] | b[4] << 8), b[7], b[8..(8 + count)]);
            Requests.Add(request);
            BeforeResponse?.Invoke(request);
            switch (request.Command)
            {
                case 0xF00F:
                    if (ApplyWrites) Noise = request.Payload[1];
                    Emit(0x700F, request.Sequence, [0]);
                    return;
                case 0xF010 or 0xF01D:
                    if (ApplyWrites) Eq = request.Payload[0];
                    Emit(Name.StartsWith("CMF", StringComparison.Ordinal) ? (ushort)0x701D : (ushort)0x7010, request.Sequence, [0]);
                    return;
            }
            if (Missing.Contains(request.Command)) return;
            if ((Replacement?.Invoke(request) ?? DefaultPayload(request)) is { } payload)
                Emit((ushort)(request.Command & 0x7FFF), WrongSequence ? (byte)(request.Sequence + 1) : request.Sequence, payload);
        }

        private byte[]? DefaultPayload(NothingFrame request)
        {
            var serials = new Dictionary<string, string>
            {
                ["Nothing Ear (a)"] = "SH0063TEST0000", ["Nothing Headphone (1)"] = "M3A603TEST0000",
                ["CMF Buds 2"] = "SH0099TEST0000", ["CMF Buds Pro 2"] = "SH0076TEST0000",
            };
            return request.Command switch
            {
                0xC01C => [.. "B172"u8],
                0xC006 => Encoding.ASCII.GetBytes("1,4," + (SerialOverride ?? serials.GetValueOrDefault(Name, "UNKNOWN")) + "\n"),
                0xC042 => Encoding.ASCII.GetBytes(Firmware),
                0xC007 => Name == "Nothing Headphone (1)" ? [0, 0, 88] : [3, 2, 80, 3, 0xC6, 4, 0xFF],
                0xC01E => Name == "CMF Buds Pro 2" ? [2, 1, 0, 1, Noise, 0] : [0, Noise],
                0xC01F or 0xC050 => [Eq],
                0xC04C => [Advanced],
                _ => null,
            };
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static (NothingDeviceController Controller, DeviceStateStore Store) Create(Device device, double timeoutMs = 200)
    {
        var model = NothingModel.Named(device.Name) ?? throw new InvalidOperationException(device.Name);
        var profile = new NothingProfile(model);
        var identity = new DeviceIdentity("nothing-test", device.Name, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (new NothingDeviceController(device, context, profile, TimeSpan.FromMilliseconds(timeoutMs)), store);
    }

    [Theory]
    [InlineData("Nothing Ear (a)")]
    [InlineData("Nothing Headphone (1)")]
    [InlineData("CMF Buds 2")]
    [InlineData("CMF Buds Pro 2")]
    public async Task FourProfilesReadFreshStateAndWriteVerifiedModes(string name)
    {
        var device = new Device(name);
        var (controller, store) = Create(device);
        await using var session = controller;
        var profile = Assert.IsType<NothingProfile>(store.Current.Profile);
        Assert.Empty(profile.NoiseControlOptions);
        Assert.True(await controller.InitializeAsync(Ct));

        Assert.Null(store.Current.Battery.Case);
        if (name == "Nothing Headphone (1)")
        {
            Assert.Equal(88, store.Current.Battery.Left!.Value.Level);
            Assert.Null(store.Current.Battery.Right);
        }
        else
        {
            Assert.Equal(new BatteryReading(80, false), store.Current.Battery.Left!.Value);
            Assert.Equal(new BatteryReading(70, true), store.Current.Battery.Right!.Value);
        }
        Assert.Equal("1.0.1.50", store.Current.Firmware!.Value.Version);
        Assert.Equal(new NoiseControlState(NoiseControlMode.Off), store.Current.NoiseControl!.Value);
        profile = Assert.IsType<NothingProfile>(store.Current.Profile);
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.Equalizer).Support);
        Assert.Equal(3, profile.NoiseControlOptions.Count);

        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(7, device.Noise);
        Assert.True((await controller.SetEqualizerPresetAsync("3", Ct)).IsSuccess);
        Assert.Equal(3, device.Eq);
        Assert.Equal("3", store.Current.Equalizer!.Value.ActivePresetId);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Adaptive), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("9", Ct)).Status);

        var noiseRead = device.Requests.First(r => r.Command == 0xC01E);
        Assert.Equal(name == "CMF Buds Pro 2" ? new byte[] { 3 } : [], noiseRead.Payload);
        Assert.Contains(device.Requests, r => r.Command == (name.StartsWith("CMF", StringComparison.Ordinal) ? 0xF01D : 0xF010) && r.Payload.SequenceEqual(new byte[] { 3, 0 }));
        // Sequences are never reused within the connection.
        Assert.Equal(device.Requests.Count, device.Requests.Select(r => r.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task IdentityMismatchFailsBeforeAnyOtherRequest()
    {
        var device = new Device("Nothing Ear (a)") { SerialOverride = "M3A603TEST0000" };
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        Assert.Equal(new[] { (ushort)0xC006 }, device.Requests.Select(r => r.Command));
        Assert.Null(store.Current.Battery.Left);
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
    }

    [Fact]
    public async Task AckAndUnsolicitedEventsNeverConfirmWrites()
    {
        var device = new Device("Nothing Ear (a)");
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.ApplyWrites = false;
        device.BeforeResponse = r => { if (r.Command == 0xC01E) device.Emit(0xE003, r.Sequence, [0, 7]); };
        var result = await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl!.Value.Mode);
        Assert.Equal(6, device.Requests.Count(r => r.Command == 0xC01E) - 1);

        // A missing read-back is a timeout that retires the session; nothing is assumed.
        device.BeforeResponse = null;
        device.Missing.Add(0xC01E);
        Assert.Equal(CommandStatus.Timeout, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.True(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task CmfBuds2FirmwareGatesOnlyEq()
    {
        foreach (string firmware in new[] { "1.0.1.49", "unknown", "1.0.1.50" })
        {
            var device = new Device("CMF Buds 2") { Firmware = firmware };
            var (controller, store) = Create(device);
            await using var session = controller;
            Assert.True(await controller.InitializeAsync(Ct));
            Assert.NotEmpty(store.Current.Profile!.NoiseControlOptions);
            bool allowed = firmware == "1.0.1.50";
            Assert.Equal(allowed, store.Current.Profile.EqualizerPresets.Count > 0);
            Assert.Equal(allowed, device.Requests.Any(r => r.Command == 0xC050));
            if (!allowed) Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("1", Ct)).Status);
        }
    }

    [Fact]
    public async Task HeadphoneAdvancedEqBlocksPresetWrites()
    {
        var device = new Device("Nothing Headphone (1)");
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.NotEmpty(store.Current.Profile!.EqualizerPresets);
        device.Advanced = 1;
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("3", Ct)).Status);
        Assert.DoesNotContain(device.Requests, r => r.Command == 0xF010);
        Assert.Empty(store.Current.Profile!.EqualizerPresets);
        Assert.Null(store.Current.Equalizer);
        Assert.NotEmpty(store.Current.Profile.NoiseControlOptions);

        // Advanced EQ active at connect: presets are never offered.
        var active = new Device("Nothing Headphone (1)") { Advanced = 1 };
        var (second, secondStore) = Create(active);
        await using var secondSession = second;
        Assert.True(await second.InitializeAsync(Ct));
        Assert.Empty(secondStore.Current.Profile!.EqualizerPresets);
    }

    [Fact]
    public async Task MissingOptionalEqKeepsBatteryAndNoise()
    {
        var device = new Device("Nothing Ear (a)");
        device.Missing.Add(0xC01F);
        var (controller, store) = Create(device, 100);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Empty(store.Current.Profile!.EqualizerPresets);
        Assert.NotEmpty(store.Current.Profile.NoiseControlOptions);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
    }

    [Fact]
    public async Task TruncatedOptionalReplyCannotPoisonFollowingQueries()
    {
        var device = new Device("Nothing Ear (a)");
        device.Missing.Add(0xC042);
        device.BeforeResponse = r => { if (r.Command == 0xC042) device.EmitRaw([0x55, 0x60, 1, 0x42, 0x40, 100, 0, r.Sequence]); };
        var (controller, store) = Create(device, 100);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Null(store.Current.Firmware);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
    }

    [Fact]
    public async Task WrongSequenceDoesNotCompleteTheSession()
    {
        var device = new Device("Nothing Ear (a)") { WrongSequence = true };
        var (controller, _) = Create(device, 100);
        await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        Assert.Single(device.Requests);
    }

    [Fact]
    public async Task WritesArePendingUntilConfirmedOrRefused()
    {
        var device = new Device("Nothing Ear (a)");
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        bool pendingAtWrite = false;
        device.BeforeResponse = r => { if (r.Command is 0xF00F or 0xF010) pendingAtWrite = store.Current.PendingCommands.Count == 1; };

        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.True(pendingAtWrite);
        Assert.Empty(store.Current.PendingCommands);
        Assert.Null(store.Current.LastError);

        pendingAtWrite = false;
        device.BeforeResponse = r =>
        {
            if (r.Command == 0xF010) pendingAtWrite = store.Current.IsPending(PendingKeys.Equalizer);
        };
        device.ApplyWrites = false;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("3", Ct)).Status);
        Assert.True(pendingAtWrite);
        Assert.Empty(store.Current.PendingCommands);
        Assert.Equal(CommandStatus.Rejected, store.Current.LastError!.Status);
    }

    [Fact]
    public async Task ChannelFailureDuringAWriteClearsThePendingMarker()
    {
        var device = new Device("Nothing Ear (a)");
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        bool pendingAtWrite = false;
        device.BeforeResponse = r =>
        {
            if (r.Command != 0xF00F) return;
            pendingAtWrite = store.Current.IsPending(PendingKeys.NoiseControl);
            throw new IOException("link lost");
        };

        var result = await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct);

        Assert.Equal(CommandStatus.TransportError, result.Status);
        Assert.True(pendingAtWrite);
        Assert.Empty(store.Current.PendingCommands);
        Assert.Equal(CommandStatus.TransportError, store.Current.LastError!.Status);
    }

    [Fact]
    public async Task MalformedReadbackWithdrawsNoiseUntilReadAgain()
    {
        var device = new Device("Nothing Ear (a)");
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.Replacement = r => r.Command == 0xC01E ? [0] : null;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Null(store.Current.NoiseControl);
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
        device.Replacement = null;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.NotEmpty(store.Current.Profile!.NoiseControlOptions);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).IsSuccess);
    }

    [Fact]
    public async Task SequenceNeverWrapsWithinConnection()
    {
        var device = new Device("Nothing Ear (a)");
        var (controller, _) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        for (int i = 0; i < 90; i++) await controller.RefreshAsync(Ct);
        Assert.True(controller.Closed.IsCompleted);
        Assert.Equal(NothingDeviceController.MaxSequence, device.Requests.Count);
        Assert.Equal(NothingDeviceController.MaxSequence, device.Requests.Select(r => r.Sequence).Distinct().Count());
    }
}
