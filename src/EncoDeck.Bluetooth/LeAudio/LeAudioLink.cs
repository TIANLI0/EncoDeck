// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>LE Audio state of a Classic-paired device's public address.</summary>
public static class LeAudioLink
{
    private static readonly ConcurrentDictionary<ulong, byte> PairingChanges = new();
    private static readonly ConcurrentDictionary<ulong, byte> PairingOperations = new();
    private static readonly ConcurrentDictionary<ulong, byte> PreferredLe = new();

    /// <summary>Marks that the user wants this pair on LE Audio; Classic must not be used as a fallback.</summary>
    public static void MarkPreferred(ulong address) => PreferredLe[address] = 0;

    public static bool IsPreferred(ulong address) => PreferredLe.ContainsKey(address);

    public static void ClearPreferred(ulong address) => PreferredLe.TryRemove(address, out _);

    internal static bool IsPairingChanging(ulong address) => PairingChanges.ContainsKey(address);

    /// <summary>A switch to or from LE Audio is running for these earbuds (it handles their pairings itself).</summary>
    internal static bool IsPairingOperationRunning(ulong address) => PairingOperations.ContainsKey(address);

    internal static IDisposable BeginPairingOperation(ulong address)
    {
        if (!PairingOperations.TryAdd(address, 0)) throw new InvalidOperationException("A pairing operation is already in progress for these earbuds.");
        return new PairingOperation(address);
    }

    private sealed class PairingOperation(ulong address) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) PairingOperations.TryRemove(address, out _);
        }
    }

    internal static IDisposable BeginPairingChange(ulong address)
    {
        if (!PairingChanges.TryAdd(address, 0)) throw new InvalidOperationException("A pairing change is already in progress for these earbuds.");
        return new PairingChange(address);
    }

    private sealed class PairingChange(ulong address) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) PairingChanges.TryRemove(address, out _);
        }
    }

    /// <summary>
    /// True when Windows has a bond with the LE identity at the same public address, i.e. the
    /// earbuds were switched to LE Audio on this PC (connected or not). Read the paired association
    /// endpoints, not a newly opened device's transient IsPaired snapshot during reconnect.
    /// Errors propagate: an unknown bond state must never authorize a Classic connection.
    /// </summary>
    public static async Task<bool> IsBondedAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        string selector = $"({BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(bluetoothAddress, BluetoothAddressType.Public)}) AND ({BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)})";
        var bonds = await DeviceInformation.FindAllAsync(selector, [], DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken).ConfigureAwait(false);
        return bonds.Count > 0;
    }

    /// <summary>
    /// True when the other earbud still has an LE bond. Together with a missing primary bond this
    /// means the LE Audio pairing is incomplete; opening Classic would silently fall back to A2DP.
    /// </summary>
    public static async Task<bool> HasSecondaryBondAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default, string? knownName = null)
    {
        // Unknown is not "no bond": errors must reach the router and block a Classic fallback.
        using var device = knownName is null
            ? await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false)
            : null;
        var others = await LeAudioPairing.FindSecondaryBondsAsync(bluetoothAddress, knownName ?? device?.Name, cancellationToken).ConfigureAwait(false);
        try
        {
            return others.Count > 0;
        }
        finally
        {
            foreach (var other in others) other.Dispose();
        }
    }

    /// <summary>
    /// True when Windows has a bond with the LE identity at the same public address and that earbud
    /// or the other bonded one is connected (earbuds on LE Audio keep only LE links; Classic stays down).
    /// </summary>
    public static async Task<bool> IsConnectedAsync(ulong bluetoothAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, BluetoothAddressType.Public).AsTask(cancellationToken).ConfigureAwait(false);
            if (device is null || !await IsBondedAsync(bluetoothAddress, cancellationToken).ConfigureAwait(false)) return false;
            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;
            var others = await LeAudioPairing.FindSecondaryBondsAsync(bluetoothAddress, device.Name, cancellationToken).ConfigureAwait(false);
            try { return others.Any(o => o.ConnectionStatus == BluetoothConnectionStatus.Connected); }
            finally { foreach (var other in others) other.Dispose(); }
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
