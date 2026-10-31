// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Oppo;

/// <summary>One battery entry: component id (1 = left, 2 = right, 3 = case), level 0–100, charging flag.</summary>
public readonly record struct OppoBatteryEntry(byte Component, int Level, bool IsCharging);

/// <summary>One (feature id, value) pair from the feature status response.</summary>
public readonly record struct OppoFeatureValue(byte FeatureId, byte Value);

/// <summary>
/// Earbud / case position flags (0x8109 and event 0x02). Bit meanings from the official client:
/// bit0 = 1 out of case (inverted "in box"), bit1 = in ear, bit2 = 0 means case lid closed.
/// </summary>
public readonly record struct OppoWearEntry(byte Component, byte Flags)
{
    public bool IsOutOfCase => (Flags & 0x01) != 0;
    public bool IsInEar => (Flags & 0x02) != 0;
    public bool IsLidOpen => (Flags & 0x04) != 0;
}

/// <summary>Firmware version triple from 0x8105: component (1 L, 2 R, 3 case), version type, version.</summary>
public readonly record struct OppoVersionEntry(int Component, int VersionType, string Version);

/// <summary>One preset from the equalizer list (0x8122 / 0x0506).</summary>
public sealed record OppoEqualizerEntry(bool IsSelected, sbyte MinGain, sbyte MaxGain, byte Id, string Name, IReadOnlyList<(ushort FrequencyHz, sbyte Gain)> Bands);

/// <summary>A source device known to the earbuds (event 0x06 / 0x8112). Contains personal data — never log it unredacted.</summary>
public sealed record OppoSourceDevice(byte[] AddressLittleEndian, byte State, byte Flags, string Name)
{
    public bool IsConnected => State != 0;
    /// <summary>Flags bit0: the device that is reading this list (i.e. this PC).</summary>
    public bool IsCurrentHost => (Flags & 0x01) != 0;
    public bool IsMainAudioDevice => (Flags & 0x02) != 0;
    public bool IsAudioActive => (Flags & 0x04) != 0;
    public int DeviceType => (Flags >> 3) & 0x07;
}

/// <summary>Noise-control sub-report: subtype 1 = current mode, 2 = long-press cycle, 3 = supported, 4 = smart level.</summary>
public readonly record struct OppoNoiseReport(byte Subtype, byte Type, uint Bitmap);

/// <summary>
/// Stateless payload parsers. Every parser is strict: a malformed payload yields false rather than
/// a guess, and callers log it as an unrecognized frame. Response payloads begin with a status byte
/// where 0x00 means success (inferred from captures; see docs/PROTOCOL_FINDINGS.md).
/// </summary>
public static class OppoParsers
{
    public const byte StatusOk = 0x00;

    public static bool IsResponseOk(OppoFrame frame) =>
        frame.IsResponse && frame.Payload.Length > 0 && frame.Payload[0] == StatusOk;

    /// <summary><c>0x8103</c>: <c>[status][id0][id1][id2]</c>, 24-bit little-endian product id.</summary>
    public static bool TryParseProductId(OppoFrame frame, out uint productId)
    {
        productId = 0;
        var p = frame.Payload;
        if (frame.Command != OppoCommand.ResponseOf(OppoCommand.QueryProductId) || p.Length < 4 || p[0] != StatusOk) return false;
        productId = (uint)(p[1] | (p[2] << 8) | (p[3] << 16));
        return true;
    }

    /// <summary>
    /// <c>0x8106</c>: <c>[status][count]([component][raw])*count</c>;
    /// <c>0x0204</c> event 0x01: <c>[0x01][count]([component][raw])*count</c>.
    /// raw: bit 7 = charging, bits 0–6 = percent.
    /// </summary>
    public static bool TryParseBattery(OppoFrame frame, out IReadOnlyList<OppoBatteryEntry> entries)
    {
        entries = [];
        var p = frame.Payload;
        bool isResponse = frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryBattery);
        bool isEvent = frame.Command == OppoCommand.DeviceEvent && p.Length > 0 && p[0] == OppoEventCode.Battery;
        if (!isResponse && !isEvent) return false;
        if (p.Length < 2 || (isResponse && p[0] != StatusOk)) return false;

        int count = p[1];
        if (p.Length < 2 + count * 2) return false;
        var list = new List<OppoBatteryEntry>(count);
        for (int i = 0; i < count; i++)
        {
            byte component = p[2 + i * 2];
            byte raw = p[3 + i * 2];
            int level = raw & 0x7F;
            if (level > 100) return false;
            list.Add(new OppoBatteryEntry(component, level, (raw & 0x80) != 0));
        }
        entries = list;
        return true;
    }

    /// <summary>
    /// <c>0x810C</c>: <c>[status][0x01][0x01][bitmap…]</c> where the bitmap is little-endian with the
    /// active mode index set. Returns the raw bitmap; mapping indices to modes is model specific.
    /// </summary>
    public static bool TryParseNoiseControlResponse(OppoFrame frame, out uint bitmap)
    {
        bitmap = 0;
        var p = frame.Payload;
        if (frame.Command != OppoCommand.ResponseOf(OppoCommand.QueryNoiseControl)) return false;
        if (p.Length < 4 || p[0] != StatusOk) return false;
        return TryReadModeBitmap(p.AsSpan(1), out bitmap);
    }

    /// <summary>
    /// <c>0x0204</c> noise control event: <c>[0x03|0x04][…][0x01][0x01][bitmap…]</c>. The bytes between the
    /// event code and the 01 01 marker are not understood; the parser only accepts the marker directly
    /// after the event code (the layout observed so far) to avoid misreading other events.
    /// </summary>
    public static bool TryParseNoiseControlEvent(OppoFrame frame, out uint bitmap)
    {
        bitmap = 0;
        var p = frame.Payload;
        if (frame.Command != OppoCommand.DeviceEvent || p.Length < 4) return false;
        // Only event 03 carries noise control; event 04 is the fit-test result (official client).
        if (p[0] != OppoEventCode.NoiseControl) return false;
        return TryReadModeBitmap(p.AsSpan(1), out bitmap);
    }

    /// <summary>Reads <c>[0x01][0x01][bitmap 1–4 bytes LE]</c>.</summary>
    internal static bool TryReadModeBitmap(ReadOnlySpan<byte> data, out uint bitmap)
    {
        bitmap = 0;
        if (data.Length < 3 || data[0] != 0x01 || data[1] != 0x01) return false;
        var bits = data[2..];
        if (bits.Length > 4) bits = bits[..4];
        for (int i = 0; i < bits.Length; i++) bitmap |= (uint)bits[i] << (8 * i);
        return bitmap != 0;
    }

    /// <summary><c>0x810D</c>: <c>[status][count]([feature][value])*count</c>.</summary>
    public static bool TryParseFeatureStatus(OppoFrame frame, out IReadOnlyList<OppoFeatureValue> values)
    {
        values = [];
        var p = frame.Payload;
        if (frame.Command != OppoCommand.ResponseOf(OppoCommand.QueryFeatureStatus) || p.Length < 2 || p[0] != StatusOk) return false;
        int count = p[1];
        if (p.Length < 2 + count * 2) return false;
        var list = new List<OppoFeatureValue>(count);
        for (int i = 0; i < count; i++) list.Add(new OppoFeatureValue(p[2 + i * 2], p[3 + i * 2]));
        values = list;
        return true;
    }

    /// <summary><c>0x810F</c>: <c>[status][presetId]</c>.</summary>
    public static bool TryParseEqualizer(OppoFrame frame, out byte presetId)
    {
        presetId = 0;
        var p = frame.Payload;
        if (frame.Command != OppoCommand.ResponseOf(OppoCommand.QueryEqualizer) || p.Length < 2 || p[0] != StatusOk) return false;
        presetId = p[1];
        return true;
    }

    /// <summary><c>0x8200</c>/<c>0x8203</c>: <c>[status][count][code]*count</c>.</summary>
    public static bool TryParseEventCodes(OppoFrame frame, out IReadOnlyList<byte> codes)
    {
        codes = [];
        var p = frame.Payload;
        bool expected = frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryEventCodes) || frame.Command == OppoCommand.EventCodesAltResponse;
        if (!expected || p.Length < 2 || p[0] != StatusOk) return false;
        int count = p[1];
        if (p.Length < 2 + count) return false;
        codes = p.AsSpan(2, count).ToArray();
        return true;
    }

    /// <summary>
    /// Any noise-control report: <c>0x810C [status][subtype][type][bitmap…]</c> or
    /// <c>0x0204 [0x03][subtype][type][bitmap…]</c>.
    /// </summary>
    public static bool TryParseNoiseReport(OppoFrame frame, out OppoNoiseReport report)
    {
        report = default;
        ReadOnlySpan<byte> body;
        if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryNoiseControl))
        {
            if (frame.Payload.Length < 1 || frame.Payload[0] != StatusOk) return false;
            body = frame.Payload.AsSpan(1);
        }
        else if (frame.Command == OppoCommand.DeviceEvent && frame.Payload.Length > 0 && frame.Payload[0] == OppoEventCode.NoiseControl)
        {
            body = frame.Payload.AsSpan(1);
        }
        else
        {
            return false;
        }
        if (body.Length < 3) return false;
        uint bitmap = 0;
        var bits = body[2..];
        if (bits.Length > 4) bits = bits[..4];
        for (int i = 0; i < bits.Length; i++) bitmap |= (uint)bits[i] << (8 * i);
        report = new OppoNoiseReport(body[0], body[1], bitmap);
        return true;
    }

    /// <summary><c>0x8109 [status][n]{component, flags}</c> or event <c>[0x02][n]{component, flags}</c>.</summary>
    public static bool TryParseWearState(OppoFrame frame, out IReadOnlyList<OppoWearEntry> entries)
    {
        entries = [];
        var p = frame.Payload;
        int start;
        if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryWearState) && p.Length >= 2 && p[0] == StatusOk) start = 1;
        else if (frame.Command == OppoCommand.DeviceEvent && p.Length >= 2 && p[0] == OppoEventCode.WearState) start = 1;
        else return false;
        int count = p[start];
        if (p.Length < start + 1 + count * 2) return false;
        var list = new List<OppoWearEntry>(count);
        for (int i = 0; i < count; i++) list.Add(new OppoWearEntry(p[start + 1 + i * 2], p[start + 2 + i * 2]));
        entries = list;
        return true;
    }

    /// <summary><c>0x8105 [status][n]"comp,type,version,…"</c> (UTF-8 CSV, triples).</summary>
    public static bool TryParseVersions(OppoFrame frame, out IReadOnlyList<OppoVersionEntry> versions)
    {
        versions = [];
        var p = frame.Payload;
        if (frame.Command != OppoCommand.ResponseOf(OppoCommand.QueryFirmware) || p.Length < 2 || p[0] != StatusOk) return false;
        string text;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(p, 2, p.Length - 2);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var list = new List<OppoVersionEntry>();
        for (int i = 0; i + 2 < parts.Length; i += 3)
        {
            if (!int.TryParse(parts[i], out int component) || !int.TryParse(parts[i + 1], out int type)) return false;
            list.Add(new OppoVersionEntry(component, type, parts[i + 2]));
        }
        if (list.Count != p[1]) return false;
        versions = list;
        return true;
    }

    /// <summary><c>0x8122 [status][n]{selected, min, max, id, nameLen, name, bandCount, (freq u16, gain s8)*}</c>.</summary>
    public static bool TryParseEqualizerList(OppoFrame frame, out IReadOnlyList<OppoEqualizerEntry> entries)
    {
        entries = [];
        var p = frame.Payload;
        if (frame.Command != OppoCommand.ResponseOf(OppoCommand.QueryEqualizerList) || p.Length < 2 || p[0] != StatusOk) return false;
        int count = p[1];
        int offset = 2;
        var list = new List<OppoEqualizerEntry>(count);
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            for (int i = 0; i < count; i++)
            {
                if (offset + 5 > p.Length) return false;
                bool selected = p[offset] != 0;
                sbyte min = (sbyte)p[offset + 1];
                sbyte max = (sbyte)p[offset + 2];
                byte id = p[offset + 3];
                int nameLength = p[offset + 4];
                offset += 5;
                if (offset + nameLength + 1 > p.Length) return false;
                string name = utf8.GetString(p, offset, nameLength);
                offset += nameLength;
                int bandCount = p[offset++];
                if (offset + bandCount * 3 > p.Length) return false;
                var bands = new List<(ushort, sbyte)>(bandCount);
                for (int b = 0; b < bandCount; b++)
                {
                    bands.Add((ReadUInt16(p.AsSpan(offset)), (sbyte)p[offset + 2]));
                    offset += 3;
                }
                list.Add(new OppoEqualizerEntry(selected, min, max, id, name, bands));
            }
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
        entries = list;
        return offset == p.Length;
    }

    /// <summary>Device push <c>0x0504 [presetId]</c>.</summary>
    public static bool TryParseEqualizerChanged(OppoFrame frame, out byte presetId)
    {
        presetId = 0;
        if (frame.Command != OppoCommand.EqualizerChangedNotification || frame.Payload.Length < 1) return false;
        presetId = frame.Payload[0];
        return true;
    }

    /// <summary>Device push <c>0x0503 [n]{feature, value}</c>.</summary>
    public static bool TryParseFeatureChanged(OppoFrame frame, out IReadOnlyList<OppoFeatureValue> values)
    {
        values = [];
        var p = frame.Payload;
        if (frame.Command != OppoCommand.FeatureChangedNotification || p.Length < 1) return false;
        int count = p[0];
        if (p.Length < 1 + count * 2) return false;
        var list = new List<OppoFeatureValue>(count);
        for (int i = 0; i < count; i++) list.Add(new OppoFeatureValue(p[1 + i * 2], p[2 + i * 2]));
        values = list;
        return true;
    }

    /// <summary>Event <c>[0x06][n]{mac[6] LE, entryLen, state, flags, nameLen, name, …}</c>.</summary>
    public static bool TryParseSourceDevices(OppoFrame frame, out IReadOnlyList<OppoSourceDevice> devices)
    {
        devices = [];
        var p = frame.Payload;
        int offset;
        if (frame.Command == OppoCommand.DeviceEvent && p.Length >= 2 && p[0] == OppoEventCode.ConnectedDevices) offset = 1;
        else if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryConnectedDevices) && p.Length >= 2 && p[0] == StatusOk) offset = 1;
        else return false;
        int count = p[offset++];
        var list = new List<OppoSourceDevice>(count);
        for (int i = 0; i < count; i++)
        {
            if (offset + 10 > p.Length) return false;
            byte[] mac = p.AsSpan(offset, 6).ToArray();
            int entryLength = p[offset + 6];
            byte state = p[offset + 7];
            byte flags = p[offset + 8];
            int nameLength = p[offset + 9];
            int nameStart = offset + 10;
            if (entryLength < nameLength + 3 || nameStart + nameLength > p.Length) return false;
            string name = System.Text.Encoding.UTF8.GetString(p, nameStart, nameLength);
            // The next entry starts after the declared entry length (which covers state, flags, nameLen, name, extras).
            offset = offset + 7 + entryLength;
            if (mac.All(b => b == 0)) continue;
            list.Add(new OppoSourceDevice(mac, state, flags, name));
        }
        devices = list;
        return true;
    }

    /// <summary>Acknowledgement of a set command: <c>[status]…</c>.</summary>
    public static bool TryParseAck(OppoFrame frame, ushort requestCommand, out byte status)
    {
        status = 0xFF;
        if (frame.Command != OppoCommand.ResponseOf(requestCommand) || frame.Payload.Length < 1) return false;
        status = frame.Payload[0];
        return true;
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data);
}
