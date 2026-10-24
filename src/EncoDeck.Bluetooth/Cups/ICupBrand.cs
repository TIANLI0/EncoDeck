// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.Cups;

/// <summary>A nearby cup device seen while scanning.</summary>
/// <param name="Brand">Id of the <see cref="ICupBrand"/> that found it.</param>
/// <param name="Model">Null when the advertisement does not identify the model; the user must pick one.</param>
public sealed record CupAdvertisement(
    string Brand,
    BluetoothAddress Address,
    bool IsRandomAddress,
    string? Name,
    ICupProfile? Model,
    short Rssi);

/// <summary>
/// One connection to a cup device, independent of the headset <c>DeviceManager</c>. Disposing
/// stops every channel before the connection closes.
/// </summary>
public interface ICupSession : IActuatorCommands, IAsyncDisposable
{
    CupAdvertisement Target { get; }

    DeviceIdentity Identity { get; }

    ICupProfile Profile { get; }

    /// <summary>Connection state and last error of this session only.</summary>
    DeviceStateStore Store { get; }

    void Start();

    /// <summary>User-initiated retry after the connection became unavailable.</summary>
    void Reconnect();
}

/// <summary>A brand in the cup category: how to find its devices and connect to them.</summary>
public interface ICupBrand : IDisposable
{
    /// <summary>Stable id, matching <see cref="ICupProfile.Brand"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Models to offer when an advertisement does not identify one.</summary>
    IReadOnlyList<ICupProfile> Models { get; }

    bool IsScanning { get; }

    IReadOnlyList<CupAdvertisement> Devices { get; }

    /// <summary>A device appeared or its name / model changed. Raised on arbitrary threads.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Windows stopped the scan on its own (radio off, access denied).</summary>
    event EventHandler<string>? ScanAborted;

    /// <summary>Starts a fresh scan; devices seen earlier are forgotten.</summary>
    void StartScan();

    void StopScan();

    /// <param name="model">One of <see cref="Models"/>; the caller has confirmed it with the user.</param>
    ICupSession CreateSession(CupAdvertisement target, ICupProfile model, Func<bool> allowOutput, ILoggerFactory loggers);
}
