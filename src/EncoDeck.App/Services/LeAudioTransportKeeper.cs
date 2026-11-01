// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo.Models;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Puts earbuds EncoDeck switches between LC3 and Classic back on LC3 when they lost it on their own. They drop their LE
/// links whenever this PC's Classic link ends (EncoDeck restarted or updated, sleep, the case closed) and Windows then
/// falls back to Classic audio. When the user's choice is LE Audio (Windows' preference, set by the switch) and LC3
/// is still missing a while after control reconnected, this runs the same switch once per connection. Only after the
/// one-time permission was granted, so it never shows a prompt by itself. On LE Audio it also removes a Classic output
/// left next to it.
/// </summary>
public sealed class LeAudioTransportKeeper : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    private readonly IDeviceStateStore _store;
    private readonly IDeviceManager _devices;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pending;
    private ulong? _attempted;
    private bool _running;
    // Earbuds already checked for a Classic output next to LE Audio during this LE Audio session.
    private ulong? _classicChecked;

    public LeAudioTransportKeeper(IDeviceStateStore store, IDeviceManager devices, ILogger logger)
    {
        _store = store;
        _devices = devices;
        _logger = logger;
    }

    public void Start() => _store.StateChanged += OnStateChanged;

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        var state = e.Current;
        ulong? candidate = NeedsLeAudio(state) ? state.Device!.Address.Value : null;
        ulong? onLeAudio = IsSwitchable(state) && state.IsLeAudio ? state.Device!.Address.Value : null;
        lock (_gate)
        {
            // The switch itself reconnects the earbuds; only that is not a new connection.
            if (_running) return;
            if (onLeAudio != _classicChecked)
            {
                _classicChecked = onLeAudio;
                if (onLeAudio is { } address) _ = RemoveClassicBesideLeAsync(address);
            }
            // A new connection may try again.
            if (!state.Connection.IsLinkUp()) _attempted = null;
            if (candidate is null || candidate == _attempted)
            {
                _pending?.Cancel();
                _pending = null;
                return;
            }
            if (_pending is not null) return;
            _pending = new CancellationTokenSource();
            _ = RecoverAsync(candidate.Value, _pending.Token);
        }
    }

    /// <summary>Set up for both (LE bonds plus the derived Classic bond), control up, and not playing over LE Audio.</summary>
    private static bool NeedsLeAudio(DeviceState state) =>
        state is { Connection: DeviceConnectionState.ControlReady, IsLeAudio: false } && IsSwitchable(state);

    private static bool IsSwitchable(DeviceState state) =>
        state is { Profile: OppoDeviceProfile { Definition.SupportsLeAudioPairing: true } }
        && state.LeAudioLinks is { PrimaryBonded: true, SecondaryBonded: true }
        && state.Device is { Transport: DeviceTransport.Bluetooth } device
        && LeAudioOnlyDevices.Find(device.DeviceId) is null;

    /// <summary>
    /// Once per LE Audio session: a Classic "Headphones" output next to LE Audio (left by a switch to Classic, or brought
    /// by the derived Classic bond) would move the earbuds to A2DP when chosen; remove its services.
    /// </summary>
    private async Task RemoveClassicBesideLeAsync(ulong address)
    {
        try
        {
            // The LE Audio output can appear a few seconds before Windows finishes with Classic.
            await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
            if (await LeAudioPairing.RemoveClassicAudioBesideLeAsync(address, _lifetime.Token).ConfigureAwait(false))
                _logger.LogInformation("Removed the Classic audio output next to LE Audio");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not remove the Classic audio output next to LE Audio");
        }
    }

    private async Task RecoverAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Grace, cancellationToken).ConfigureAwait(false);
            // The user's choice: the switch sets this preference, and Settings' "Use LE Audio when available" is it too.
            if (WindowsAudioTransport.ReadPreference() != AudioTransportPreference.LeAudio || !WindowsAudioTransport.CanRestartService) return;
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested) return;
                _attempted = address;
                _running = true;
            }
            _logger.LogInformation("LE Audio was chosen but is not playing; switching back to LE Audio");
            try
            {
                // Once started it runs to the end: stopping halfway could leave no audio at all.
                var result = await LeAudioPairing.SwitchTransportAsync(address, toLeAudio: true, null, _lifetime.Token,
                    token => _devices.ReconnectClassicLinkAsync(address, token)).ConfigureAwait(false);
                _logger.LogInformation("Automatic switch back to LE Audio: {Outcome}", result.Outcome);
            }
            finally
            {
                lock (_gate) _running = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Automatic switch back to LE Audio failed");
        }
        finally
        {
            lock (_gate)
            {
                if (_pending?.Token == cancellationToken)
                {
                    _pending.Dispose();
                    _pending = null;
                }
            }
        }
    }

    public void Dispose()
    {
        _store.StateChanged -= OnStateChanged;
        _lifetime.Cancel();
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = null;
        }
    }
}
