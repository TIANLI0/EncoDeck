// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Platform.Cache;

internal sealed record CachedReading(int Level, bool Charging, DateTimeOffset Timestamp);

internal sealed record CachedDevice(CachedReading? Left, CachedReading? Right, CachedReading? Case)
{
    /// <summary>Last product id the earbuds reported (identifies the model and its art before they connect).</summary>
    public uint? ProductId { get; init; }

    /// <summary>Last colour id the earbuds reported.</summary>
    public int? ColorId { get; init; }

    /// <summary>Last control-capability bitmap (hex); decides which settings the model offers.</summary>
    public string? Capabilities { get; init; }

    /// <summary>Last firmware version (lower of the two earbuds); some EQ presets depend on it.</summary>
    public int? Firmware { get; init; }

    /// <summary>Last multi-connection list the earbuds reported (without addresses), shown until they answer again.</summary>
    public IReadOnlyList<CachedSource>? Sources { get; init; }

    /// <summary>Last user EQ curve EncoDeck sent to earbuds that never report it (<see cref="StateSource.Sent"/>).</summary>
    public CachedCurve? SentCurve { get; init; }
}

/// <summary>A user EQ curve as sent: its id, gain limits and bands.</summary>
internal sealed record CachedCurve(int Id, int MinGain, int MaxGain, int[] Frequencies, int[] Gains, DateTimeOffset Timestamp);

/// <summary>One multi-connection source device; its Bluetooth address is deliberately not kept.</summary>
internal sealed record CachedSource(string Name, int State, bool IsThisDevice, bool IsAudioActive, int DeviceType, DateTimeOffset Timestamp);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, CachedDevice>))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;

/// <summary>
/// Remembers the last confirmed battery readings, product id, colour id, capabilities and firmware
/// per device across app restarts, and the EQ curve last sent to earbuds that cannot report theirs. Restored readings are marked <see cref="StateSource.Cache"/> so
/// the UI presents them as "last read", never as live; the rest lets the UI show the device's art
/// and the same settings as last time before (and while) it connects.
/// Device keys are hashed; no Bluetooth address is written to disk.
/// </summary>
public sealed class DeviceStateCache : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(20);

    private readonly string _path;
    private readonly IDeviceStateStore _store;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private Dictionary<string, CachedDevice> _entries = [];
    private string? _lastDeviceId;
    private Timer? _saveTimer;
    private bool _dirty;

    public DeviceStateCache(string path, IDeviceStateStore store, ILogger<DeviceStateCache> logger)
    {
        _path = path;
        _store = store;
        _logger = logger;
    }

    public void Start()
    {
        Load();
        _store.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        var current = e.Current;
        if (current.Device is not { } device)
        {
            // Restore again when the device comes back (e.g. after it was unpaired and paired).
            _lastDeviceId = null;
            return;
        }
        string key = Key(device.DeviceId);

        // A new device, or the same one after its state was reset (e.g. it reconnected): fill what
        // the state does not know yet from last time.
        bool reset = e.Previous.Device?.DeviceId != device.DeviceId || IsBlank(current);
        if (device.DeviceId != _lastDeviceId || reset)
        {
            _lastDeviceId = device.DeviceId;
            Restore(device.DeviceId, key, current);
            return;
        }

        var battery = current.Battery;
        CachedDevice? updated = null;
        lock (_gate)
        {
            _entries.TryGetValue(key, out var existing);
            var next = new CachedDevice(
                Pick(battery.Left, existing?.Left),
                Pick(battery.Right, existing?.Right),
                Pick(battery.Case, existing?.Case))
            {
                ProductId = current.ProductId ?? existing?.ProductId,
                ColorId = current.ColorId ?? existing?.ColorId,
                Capabilities = current.ControlCapabilities is { Length: > 0 } caps ? Convert.ToHexString(caps) : existing?.Capabilities,
                Firmware = current.BudFirmware ?? existing?.Firmware,
                Sources = PickSources(current.SourceDevices, existing?.Sources),
                SentCurve = PickCurve(current.CustomEqualizers, existing?.SentCurve),
            };
            if (next != existing)
            {
                _entries[key] = next;
                updated = next;
            }
        }
        if (updated is not null) ScheduleSave();
    }

    private void Restore(string deviceId, string key, DeviceState current)
    {
        CachedDevice? cached;
        lock (_gate) _entries.TryGetValue(key, out cached);
        if (cached is null) return;

        _store.TryUpdate(deviceId, state =>
        {
            var battery = state.Battery;
            battery = battery.Left is null && cached.Left is { } l ? battery with { Left = ToObserved(l) } : battery;
            battery = battery.Right is null && cached.Right is { } r ? battery with { Right = ToObserved(r) } : battery;
            battery = battery.Case is null && cached.Case is { } c ? battery with { Case = ToObserved(c) } : battery;
            return state with
            {
                Battery = battery,
                ProductId = state.ProductId ?? cached.ProductId,
                ColorId = state.ColorId ?? cached.ColorId,
                ControlCapabilities = state.ControlCapabilities ?? Capabilities(cached.Capabilities),
                BudFirmware = state.BudFirmware ?? cached.Firmware,
                SourceDevices = state.SourceDevices ?? ToObserved(cached.Sources),
                CustomEqualizers = state.CustomEqualizers ?? ToObserved(cached.SentCurve),
            };
        });
    }

    /// <summary>A freshly reset state: nothing read from the earbuds yet.</summary>
    private static bool IsBlank(DeviceState state) =>
        state.Battery.Left is null && state.Battery.Right is null && state.Battery.Case is null && state.ProductId is null
        && state.SourceDevices is null;

    private static byte[]? Capabilities(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Observed<BatteryReading> ToObserved(CachedReading r) =>
        Observed.From(new BatteryReading(r.Level, r.Charging), r.Timestamp, StateSource.Cache);

    /// <summary>
    /// Restored without addresses and marked <see cref="StateSource.Cache"/>: the UI shows them as last
    /// known and offers no actions until the earbuds report the live list.
    /// </summary>
    private static Observed<IReadOnlyList<SourceDevice>>? ToObserved(IReadOnlyList<CachedSource>? sources) =>
        sources is { Count: > 0 }
            ? Observed.From<IReadOnlyList<SourceDevice>>(
                sources.Select(d => new SourceDevice([], d.Name, d.State, d.IsThisDevice, d.IsAudioActive, d.DeviceType)).ToArray(),
                sources[0].Timestamp, StateSource.Cache)
            : null;

    private static IReadOnlyList<CachedSource>? PickSources(Observed<IReadOnlyList<SourceDevice>>? live, IReadOnlyList<CachedSource>? existing)
    {
        if (live is null || !live.IsConfirmed) return existing;
        // Connecting / disconnecting (1, 4) is momentary: remember the device as not connected.
        var next = live.Value.Select(d => new CachedSource(d.Name, d.State == 2 ? 2 : 0, d.IsThisDevice, d.IsAudioActive && d.State == 2, d.DeviceType, live.Timestamp)).ToArray();
        // Same devices as before: keep the old list (and its timestamp) so nothing is saved.
        return existing is not null && next.Select(Comparable).SequenceEqual(existing.Select(Comparable)) ? existing : next;

        static CachedSource Comparable(CachedSource d) => d with { Timestamp = default };
    }

    /// <summary>Only a curve the earbuds cannot report is kept; a newer send replaces it.</summary>
    private static CachedCurve? PickCurve(Observed<IReadOnlyList<CustomEqualizer>>? live, CachedCurve? existing) =>
        live is not { Source: StateSource.Sent, Value: [var curve] } || existing?.Timestamp == live.Timestamp ? existing
        : new(curve.Id, curve.MinGain, curve.MaxGain, [.. curve.Bands.Select(b => b.FrequencyHz)], [.. curve.Bands.Select(b => b.Gain)], live.Timestamp);

    private static Observed<IReadOnlyList<CustomEqualizer>>? ToObserved(CachedCurve? curve) =>
        curve is { Frequencies.Length: > 0 } && curve.Frequencies.Length == curve.Gains.Length
            ? Observed.From<IReadOnlyList<CustomEqualizer>>([new CustomEqualizer(curve.Id, "", curve.MinGain, curve.MaxGain, IsSelected: true,
                [.. curve.Frequencies.Zip(curve.Gains, (f, g) => new EqualizerBand(f, g))])], curve.Timestamp, StateSource.Cache)
            : null;

    private static CachedReading? Pick(Observed<BatteryReading>? live, CachedReading? existing)
    {
        if (live is null || !live.IsConfirmed || live.Value.Level is not { } level) return existing;
        return new CachedReading(level, live.Value.IsCharging == true, live.Timestamp);
    }

    private static string Key(string deviceId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("encodeck-cache|" + deviceId)).AsSpan(0, 12));

    private void ScheduleSave()
    {
        lock (_gate)
        {
            _dirty = true;
            _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var stream = File.OpenRead(_path);
            _entries = JsonSerializer.Deserialize(stream, CacheJsonContext.Default.DictionaryStringCachedDevice) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Device cache unreadable; starting empty");
            _entries = [];
        }
    }

    private void Save()
    {
        Dictionary<string, CachedDevice> snapshot;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            snapshot = new Dictionary<string, CachedDevice>(_entries);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot, CacheJsonContext.Default.DictionaryStringCachedDevice);
            }
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save device cache");
        }
    }

    public void Dispose()
    {
        _store.StateChanged -= OnStateChanged;
        Timer? timer;
        lock (_gate)
        {
            timer = _saveTimer;
            _saveTimer = null;
        }
        timer?.Dispose();
        Save();
    }
}
