// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>A message of BlueCaster's language files: a key and printf-style arguments (see <see cref="A2dpBridgeText"/>).</summary>
public sealed record BridgeMessage(string Key, IReadOnlyList<string> Args)
{
    public static BridgeMessage Empty { get; } = new("", []);

    public bool IsEmpty => Key.Length == 0;
}

public enum BridgeAdapterStatus { Stopped, Initializing, Ready, Error }

/// <summary>The driver of a USB Bluetooth adapter: only a WinUSB one is usable by BlueCaster; a Bluetooth one belongs to Windows.</summary>
public enum BridgeDriver { WinUsb, Bluetooth, Other, None }

public sealed record BridgeAdapter(string InstanceId, int Vid, int Pid, string DisplayName, string DeviceName, string Vendor,
    BridgeDriver Driver, string DriverService, bool? FirmwarePresent, IReadOnlyList<string> FirmwareFiles, string? FirmwareInvalid, BridgeMessage? VendorNote);

/// <summary>A device BlueCaster knows from its own pairing list or a scan (its own adapter, not Windows').</summary>
public sealed record BridgeDevice(string Address, string Name, bool Saved, bool Audio, int? Rssi, IReadOnlyList<string> Codecs, bool LeAudio);

public enum BridgeStreamState { Idle, Connecting, Streaming, Reconnecting, Error }

public sealed record BridgeStreamInfo(string Codec, int BitrateKbps, int SampleRate, int Channels, int SourceSampleRate, int SourceBits,
    string? RequestedCodec, bool RequestedInUse, bool Abr, bool BitrateCapped, string? SourceKind, string? SourceName, bool SourceWaiting, string? LeSummary);

public sealed record BridgeStream(BridgeStreamState State, BridgeMessage Message, string? Profile, BridgeStreamInfo? Info, int? Volume)
{
    public static BridgeStream Idle { get; } = new(BridgeStreamState.Idle, BridgeMessage.Empty, null, null, null);

    public bool IsActive => State is BridgeStreamState.Connecting or BridgeStreamState.Streaming or BridgeStreamState.Reconnecting;
}

/// <summary>A connection profile (profiles.json): which device, codec and audio source.</summary>
public sealed record BridgeProfile(string Name, string DeviceAddress, string DeviceName, string Codec, string Quality, bool LeAudio);

/// <summary>The core's state, as its snapshot and the events after it describe it.</summary>
public sealed record BridgeSnapshot
{
    public BridgeAdapterStatus AdapterStatus { get; init; }

    public BridgeMessage? AdapterError { get; init; }

    public IReadOnlyList<BridgeAdapter> Adapters { get; init; } = [];

    public IReadOnlyList<BridgeDevice> Devices { get; init; } = [];

    public bool Scanning { get; init; }

    public BridgeStream Stream { get; init; } = BridgeStream.Idle;

    public IReadOnlyList<BridgeProfile> Profiles { get; init; } = [];

    public string? ActiveProfile { get; init; }

    /// <summary>The core's settings as sent (kept whole: settings.set must send every field back).</summary>
    public string SettingsJson { get; init; } = "{}";

    public string ConfigDirectory { get; init; } = "";

    public string LogDirectory { get; init; } = "";

    /// <summary>A2DPWB v1 is running and may hold the adapter.</summary>
    public bool V1Running { get; init; }

    /// <summary>A newer BlueCaster release the core found, if any.</summary>
    public string? UpdateVersion { get; init; }

    /// <summary>The profile being streamed or connected, while the stream is active.</summary>
    public BridgeProfile? ActiveStreamProfile =>
        Stream.IsActive && Stream.Profile is { } name ? Profiles.FirstOrDefault(p => p.Name == name) : null;

    public bool HasUsableAdapter => Adapters.Any(a => a.Driver == BridgeDriver.WinUsb);

    public JsonObject SettingsCopy() => JsonNode.Parse(SettingsJson) as JsonObject ?? [];

    public bool Setting(string name) => SettingsCopy()[name] is JsonValue v && v.TryGetValue(out bool b) && b;
}

/// <summary>Reads the core's JSON (ipc crate, protocol 1) leniently: unknown fields are ignored, missing ones take defaults.</summary>
internal static class BridgeJson
{
    public static BridgeSnapshot Snapshot(JsonObject state)
    {
        var snapshot = new BridgeSnapshot();
        snapshot = ApplyAdapter(snapshot, state["adapter"] as JsonObject);
        return snapshot with
        {
            Devices = Devices(state["devices"]),
            Scanning = Bool(state["scanning"]),
            Stream = Stream(state["stream"] as JsonObject),
            Profiles = Profiles(state["profiles"]),
            ActiveProfile = Str(state["active_profile"]),
            SettingsJson = (state["settings"] as JsonObject)?.ToJsonString() ?? "{}",
            ConfigDirectory = Str(state["config_dir"]) ?? "",
            LogDirectory = Str(state["log_dir"]) ?? "",
            V1Running = Bool(state["v1_running"]),
            UpdateVersion = Str((state["update"] as JsonObject)?["available"]?["version"]),
        };
    }

    /// <summary>Applies one event; null for events that change nothing EncoDeck shows. <paramref name="bye"/>: the core is exiting.</summary>
    public static BridgeSnapshot? Apply(BridgeSnapshot current, JsonObject? @event, out bool bye)
    {
        bye = false;
        if (@event is null) return null;
        var value = @event["value"];
        switch (Str(@event["kind"]))
        {
            case "adapter": return ApplyAdapter(current, value as JsonObject);
            case "devices": return current with { Devices = Devices(value) };
            case "scanning": return current with { Scanning = Bool(value) };
            case "stream": return current with { Stream = Stream(value as JsonObject) };
            case "profiles":
                return current with { Profiles = Profiles(value?["profiles"]), ActiveProfile = Str(value?["active_profile"]) };
            case "settings": return value is JsonObject settings ? current with { SettingsJson = settings.ToJsonString() } : null;
            case "update": return current with { UpdateVersion = Str((value as JsonObject)?["available"]?["version"]) };
            case "v1_running": return current with { V1Running = Bool(value) };
            case "bye": bye = true; return null;
            default: return null;
        }
    }

    private static BridgeSnapshot ApplyAdapter(BridgeSnapshot current, JsonObject? adapter) => adapter is null ? current : current with
    {
        AdapterStatus = Str(adapter["status"]) switch
        {
            "initializing" => BridgeAdapterStatus.Initializing,
            "ready" => BridgeAdapterStatus.Ready,
            "error" => BridgeAdapterStatus.Error,
            _ => BridgeAdapterStatus.Stopped,
        },
        AdapterError = Message(adapter["error"]),
        Adapters = Adapters(adapter["adapters"]),
    };

    public static IReadOnlyList<BridgeAdapter> Adapters(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Select(a => new BridgeAdapter(
        Str(a["instance_id"]) ?? "", Int(a["vid"]) ?? 0, Int(a["pid"]) ?? 0, Str(a["display_name"]) ?? "", Str(a["device_name"]) ?? "", Str(a["vendor"]) ?? "",
        Str(a["driver"]) switch { "winusb" => BridgeDriver.WinUsb, "bluetooth" => BridgeDriver.Bluetooth, "none" => BridgeDriver.None, _ => BridgeDriver.Other },
        Str(a["driver_service"]) ?? "", a["firmware_present"] is JsonValue fp && fp.TryGetValue(out bool present) ? present : null,
        Strings(a["firmware_files"]), Str(a["firmware_invalid"]), Message(a["vendor_note"]))).ToArray();

    private static IReadOnlyList<BridgeDevice> Devices(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Select(d => new BridgeDevice(
        Str(d["address"]) ?? "", Str(d["name"]) ?? "", Bool(d["saved"]), Bool(d["audio"]), Int(d["rssi"]), Strings(d["codecs"]), Bool(d["le_audio"]))).ToArray();

    private static IReadOnlyList<BridgeProfile> Profiles(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Select(p => new BridgeProfile(
        Str(p["name"]) ?? "", Str(p["device_address"]) ?? "", Str(p["device_name"]) ?? "", Str(p["codec"]) ?? "auto", Str(p["quality"]) ?? "", Bool(p["le_audio"])))
        .Where(p => p.Name.Length > 0).ToArray();

    private static BridgeStream Stream(JsonObject? s)
    {
        if (s is null) return BridgeStream.Idle;
        var state = Str(s["state"]) switch
        {
            "connecting" => BridgeStreamState.Connecting,
            "streaming" => BridgeStreamState.Streaming,
            "reconnecting" => BridgeStreamState.Reconnecting,
            "error" => BridgeStreamState.Error,
            _ => BridgeStreamState.Idle,
        };
        BridgeStreamInfo? info = s["info"] is JsonObject i ? new BridgeStreamInfo(
            Str(i["codec"]) ?? "", Int(i["bitrate_kbps"]) ?? 0, Int(i["sample_rate"]) ?? 0, Int(i["channels"]) ?? 0,
            Int(i["source_sample_rate"]) ?? 0, Int(i["source_bits"]) ?? 0, Str(i["requested_codec"]), Bool(i["requested_in_use"]),
            Bool(i["abr"]), Bool(i["bitrate_capped"]), Str(i["source"]?["kind"]), Str(i["source"]?["name"]), Bool(i["source"]?["waiting"]),
            Str(i["le"]?["summary"])) : null;
        return new BridgeStream(state, Message(s["message"]) ?? BridgeMessage.Empty, Str(s["profile"]), info, Int(s["volume"]));
    }

    public static BridgeMessage? Message(JsonNode? node) =>
        node is JsonObject m && Str(m["key"]) is { Length: > 0 } key ? new BridgeMessage(key, Strings(m["args"])) : null;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v ? (v.TryGetValue(out string? s) ? s : v.ToJsonString()) : "").ToArray();

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue(out long l) ? l : null;

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue(out double d) && double.IsFinite(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        return null;
    }
}
