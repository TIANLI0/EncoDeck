// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Platform.A2dpCodec;
using EncoDeck.Platform.Lhdc;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Projects which driver carries the active device's A2DP (<see cref="A2dpSinkDriver"/>), and the LHDC component's
/// bitrate while it does, into the device state. Windows logs codecs only for its own driver, so without this the tag
/// kept the codec Windows streamed before the LHDC component took the earbuds over. That component swaps the driver when
/// it applies LHDC or restores Windows audio, so it is read every few seconds while a link is up (a few CfgMgr32 calls
/// and a registry read, no administrator rights) and right away when the device or its link changes. Without a
/// Bluetooth link there is nothing to read and no timer runs; a state change wakes it.
/// </summary>
public sealed class A2dpDriverCompanion : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IDeviceStateStore _store;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly ConditionalPoll _poll;
    private string? _logged;

    public A2dpDriverCompanion(IDeviceStateStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
        _poll = new ConditionalPoll("A2DP driver poll", PollInterval, () => Target(_store.Current) is not null, Check, logger);
    }

    public void Start()
    {
        _store.StateChanged += OnStateChanged;
        _poll.Start();
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        ulong? target = Target(e.Current);
        if (target == Target(e.Previous)) return;
        // Another device, or a link that just came up: read it now rather than on the next tick.
        if (target is not null) _ = Task.Run(_poll.CheckNow);
        // Resume polling for the new link, or let one more check clear what the dropped one projected.
        _poll.Wake();
    }

    /// <summary>The Bluetooth device whose driver matters: the active one, while a link is up.</summary>
    private static ulong? Target(DeviceState state) =>
        state.Device is { Transport: DeviceTransport.Bluetooth } device && state.Connection.IsLinkUp() ? device.Address.Value : null;

    private void Check()
    {
        lock (_gate)
        {
            ulong? target = Target(_store.Current);
            string? driver = target is { } address ? A2dpSinkDriver.ServiceFor(address) : null;
            int kbps = string.Equals(driver, CodecDisplay.LhdcA2dpDriver, StringComparison.OrdinalIgnoreCase) ? LhdcComponent.ActiveBitrate() : 0;
            if (driver != _logged)
            {
                _logged = driver;
                if (driver is not null) _logger.LogInformation("A2DP driver of the active device: {Driver}", driver);
            }
            // Only onto the device it was read for: the active device may have changed meanwhile (that change reads again).
            _store.TryUpdate(null, s => Target(s) != target || s.A2dpDriver == driver && s.LhdcKbps == kbps
                ? s : s with { A2dpDriver = driver, LhdcKbps = kbps });
        }
    }

    public void Dispose()
    {
        _store.StateChanged -= OnStateChanged;
        _poll.Dispose();
    }
}
