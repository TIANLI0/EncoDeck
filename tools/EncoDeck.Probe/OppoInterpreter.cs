// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Probe;

/// <summary>Human-readable, model-agnostic interpretation of frames for diagnostic output.</summary>
internal static class OppoInterpreter
{
    public static string Describe(OppoFrame frame)
    {
        if (OppoParsers.TryParseProductId(frame, out uint productId))
            return $"product id 0x{productId:X6}";
        if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryCodec) && frame.Payload.Length >= 2)
            return $"reported codec id 0x{frame.Payload[1]:X2} (the earbuds' own report; not used by EncoDeck)";
        if (OppoParsers.TryParseBattery(frame, out var battery))
            return "battery " + string.Join(", ", battery.Select(b => $"{ComponentName(b.Component)}={b.Level}%{(b.IsCharging ? " charging" : string.Empty)}"));
        if (OppoParsers.TryParseNoiseControlResponse(frame, out uint ncBitmap) || OppoParsers.TryParseNoiseControlEvent(frame, out ncBitmap))
            return $"noise-control bitmap 0x{ncBitmap:X} (set bits: {string.Join(",", SetBits(ncBitmap))})";
        if (OppoParsers.TryParseFeatureStatus(frame, out var features))
            return "features " + string.Join(" ", features.Select(f => $"{f.FeatureId:X2}={f.Value:X2}"));
        if (OppoParsers.TryParseEqualizer(frame, out byte preset))
            return $"equalizer preset id {preset}";
        if (OppoParsers.TryParseEventCodes(frame, out var codes))
            return "event codes " + ConsoleTrace.Spaced(codes.ToArray());
        var p = frame.Payload;
        if (frame.Command == OppoCommand.DeviceEvent && p.Length >= 2 && p[0] == OppoEventCode.WearState && p.Length >= 2 + p[1] * 2)
            return "wear/position " + string.Join(", ", Enumerable.Range(0, p[1]).Select(i => $"{ComponentName(p[2 + i * 2])}=0x{p[3 + i * 2]:X2}"));
        if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryWearState) && p.Length >= 2 && p[0] == 0 && p.Length >= 2 + p[1] * 2)
            return "wear/position " + string.Join(", ", Enumerable.Range(0, p[1]).Select(i => $"{ComponentName(p[2 + i * 2])}=0x{p[3 + i * 2]:X2}"));
        if (frame.Command == OppoCommand.DeviceEvent && p.Length >= 2 && p[0] == OppoEventCode.ConnectedDevices)
            return $"source-device list, {p[1]} entr(ies) [names/addresses not printed]";
        if (frame.Command == OppoCommand.ResponseOf(OppoCommand.QueryFirmware) && p.Length > 2 && p[0] == 0)
        {
            string text = new(System.Text.Encoding.UTF8.GetString(p, 1, p.Length - 1).Select(c => char.IsControl(c) ? '.' : c).ToArray());
            return $"firmware text \"{text}\"";
        }
        if (frame.IsResponse && frame.Payload.Length > 0)
            return frame.Payload[0] == 0 ? "ok (payload not interpreted)" : $"status 0x{frame.Payload[0]:X2} (non-zero: failure?)";
        if (frame.IsResponse)
            return "empty response";
        return "not interpreted";
    }

    private static string ComponentName(byte component) => component switch
    {
        1 => "left",
        2 => "right",
        3 => "case",
        _ => $"component{component}",
    };

    private static IEnumerable<int> SetBits(uint value)
    {
        for (int i = 0; i < 32; i++)
        {
            if ((value & (1u << i)) != 0) yield return i;
        }
    }
}
