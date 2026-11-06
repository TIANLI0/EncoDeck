// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol.Huawei;

/// <summary>
/// Emulates HUAWEI earbuds on top of <see cref="FakeByteChannel"/> (whose responder only speaks OPPO).
/// Writes are acknowledged with result code 100000 like real firmware (OpenFreebuds / HuaweiPods
/// captures: <c>5A0009002B107F04000186A0729D</c>); unsupported reads answer result code 100003.
/// </summary>
internal sealed class FakeHuaweiDevice : IDuplexByteChannel
{
    public static readonly byte[] Ack = [0x7F, 0x04, 0x00, 0x01, 0x86, 0xA0];
    public static readonly byte[] NotSupported = [0x7F, 0x04, 0x00, 0x01, 0x86, 0xA3];

    private readonly LegacyHuaweiFrameCodec _codec = new();

    public FakeByteChannel Inner { get; } = new();

    public bool Silent { get; set; }
    public bool ErrorOnly { get; set; }

    /// <summary>Acknowledge writes with result code 100000 (Pro 5 sometimes does not for noise control, #133).</summary>
    public bool AckWrites { get; set; } = true;
    public bool AckAncWrites { get; set; } = true;
    public bool IgnoreAncWrites { get; set; }
    public bool RejectWrites { get; set; }

    /// <summary>Noise-control reads that still report the previous state after a write (#133).</summary>
    public int StaleAncReads { get; set; }

    public byte[] AncState { get; set; } = [0x00, 0x00]; // [sub, mode]
    public byte[] BatteryReply { get; set; } = [0x02, 0x03, 0x50, 0x46, 0x20, 0x03, 0x03, 0x01, 0x00, 0x01];
    public byte Wear { get; set; } = 1;
    public byte Eq { get; set; } = 5;
    public byte[]? EqOffered { get; set; }
    public byte LowLatency { get; set; }
    public byte Dual { get; set; } = 1;
    public (byte Left, byte Right)? WearState { get; set; }
    public List<byte[]> Sources { get; } = [];

    public bool SpatialAvailable { get; set; }
    public byte SpatialMode { get; set; }
    public bool IgnoreSpatialWrites { get; set; }

    /// <summary>Spatial values this firmware refuses: the write is answered with an error code and the mode stays.</summary>
    public HashSet<byte> RefusedSpatialValues { get; } = [];

    /// <summary>Feature-id switches (2BB4) the earbuds answer: id → field → value.</summary>
    public Dictionary<byte, Dictionary<byte, byte>> Features { get; } = [];

    public List<HuaweiFrame> Received { get; } = [];

    public string Description => "fake huawei";

    private byte[] _staleAnc = [];

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Inner.WriteAsync(data, cancellationToken);
        foreach (var frame in new LegacyHuaweiFrameCodec().Feed(data.Span))
        {
            lock (Received) Received.Add(frame);
            if (Silent) continue;
            if (ErrorOnly) { Reply(frame.Command, [0x7F, 1, 1]); continue; }
            var fields = HuaweiWire.ParseTlv(frame.Payload);
            bool write = fields.Values.Any(v => v.Length > 0);
            if (write && RejectWrites && frame.Command != HuaweiCommands.FeatureState) { Reply(frame.Command, NotSupported); continue; }
            switch (frame.Command)
            {
                case HuaweiCommands.Battery:
                    Reply(frame.Command, BatteryReply);
                    break;
                case HuaweiCommands.QueryAnc:
                    if (StaleAncReads > 0) { StaleAncReads--; Reply(frame.Command, [0x01, 0x02, .. _staleAnc]); }
                    else Reply(frame.Command, [0x01, 0x02, .. AncState]);
                    break;
                case HuaweiCommands.SetAnc:
                    _staleAnc = AncState;
                    if (!IgnoreAncWrites)
                    {
                        byte mode = frame.Payload[2], sub = frame.Payload[3];
                        AncState = [sub == 0xFF ? (mode == 1 ? (byte)0x03 : (byte)0x02) : sub, mode];
                    }
                    if (AckAncWrites) WriteAck(frame.Command);
                    break;
                case HuaweiCommands.QueryWearDetection:
                    Reply(frame.Command, [0x01, 0x01, Wear]);
                    break;
                case HuaweiCommands.SetWearDetection:
                    Wear = fields[1][0];
                    WriteAck(frame.Command);
                    break;
                case HuaweiCommands.WearState:
                    if (WearState is { } w) Reply(frame.Command, [1, 1, w.Left, 2, 1, w.Right, 3, 1, 0, 4, 1, 0]);
                    else Reply(frame.Command, NotSupported);
                    break;
                case HuaweiCommands.QueryEqualizer:
                    Reply(frame.Command, [0x01, 0x01, 0x01, 0x02, 0x01, Eq, .. EqOffered is { } offered ? [0x03, (byte)offered.Length, .. offered] : Array.Empty<byte>(), 0x08, 0x00]);
                    break;
                case HuaweiCommands.SetEqualizer:
                    Eq = fields[1][0];
                    WriteAck(frame.Command);
                    break;
                case HuaweiCommands.LowLatency when write:
                    LowLatency = fields[1][0];
                    WriteAck(frame.Command);
                    break;
                case HuaweiCommands.LowLatency:
                    Reply(frame.Command, [0x02, 0x01, LowLatency]);
                    break;
                case HuaweiCommands.QueryDualConnect:
                    Reply(frame.Command, [0x01, 0x01, Dual]);
                    break;
                case HuaweiCommands.SetDualConnect:
                    Dual = fields[1][0];
                    WriteAck(frame.Command);
                    break;
                case HuaweiCommands.DualConnectDevices:
                    foreach (var source in Sources) Reply(frame.Command, source);
                    if (Sources.Count == 0) Reply(frame.Command, NotSupported);
                    break;
                case HuaweiCommands.FeatureState:
                    HandleFeature(frame, fields);
                    break;
            }
        }
    }

    private void HandleFeature(HuaweiFrame frame, Dictionary<byte, byte[]> fields)
    {
        byte id = fields[1][0];
        bool isWrite = fields.Where(f => f.Key != 1).Any(f => f.Value.Length > 0);
        if (id == HuaweiCommands.FeatureSpatialAudio)
        {
            if (!SpatialAvailable) { Reply(frame.Command, NotSupported); return; }
            if (isWrite)
            {
                if (RefusedSpatialValues.Contains(fields[2][0])) { Reply(frame.Command, NotSupported); return; }
                if (!IgnoreSpatialWrites) SpatialMode = fields[2][0];
                WriteAck(frame.Command);
            }
            else Reply(frame.Command, [1, 1, 24, 2, 1, SpatialMode, 3, 1, 0]);
            return;
        }
        if (!Features.TryGetValue(id, out var state)) { Reply(frame.Command, NotSupported); return; }
        if (isWrite)
        {
            if (RejectWrites) { Reply(frame.Command, NotSupported); return; }
            foreach (var (field, value) in fields.Where(f => f.Key != 1)) state[field] = value[0];
            WriteAck(frame.Command);
            return;
        }
        Reply(frame.Command, [1, 1, id, .. state.OrderBy(f => f.Key).SelectMany(f => new byte[] { f.Key, 1, f.Value })]);
    }

    private void WriteAck(ushort command)
    {
        if (AckWrites) Reply(command, Ack);
    }

    public void Reply(ushort command, byte[] payload) => Inner.Receive(_codec.Encode(command, payload));

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Inner.ReadAsync(buffer, cancellationToken);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}
