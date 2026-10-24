// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.Cups;
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.DokiDoki;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth;

namespace EncoDeck.Bluetooth.DokiDoki;

/// <summary>
/// One experimental DokiDoki connection, independent of the headset <see cref="DeviceManager"/>:
/// it has its own <see cref="Store"/> so it never replaces the active headset's state. The link is
/// "up" for as long as the session exists; the reused <see cref="ControlChannelSupervisor"/> handles
/// bounded retries. Disposing stops every channel before the connection closes.
/// </summary>
/// <remarks>
/// The target address is fixed for the session. Devices that rotate resolvable private addresses
/// must be rediscovered and a new session started.
/// </remarks>
public sealed class DokiDokiSession : ICupSession
{
    private readonly SessionLink _link = new();
    private readonly ControlChannelSupervisor _supervisor;
    private int _disposed;

    public DokiDokiSession(
        CupAdvertisement target,
        DokiDokiFamily family,
        Func<bool> allowExperimental,
        ILoggerFactory? loggers = null,
        ReconnectPolicy? policy = null)
    {
        Target = target;
        Profile = new DokiDokiProfile(family);
        Identity = new DeviceIdentity(
            "BluetoothLE#dokidoki-" + target.Address.Value.ToString("X12", CultureInfo.InvariantCulture),
            target.Name ?? Profile.DisplayName,
            target.Address);
        Store.Reset(DeviceState.Empty with { Device = Identity, Profile = Profile, Connection = DeviceConnectionState.ControlConnecting });
        var connector = new Connector(target.Address.Value, target.IsRandomAddress ? BluetoothAddressType.Random : BluetoothAddressType.Public);
        _supervisor = new ControlChannelSupervisor(Identity, Profile, _link, connector, Store, policy, allowExperimental, loggers ?? NullLoggerFactory.Instance);
    }

    public CupAdvertisement Target { get; }

    public DeviceIdentity Identity { get; }

    public DokiDokiProfile Profile { get; }

    ICupProfile ICupSession.Profile => Profile;

    /// <summary>Connection state and last error of this session only.</summary>
    public DeviceStateStore Store { get; } = new();

    public IReadOnlyList<ActuatorChannel> Channels => Profile.Channels;

    private IActuatorCommands? Actuator => _supervisor.Controller as IActuatorCommands;

    public void Start() => _supervisor.Start();

    /// <summary>User-initiated retry after the connection became unavailable.</summary>
    public void Reconnect() => _supervisor.RequestReconnect();

    /// <summary>Call after the system resumes from sleep: the old GATT connection is usually dead.</summary>
    public void NotifySystemResumed() => _supervisor.RequestReconnect();

    public Task<CommandResult> SetIntensityAsync(ActuatorChannel channel, int strength, CancellationToken cancellationToken = default) =>
        Actuator is { } actuator ? actuator.SetIntensityAsync(channel, strength, cancellationToken) : Task.FromResult(CommandResult.NotConnected);

    public Task<CommandResult> StopAllAsync() =>
        Actuator is { } actuator ? actuator.StopAllAsync() : Task.FromResult(CommandResult.NotConnected);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // The supervisor disposes the controller, whose closing sequence writes the final stop.
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _link.Dispose();
        Store.TryUpdate(Identity.DeviceId, s => s with { Connection = DeviceConnectionState.Disconnected });
    }

    private sealed class SessionLink : ILinkStateSource
    {
        public bool IsConnected => true;

        public event EventHandler<bool>? ConnectionChanged
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Connector(ulong address, BluetoothAddressType addressType) : IChannelConnector
    {
        public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken) =>
            DokiDokiGattByteChannel.OpenAsync(address, addressType, cancellationToken);
    }
}
