// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using EncoDeck.Core.Privacy;
using EncoDeck.Protocol.Brands.DokiDoki;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace EncoDeck.Bluetooth.DokiDoki;

/// <summary>A nearby DokiDoki peripheral seen in BLE advertisements.</summary>
/// <param name="Family">Null when the name does not identify a family (e.g. <c>TRYFUN-…</c>, or no name seen yet).</param>
public sealed record DokiDokiAdvertisement(
    BluetoothAddress Address,
    bool IsRandomAddress,
    string? Name,
    DokiDokiFamily? Family,
    short Rssi,
    DateTimeOffset LastSeen);

/// <summary>
/// Active BLE scan for DokiDoki devices, matched by name prefix or the FFAC service. Unlike the
/// headset path this does not require pairing: these devices are LE-only and often use random addresses.
/// </summary>
public sealed class DokiDokiAdvertisementWatcher : IDisposable
{
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, DokiDokiAdvertisement> _devices = [];
    private BluetoothLEAdvertisementWatcher? _watcher;

    public DokiDokiAdvertisementWatcher(ILogger<DokiDokiAdvertisementWatcher>? logger = null, TimeProvider? time = null)
    {
        _logger = logger ?? NullLogger<DokiDokiAdvertisementWatcher>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<DokiDokiAdvertisement> Devices
    {
        get { lock (_gate) return _devices.Values.OrderByDescending(d => d.Rssi).ToList(); }
    }

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    /// <summary>Raised on a thread-pool thread for every matching advertisement (merged with what was seen before).</summary>
    public event EventHandler<DokiDokiAdvertisement>? Seen;

    /// <summary>Raised when Windows stops the scan on its own (radio off, access denied).</summary>
    public event EventHandler<string>? Aborted;

    /// <summary>Starts a fresh scan; devices seen earlier are forgotten.</summary>
    public void Start()
    {
        if (IsScanning) return;
        Stop();
        lock (_gate) _devices.Clear();
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
        _watcher.Start();
    }

    public void Stop()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null) return;
        watcher.Received -= OnReceived;
        watcher.Stopped -= OnStopped;
        try
        {
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
        catch (InvalidOperationException)
        {
            // Already stopping.
        }
    }

    internal static bool Matches(string? name, IEnumerable<Guid> serviceUuids) =>
        DokiDokiNames.IsSupportedDeviceName(name) || serviceUuids.Contains(DokiDokiConstants.ServiceUuid);

    /// <summary>Merges one observation into what is known about an address; a name, once seen, is kept.</summary>
    internal static DokiDokiAdvertisement Merge(DokiDokiAdvertisement? previous, ulong address, bool isRandom, string? name, short rssi, DateTimeOffset now)
    {
        string? merged = string.IsNullOrWhiteSpace(name) ? previous?.Name : name.Trim().TrimEnd('\0');
        return new DokiDokiAdvertisement(new BluetoothAddress(address), isRandom, merged, DokiDokiNames.DetectFamily(merged), rssi, now);
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string? name = ReadName(args.Advertisement);
        DokiDokiAdvertisement seen;
        lock (_gate)
        {
            _devices.TryGetValue(args.BluetoothAddress, out var previous);
            // A scan response may carry only the name or only the service; accept either once matched.
            if (previous is null && !Matches(name, args.Advertisement.ServiceUuids)) return;
            seen = Merge(previous, args.BluetoothAddress, args.BluetoothAddressType == BluetoothAddressType.Random,
                name, args.RawSignalStrengthInDBm, _time.GetUtcNow());
            _devices[args.BluetoothAddress] = seen;
        }
        Seen?.Invoke(this, seen);
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error == BluetoothError.Success) return;
        _logger.LogWarning("DokiDoki scan stopped: {Error}", args.Error);
        Aborted?.Invoke(this, args.Error.ToString());
    }

    private static string? ReadName(BluetoothLEAdvertisement advertisement)
    {
        if (!string.IsNullOrWhiteSpace(advertisement.LocalName)) return advertisement.LocalName;
        foreach (var section in advertisement.DataSections)
        {
            // 0x08 = shortened local name, 0x09 = complete local name.
            if (section.DataType is not (0x08 or 0x09)) continue;
            byte[] bytes = section.Data.ToArray();
            if (bytes.Length > 0) return Encoding.UTF8.GetString(bytes);
        }
        return null;
    }

    public void Dispose() => Stop();
}
