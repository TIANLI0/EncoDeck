// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace EncoDeck.Probe;

internal sealed class DokiDokiAdvertisement
{
    private readonly HashSet<Guid> _serviceUuids = [];
    private readonly Dictionary<ushort, string> _manufacturerData = [];

    public DokiDokiAdvertisement(ulong address, BluetoothAddressType addressType)
    {
        Address = address;
        AddressType = addressType;
    }

    public ulong Address { get; }
    public BluetoothAddressType AddressType { get; private set; }
    public string? Name { get; private set; }
    public short Rssi { get; private set; } = short.MinValue;
    public IReadOnlyCollection<Guid> ServiceUuids => _serviceUuids;
    public IReadOnlyDictionary<ushort, string> ManufacturerData => _manufacturerData;

    public void Observe(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        AddressType = args.BluetoothAddressType;
        Rssi = Math.Max(Rssi, args.RawSignalStrengthInDBm);

        string? name = ReadName(args.Advertisement);
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();

        foreach (Guid uuid in args.Advertisement.ServiceUuids)
            _serviceUuids.Add(uuid);

        foreach (var entry in args.Advertisement.ManufacturerData)
        {
            byte[] data = entry.Data.ToArray();
            _manufacturerData[entry.CompanyId] = Convert.ToHexString(data);
        }
    }

    public static string? ReadName(BluetoothLEAdvertisement advertisement)
    {
        if (!string.IsNullOrWhiteSpace(advertisement.LocalName)) return advertisement.LocalName;
        foreach (var section in advertisement.DataSections)
        {
            // 0x08 = shortened local name, 0x09 = complete local name.
            if (section.DataType is not (0x08 or 0x09)) continue;
            byte[] bytes = section.Data.ToArray();
            if (bytes.Length > 0) return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        return null;
    }
}

internal static class DokiDokiAdvertisementScanner
{
    public static async Task<IReadOnlyList<DokiDokiAdvertisement>> ScanAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var observations = new Dictionary<ulong, DokiDokiAdvertisement>();
        var gate = new Lock();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            bool matches = DokiDokiProtocol.IsSupportedName(DokiDokiAdvertisement.ReadName(args.Advertisement))
                || args.Advertisement.ServiceUuids.Any(DokiDokiProtocol.IsServiceUuid);
            if (!matches) return;

            lock (gate)
            {
                if (!observations.TryGetValue(args.BluetoothAddress, out var observation))
                {
                    observation = new DokiDokiAdvertisement(args.BluetoothAddress, args.BluetoothAddressType);
                    observations.Add(args.BluetoothAddress, observation);
                }
                observation.Observe(args);
            }
        }

        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            watcher.Received -= OnReceived;
            Stop(watcher);
        }

        lock (gate)
        {
            return observations.Values
                .OrderBy(device => DokiDokiProtocol.FormatAddress(device.Address), StringComparer.Ordinal)
                .ToArray();
        }
    }

    public static async Task<BluetoothAddressType?> FindAddressTypeAsync(
        ulong address,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<BluetoothAddressType?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress == address)
                found.TrySetResult(args.BluetoothAddressType);
        }

        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            return await found.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            watcher.Received -= OnReceived;
            Stop(watcher);
        }
    }

    private static void Stop(BluetoothLEAdvertisementWatcher watcher)
    {
        try
        {
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                watcher.Stop();
            }
        }
        catch (InvalidOperationException)
        {
            // The watcher may already have transitioned to stopping/stopped.
        }
    }
}
