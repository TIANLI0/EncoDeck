// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Earbuds on LE Audio whose CTKD Classic pairing was removed on purpose.</summary>
/// <param name="Address">The primary earbud's public address (the Classic pairing's address).</param>
/// <param name="Name">The Classic pairing's name, which the device profiles match.</param>
/// <param name="DeviceId">The removed Classic pairing's id: the device keeps its settings, cache and preference.</param>
public sealed record LeAudioOnlyDevice(ulong Address, string Name, string DeviceId);

/// <summary>
/// Remembers earbuds switched to LE Audio whose Classic pairing EncoDeck removed. Pairing LE derives a Classic key
/// as well (CTKD); with it Windows and the earbuds keep bringing the Classic link up next to LC3, which steals the
/// audio (A2DP) or drops the LE links. Without the Classic pairing nothing else names the earbuds, so this list
/// keeps them known by their LE bonds. Persisted in a small JSON file; <see cref="Configure"/> picks the file.
/// </summary>
public static class LeAudioOnlyDevices
{
    private static readonly Lock Gate = new();
    private static string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EncoDeck", "le-audio-devices.json");
    private static List<LeAudioOnlyDevice>? _devices;

    /// <summary>Raised after the list changed.</summary>
    public static event EventHandler? Changed;

    /// <summary>Uses <paramref name="path"/> instead of the default file (call before anything reads the list).</summary>
    public static void Configure(string path)
    {
        lock (Gate)
        {
            _path = path;
            _devices = null;
        }
    }

    public static IReadOnlyList<LeAudioOnlyDevice> All
    {
        get { lock (Gate) return [.. Load()]; }
    }

    public static LeAudioOnlyDevice? Find(string deviceId)
    {
        lock (Gate) return Load().FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    public static LeAudioOnlyDevice? Find(ulong address)
    {
        lock (Gate) return Load().FirstOrDefault(d => d.Address == address);
    }

    public static void Record(LeAudioOnlyDevice device)
    {
        lock (Gate)
        {
            var devices = Load();
            if (devices.Contains(device)) return;
            devices.RemoveAll(d => d.Address == device.Address);
            devices.Add(device);
            Save(devices);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(ulong address)
    {
        lock (Gate)
        {
            var devices = Load();
            if (devices.RemoveAll(d => d.Address == address) == 0) return;
            Save(devices);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static List<LeAudioOnlyDevice> Load()
    {
        if (_devices is not null) return _devices;
        try
        {
            _devices = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<LeAudioOnlyDevice>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _devices = [];
        }
        _devices.RemoveAll(d => d is null || d.Address == 0 || string.IsNullOrEmpty(d.DeviceId));
        return _devices;
    }

    private static void Save(List<LeAudioOnlyDevice> devices)
    {
        _devices = devices;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(devices));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory list still serves this run.
        }
    }
}
