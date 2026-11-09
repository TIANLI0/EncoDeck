// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;

namespace EncoDeck.Platform.A2dpBridge;

/// <summary>Earbuds BlueCaster has a connection profile for (see <see cref="BridgeDeviceMatch.Devices"/>).</summary>
public sealed record BridgeKnownDevice(DeviceIdentity Identity, bool IsStreaming);

/// <summary>Which of EncoDeck's devices the bridge is streaming to: the active profile's address against the Windows pairing's.</summary>
public static class BridgeDeviceMatch
{
    public static BridgeAudioLink? LinkFor(DeviceIdentity? device, BridgeStatus status)
    {
        if (device is not { Transport: DeviceTransport.Bluetooth } || !status.IsConnected) return null;
        var snapshot = status.Snapshot!;
        if (snapshot.ActiveStreamProfile is not { } profile || ParseAddress(profile.DeviceAddress) is not { } address || address != device.Address.Value) return null;
        var stream = snapshot.Stream;
        var info = stream.State == BridgeStreamState.Streaming ? stream.Info : null;
        return new BridgeAudioLink(stream.State == BridgeStreamState.Streaming, info?.Codec is { Length: > 0 } codec ? codec : null,
            info?.BitrateKbps ?? 0, info?.SampleRate ?? 0, profile.Name, CanCarryControl: status.Rfcomm && !profile.LeAudio);
    }

    /// <summary>Prefix of the device ids of <see cref="Devices"/>: not Windows ids, nothing opens them through Windows.</summary>
    public const string DeviceIdPrefix = "A2DPWB#";

    /// <summary>
    /// The earbuds BlueCaster has connection profiles for, one per address, as devices of their own: earbuds paired only
    /// with the bridge's adapter are unknown to Windows. Streaming while the bridge streams to them.
    /// </summary>
    public static IReadOnlyList<BridgeKnownDevice> Devices(BridgeStatus status)
    {
        if (!status.IsConnected) return [];
        var snapshot = status.Snapshot!;
        ulong? streaming = snapshot.Stream.State == BridgeStreamState.Streaming && snapshot.ActiveStreamProfile is { } active ? ParseAddress(active.DeviceAddress) : null;
        return snapshot.Profiles
            .Select(p => (Profile: p, Address: ParseAddress(p.DeviceAddress)))
            .Where(x => x.Address is not null and not 0)
            .GroupBy(x => x.Address!.Value)
            .Select(g =>
            {
                var profile = g.First().Profile;
                string name = g.Select(x => x.Profile.DeviceName.Trim()).FirstOrDefault(n => n.Length > 0) ?? profile.Name;
                var identity = new DeviceIdentity(DeviceIdPrefix + g.Key.ToString("X12", CultureInfo.InvariantCulture), name, new BluetoothAddress(g.Key));
                return new BridgeKnownDevice(identity, g.Key == streaming);
            })
            .ToArray();
    }

    /// <summary>"AA:BB:CC:DD:EE:FF" (also with '-' or no separators) as the 48-bit address.</summary>
    public static ulong? ParseAddress(string text)
    {
        string hex = new(text.Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 && text.Length <= 17 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value) ? value : null;
    }
}
