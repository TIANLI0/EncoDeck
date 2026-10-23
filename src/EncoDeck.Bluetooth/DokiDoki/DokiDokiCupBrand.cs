// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Cups;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.DokiDoki;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.DokiDoki;

/// <summary>Cup › DokiDoki (TryFun): BLE advertisement scan and GATT sessions.</summary>
public sealed class DokiDokiCupBrand : ICupBrand
{
    private readonly DokiDokiAdvertisementWatcher _watcher;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, (string? Name, DokiDokiFamily? Family)> _listed = [];

    public DokiDokiCupBrand(ILoggerFactory loggers)
    {
        _watcher = new DokiDokiAdvertisementWatcher(loggers.CreateLogger<DokiDokiAdvertisementWatcher>());
        _watcher.Seen += OnSeen;
        _watcher.Aborted += OnAborted;
    }

    public string Id => DokiDokiProfile.BrandId;

    public string DisplayName => "DokiDoki";

    public IReadOnlyList<ICupProfile> Models => DokiDokiProfile.Models;

    public bool IsScanning => _watcher.IsScanning;

    public IReadOnlyList<CupAdvertisement> Devices => _watcher.Devices.Select(ToCup).ToList();

    public event EventHandler? DevicesChanged;

    public event EventHandler<string>? ScanAborted;

    public void StartScan()
    {
        lock (_gate) _listed.Clear();
        _watcher.Start();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopScan() => _watcher.Stop();

    public ICupSession CreateSession(CupAdvertisement target, ICupProfile model, Func<bool> allowOutput, ILoggerFactory loggers)
    {
        if (model is not DokiDokiProfile profile) throw new ArgumentException("Not a DokiDoki model.", nameof(model));
        return new DokiDokiSession(target, profile.Family, allowOutput, loggers);
    }

    private CupAdvertisement ToCup(DokiDokiAdvertisement device) => new(
        Id, device.Address, device.IsRandomAddress, device.Name,
        device.Family is { } family ? DokiDokiProfile.Models.First(m => m.Family == family) : null,
        device.Rssi);

    // RSSI-only updates are not reported, so the list is not rebuilt for every advertisement.
    private void OnSeen(object? sender, DokiDokiAdvertisement device)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_listed.TryGetValue(device.Address.Value, out var known) || known != (device.Name, device.Family);
            if (changed) _listed[device.Address.Value] = (device.Name, device.Family);
        }
        if (changed) DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAborted(object? sender, string error) => ScanAborted?.Invoke(this, error);

    public void Dispose()
    {
        _watcher.Seen -= OnSeen;
        _watcher.Aborted -= OnAborted;
        _watcher.Dispose();
    }
}
