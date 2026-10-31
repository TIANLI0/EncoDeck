// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;

namespace EncoDeck.Bluetooth.Rfcomm;

/// <summary>Opens the first matching control service over RFCOMM, GAIA GATT or OPPO GATT.</summary>
public sealed class RfcommChannelConnector(IDeviceProfileRegistry profiles, ILogger<RfcommChannelConnector>? logger = null) : IChannelConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly ILogger _logger = logger ?? NullLogger<RfcommChannelConnector>.Instance;

    public async Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
    {
        // LE-only earbuds have no Classic pairing any more: control runs over OPPO GATT alone.
        if (LeAudioOnlyDevices.Find(deviceId) is { } leOnly && serviceUuids.Any(OppoServiceUuids.IsCandidate)
            && !await IsBackOnClassicAsync(leOnly, cancellationToken).ConfigureAwait(false))
        {
            return await OppoControlChannelRouter.OpenAsync(
                async () =>
                {
                    if (LeAudioLink.IsPairingChanging(leOnly.Address))
                        throw new InvalidOperationException("The earbuds' pairing is being changed.");
                    if (!await LeAudioLink.IsBondedAsync(leOnly.Address, cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("LE Audio 配对已不存在，请重新配对耳机。");
                    // Windows derives the Classic pairing again whenever the earbuds re-bond over LE.
                    if (!LeAudioLink.IsPairingOperationRunning(leOnly.Address))
                        await RemoveClassicPairingAsync(leOnly.Address, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                () => OppoGattByteChannel.OpenAsync(leOnly.Address, cancellationToken),
                () => Task.FromResult(ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The earbuds are not connected over LE Audio."))).ConfigureAwait(false);
        }

        BluetoothDevice? device;
        try
        {
            device = await BluetoothDevice.FromIdAsync(deviceId).AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, ex.Message);
        }
        if (device is null)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "Device not found.");
        }

        using (device)
        {
            if (serviceUuids.Any(OppoServiceUuids.IsCandidate))
            {
                bool guardsPartialLeBond = GuardsPartialLeBond(profiles.Match(new DeviceIdentity(deviceId, device.Name, new BluetoothAddress(device.BluetoothAddress))));
                return await OppoControlChannelRouter.OpenAsync(
                    async () =>
                    {
                        if (LeAudioLink.IsPairingChanging(device.BluetoothAddress))
                            throw new InvalidOperationException("The earbuds' pairing is being changed.");
                        bool bonded = await LeAudioLink.IsBondedAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                        if (!bonded && LeAudioLink.IsPreferred(device.BluetoothAddress))
                            throw new InvalidOperationException("LE Audio 配对未完成，请重新进入配对模式后再试一次。");
                        if (!bonded && guardsPartialLeBond && await LeAudioLink.HasSecondaryBondAsync(device.BluetoothAddress, cancellationToken, device.Name).ConfigureAwait(false))
                            throw new InvalidOperationException("LE Audio 配对不完整：主耳 LE 绑定缺失，请重新切换到 LE Audio。");
                        if (LeAudioLink.IsPairingChanging(device.BluetoothAddress))
                            throw new InvalidOperationException("The earbuds' pairing is being changed.");
                        // On LE Audio the CTKD Classic pairing only competes with LC3; a running switch removes it itself.
                        if (bonded && guardsPartialLeBond && !LeAudioLink.IsPairingOperationRunning(device.BluetoothAddress))
                            await RemoveClassicPairingAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                        return bonded;
                    },
                    () => OppoGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken),
                    () => OpenClassicAsync(device, deviceId, serviceUuids, cancellationToken)).ConfigureAwait(false);
            }
            return await OpenClassicAsync(device, deviceId, serviceUuids, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether another LE bond without the primary's means a half-finished LE Audio switch that Classic
    /// must not paper over. Only models EncoDeck can switch to LE Audio are guarded: Swift Pair leaves an
    /// LE bond next to a Classic-only pair (OPPO Enco Air5s), and blocking RFCOMM there left it uncontrollable.
    /// </summary>
    internal static bool GuardsPartialLeBond(IDeviceProfile? profile) =>
        profile is OppoDeviceProfile { Definition.SupportsLeAudioPairing: true };

    /// <summary>
    /// The user removed the LE bonds outside EncoDeck and paired the earbuds over Classic again: forget them as
    /// LE-only. Unknown states keep them LE-only, which never opens Classic.
    /// </summary>
    private static async Task<bool> IsBackOnClassicAsync(LeAudioOnlyDevice device, CancellationToken cancellationToken)
    {
        try
        {
            if (LeAudioLink.IsPairingOperationRunning(device.Address)) return false;
            if (await LeAudioLink.IsBondedAsync(device.Address, cancellationToken).ConfigureAwait(false)) return false;
            if (await LeAudioLink.HasSecondaryBondAsync(device.Address, cancellationToken, device.Name).ConfigureAwait(false)) return false;
            using var classic = await BluetoothDevice.FromIdAsync(device.DeviceId).AsTask(cancellationToken);
            if (classic?.DeviceInformation.Pairing.IsPaired != true) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
        LeAudioOnlyDevices.Remove(device.Address);
        return true;
    }

    private async Task RemoveClassicPairingAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            await LeAudioPairing.RemoveClassicPairingAsync(address, message => _logger.LogInformation("{Message}", message), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // GATT control still opens; the next open tries again.
            _logger.LogInformation("Could not remove the Classic pairing of LE Audio earbuds (0x{Result:X8})", ex.HResult);
        }
    }

    private async Task<ChannelOpenResult> OpenClassicAsync(BluetoothDevice device, string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
    {
        ChannelOpenResult? gattFailure = null, connectFailure = null;
        var access = await device.RequestAccessAsync().AsTask(cancellationToken);
        if (access != DeviceAccessStatus.Allowed)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"Device access: {access}");
        }

        var identity = new DeviceIdentity(deviceId, device.Name, new BluetoothAddress(device.BluetoothAddress));
        if (serviceUuids.Contains(MoondropConstants.SppServiceId) &&
            profiles.Match(identity) is MoondropProfile { RfcommChannel: { } channel })
        {
            var result = await BluetoothSocketByteChannel.OpenAsync(device.BluetoothAddress, channel, cancellationToken).ConfigureAwait(false);
            if (result.Status == ChannelOpenStatus.Opened) return result;
            connectFailure = result;
            _logger.LogInformation("Known MOONDROP RFCOMM channel failed: {Detail}", result.Detail);
        }

        foreach (var uuid in serviceUuids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EncoDeck.Protocol.Brands.GattStreamEndpoints.Find(uuid) is { } stream)
            {
                var result = await StreamGattByteChannel.OpenAsync(device.BluetoothAddress, device.Name, stream, _logger, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                gattFailure = result;
                _logger.LogInformation("{Endpoint} connect failed: {Detail}", stream.Name, result.Detail);
                continue;
            }
            if (EdifierConstants.IsGattService(uuid))
            {
                var result = await EdifierGattByteChannel.OpenAsync(device.BluetoothAddress, uuid, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                gattFailure = result;
                _logger.LogInformation("EDIFIER GATT connect failed: {Detail}", result.Detail);
                continue;
            }
            if (uuid == SonyAkgProfiles.BleServiceUuid)
            {
                // Both RFCOMM services failed with the classic link up: try the same MDR GATT channel the
                // LE Audio path uses (mos9527/SonyHeadphonesClient PR #43 controls WH-1000XM6 this way).
                var result = await SonyGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                gattFailure = result;
                _logger.LogInformation("Sony MDR GATT connect failed: {Detail}", result.Detail);
                continue;
            }
            if (SonyAkgProfiles.IsControlService(uuid)
                && await TryOpenSonyControlAsync(device, cancellationToken).ConfigureAwait(false) is { } sonyControl)
            {
                // Either the LE Audio GATT transport opened (no classic RFCOMM exists in that mode),
                // or this link state cannot offer a control endpoint at all.
                return sonyControl;
            }
            if (uuid == SennheiserProfile.GattServiceId)
            {
                var result = await GaiaGattByteChannel.OpenSennheiserAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                gattFailure = result;
                _logger.LogInformation("Sennheiser GAIA GATT connect failed: {Detail}", result.Detail);
                continue;
            }
            if (uuid == MoondropConstants.GattServiceId)
            {
                // The GAIA v1 service is shared by MOONDROP and vivo; label the channel after the matched brand.
                string brand = profiles.Match(identity) switch
                {
                    MoondropProfile => "MOONDROP",
                    EncoDeck.Protocol.Brands.Vivo.VivoDeviceProfile => "vivo",
                    _ => "Generic",
                };
                var result = await GaiaGattByteChannel.OpenAsync(device.BluetoothAddress, brand, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChannelOpenStatus.Opened) return result;
                gattFailure = result;
                _logger.LogInformation("{Brand} GAIA GATT connect failed: {Detail}", brand, result.Detail);
                continue;
            }
            RfcommDeviceServicesResult services;
            using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lookupTimeout.CancelAfter(ConnectTimeout);
            try
            {
                // Sony's control service can change after reconnecting / changing Bluetooth mode.
                // Resolve its current SDP endpoint rather than repeatedly reopening a stale channel.
                var cacheMode = SonyAkgProfiles.IsControlService(uuid) ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
                _logger.LogInformation("SDP lookup starting: {Service}, cache {CacheMode}", uuid, cacheMode);
                services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(uuid), cacheMode).AsTask(lookupTimeout.Token);
                if (services.Services.Count == 0 && cacheMode == BluetoothCacheMode.Cached)
                {
                    services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(uuid), BluetoothCacheMode.Uncached).AsTask(lookupTimeout.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("SDP lookup for {Service} timed out", uuid);
                connectFailure = ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"SDP lookup for {uuid:D} timed out.");
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("SDP lookup for {Service} failed: 0x{HResult:X8}", uuid, ex.HResult);
                continue;
            }

            if (services.Error != BluetoothError.Success)
            {
                _logger.LogInformation("SDP lookup for {Service}: {Error}", uuid, services.Error);
                if (services.Error is BluetoothError.DeviceNotConnected or BluetoothError.RadioNotAvailable)
                {
                    // Unless a GATT service later in the list can still answer without Classic (one
                    // earbud worn on LE Audio has no Classic link to page).
                    if (services.Error == BluetoothError.RadioNotAvailable || !serviceUuids.Any(IsClassicFreeFallback))
                        return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, services.Error.ToString());
                }
                continue;
            }

            try
            {
                // A service whose SDP record carries no protocol descriptor (RFCOMM:00000000:...)
                // cannot be opened: Windows would block for the full connect timeout and fail.
                var resolvable = services.Services.Where(s => ResolveRfcommChannel(s.ConnectionServiceName) > 0).ToList();
                if (SonyAkgProfiles.IsControlService(uuid) && resolvable.Count == 0)
                {
                    var diagnosis = await DiagnoseUnavailableSonyControlAsync(device, cancellationToken).ConfigureAwait(false);
                    if (diagnosis is not null) return diagnosis;
                    continue;
                }

                // Fall back to the raw list only when nothing resolved (non-Sony services).
                var candidates = resolvable.Count == 0 ? services.Services : resolvable;
                foreach (var service in candidates)
                {
                    var result = await TryConnectAsync(service, uuid, cancellationToken).ConfigureAwait(false);
                    if (result.Status == ChannelOpenStatus.Opened) return result;
                    connectFailure = result;
                    _logger.LogInformation("RFCOMM connect to {Service} failed: {Detail}", uuid, result.Detail);
                }
            }
            finally
            {
                foreach (var service in services.Services) service.Dispose();
            }
        }
        // A control socket that exists but failed to connect is usually busy right after audio
        // connects; report it (retryable) rather than a permanent GATT or SDP outcome.
        return connectFailure ?? gattFailure ?? ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "No candidate control service could be opened.");
    }

    /// <summary>Whether <paramref name="uuid"/> names an RFCOMM service; the others are GATT services opened over LE.</summary>
    internal static bool IsRfcommService(Guid uuid) =>
        EncoDeck.Protocol.Brands.GattStreamEndpoints.Find(uuid) is null && !EdifierConstants.IsGattService(uuid)
        && uuid != SonyAkgProfiles.BleServiceUuid && uuid != SennheiserProfile.GattServiceId && uuid != MoondropConstants.GattServiceId;

    private static bool IsClassicFreeFallback(Guid uuid) =>
        uuid == SennheiserProfile.GattServiceId || EncoDeck.Protocol.Brands.GattStreamEndpoints.Find(uuid) is { BondedLeIdentities: true };

    /// <summary>RFCOMM channel of the Sony MDR v2 control service on WH/WF-1000XM5 and XM6 class hardware.</summary>
    internal const int SonyMdrV2Channel = 9;

    /// <summary>Reads the RFCOMM channel out of <c>RFCOMM:00000009:{uuid}</c>; 0 means unresolved.</summary>
    internal static int ResolveRfcommChannel(string? serviceName)
    {
        if (string.IsNullOrEmpty(serviceName)) return 0;
        const string marker = "RFCOMM:";
        var at = serviceName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return 0;
        var start = at + marker.Length;
        var end = serviceName.IndexOf(':', start);
        if (end < 0) end = serviceName.Length;
        if (end <= start) return 0;
        var digits = serviceName.AsSpan(start, end - start);
        return int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var channel)
               && channel is > 0 and <= 30
            ? channel
            : 0;
    }

    /// <summary>
    /// Last-resort channel for models whose MDR v2 channel is verified independently of Windows' SDP,
    /// used only when the classic link is up but Windows still reports an unresolved channel.
    /// </summary>
    /// <remarks>
    /// WH-1000XM5 and WH-1000XM6 both publish their MDR control service with
    /// <c>RFCOMM:00000000</c>, so SDP alone never yields a channel; the real channel 9 is verified on
    /// hardware for both (WH-1000XM5: protocol-version ACK over channel 9).
    /// </remarks>
    internal static int? KnownSonyControlChannel(string? deviceName) =>
        deviceName is not null
        && (deviceName.Contains("WH-1000XM5", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("WH-1000XM6", StringComparison.OrdinalIgnoreCase))
            ? SonyMdrV2Channel
            : null;

    private const string SonyLeAudioOnly = "The headset is linked over LE Audio and its control service cannot be opened in this mode.";

    /// <summary>The headset is on LE Audio and no control transport opened; the UI explains how to switch it to Classic.</summary>
    private static ChannelOpenResult SonyLeAudioOnlyFailure(string? reason) =>
        ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, reason is { Length: > 0 } ? $"{SonyLeAudioOnly} LE Audio GATT: {reason}" : SonyLeAudioOnly)
            with { ResourceKey = "Error_SonyLeAudioControl" };

    /// <summary>
    /// Opens the Sony control channel on whichever transport the headset currently offers. A headset
    /// linked over LE Audio exposes no classic RFCOMM endpoint, but carries the same MDR v2 stream on
    /// its LE Audio control service, so that transport is tried before reporting the mode mismatch.
    /// Returns null when a classic RFCOMM attempt is still worth making.
    /// </summary>
    private async Task<ChannelOpenResult?> TryOpenSonyControlAsync(BluetoothDevice device, CancellationToken cancellationToken)
    {
        if (device.ConnectionStatus == BluetoothConnectionStatus.Connected) return null;

        bool leConnected;
        try
        {
            var le = await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress).AsTask(cancellationToken).ConfigureAwait(false);
            if (le is null) return null;
            leConnected = le.ConnectionStatus == BluetoothConnectionStatus.Connected;
            le.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("LE link probe for {Address} failed: 0x{HResult:X8}", device.BluetoothAddress, ex.HResult);
            return null;
        }
        if (!leConnected) return null;

        // A live LE link with no classic link is the LE Audio identity: the same MDR v2 stream is
        // exposed on the headset's LE Audio control service, so use it instead of the RFCOMM socket.
        var gatt = await SonyGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
        // A previous instance can still hold the GATT session for a few seconds after it exits,
        // which surfaces as AccessDenied; retry briefly instead of waiting for the supervisor backoff.
        for (var attempt = 0; attempt < 3 && gatt.Status == ChannelOpenStatus.AccessDenied; attempt++)
        {
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            gatt = await SonyGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
        }
        if (gatt.Status == ChannelOpenStatus.Opened)
        {
            _logger.LogInformation("Sony control channel opened over LE Audio GATT (classic link down).");
            return gatt;
        }
        _logger.LogInformation("Sony LE Audio GATT control channel unavailable: {Detail}", gatt.Detail);

        // Windows flips the profile set while a reconnect settles; give it one short chance to
        // bring the classic link up before declaring LE-Audio-only.
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        if (device.ConnectionStatus == BluetoothConnectionStatus.Connected) return null;

        _logger.LogWarning(
            "Sony control service unavailable: the headset is linked over LE Audio (classic link {Status}); RFCOMM cannot exist until it reconnects as Classic Audio",
            device.ConnectionStatus);
        return SonyLeAudioOnlyFailure(gatt.Detail);
    }

    /// <summary>
    /// Explains why the Sony control service cannot be reached, instead of reporting a bare
    /// "service not found": a headset linked over LE Audio exposes no classic RFCOMM endpoint.
    /// Returns null when the normal failure path should report instead.
    /// </summary>
    private async Task<ChannelOpenResult?> DiagnoseUnavailableSonyControlAsync(BluetoothDevice device, CancellationToken cancellationToken)
    {
        if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            // The classic link is up, so the channel exists; Windows' SDP cache is what is stale.
            if (KnownSonyControlChannel(device.Name) is not { } knownChannel) return null;
            _logger.LogInformation(
                "Sony SDP reports no resolvable channel although the classic link is up; trying the known RFCOMM channel {Channel}",
                knownChannel);
            var direct = await BluetoothSocketByteChannel.OpenAsync(device.BluetoothAddress, (uint)knownChannel, cancellationToken, "Sony MDR").ConfigureAwait(false);
            if (direct.Status == ChannelOpenStatus.Opened) return direct with { ServiceUuid = SonyAkgProfiles.PrimaryServiceUuid };
            _logger.LogInformation("Known Sony RFCOMM channel {Channel} failed: {Detail}", knownChannel, direct.Detail);
            // The RFCOMM endpoint is unusable even with the link up; the LE Audio control service
            // carries the identical MDR v2 stream and is the remaining transport.
            var gatt = await SonyGattByteChannel.OpenAsync(device.BluetoothAddress, cancellationToken).ConfigureAwait(false);
            if (gatt.Status == ChannelOpenStatus.Opened)
            {
                _logger.LogInformation("Sony control channel opened over LE Audio GATT (classic SDP unresolved).");
                return gatt;
            }
            _logger.LogInformation("Sony LE Audio GATT control channel unavailable: {Detail}", gatt.Detail);
            return null;
        }

        bool leConnected = false;
        try
        {
            var le = await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress).AsTask(cancellationToken).ConfigureAwait(false);
            if (le is not null)
            {
                leConnected = le.ConnectionStatus == BluetoothConnectionStatus.Connected;
                le.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("LE link probe for {Address} failed: 0x{HResult:X8}", device.BluetoothAddress, ex.HResult);
        }

        _logger.LogWarning(
            "Sony control service unreachable: classic link {Classic}, LE link {Le}. The headset has to be linked over Classic Audio for the RFCOMM control channel to exist.",
            device.ConnectionStatus,
            leConnected ? "connected" : "down");
        return leConnected
            ? SonyLeAudioOnlyFailure(null)
            : ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The headset has no Classic link, so its control channel does not exist.")
                with { ResourceKey = "Error_SonyClassicLinkDown" };
    }

    internal static SocketProtectionLevel ProtectionLevelFor(Guid uuid) => SonyAkgProfiles.IsControlService(uuid)
        // Sound Connect Rm.b uses createRfcommSocketToServiceRecord (secure RFCOMM).
        ? SocketProtectionLevel.BluetoothEncryptionWithAuthentication
        : SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication;

    private async Task<ChannelOpenResult> TryConnectAsync(RfcommDeviceService service, Guid uuid, CancellationToken cancellationToken)
    {
        var socket = new StreamSocket();
        socket.Control.KeepAlive = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            var protection = ProtectionLevelFor(uuid);
            _logger.LogInformation("RFCOMM connecting: {Service}, channel {Channel}, protection {Protection}", uuid, service.ConnectionServiceName, protection);
            await socket.ConnectAsync(
                    service.ConnectionHostName,
                    service.ConnectionServiceName,
                    protection)
                .AsTask(timeout.Token);
            var channel = new RfcommByteChannel(socket, $"RFCOMM {uuid:D}");
            return new ChannelOpenResult(ChannelOpenStatus.Opened, channel, uuid, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "Connect timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            socket.Dispose();
            var status = SocketError.GetStatus(ex.HResult) switch
            {
                SocketErrorStatus.HostIsDown or SocketErrorStatus.HostNotFound or SocketErrorStatus.ConnectionTimedOut => ChannelOpenStatus.DeviceUnreachable,
                _ => ChannelOpenStatus.ConnectFailed,
            };
            return ChannelOpenResult.Failed(status, $"{SocketError.GetStatus(ex.HResult)} (0x{ex.HResult:X8})");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
