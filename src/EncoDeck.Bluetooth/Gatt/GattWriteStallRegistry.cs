// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// Remembers, per earbuds, the LE identity whose control write last stalled. ATT allows one outstanding
/// request per bearer: a write request the peer never answers blocks that identity's GATT traffic until
/// the 30 s ATT transaction timeout, so the next attempt should try another connected identity first.
/// Kept in memory only, so no Bluetooth address is written to the settings file.
/// </summary>
internal sealed class GattWriteStallRegistry
{
    /// <summary>Long enough to cover the ATT transaction timeout and one reconnect backoff.</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromSeconds(60);

    public static GattWriteStallRegistry Shared { get; } = new();

    private readonly ConcurrentDictionary<ulong, (LeTarget Target, long At)> _stalled = new();

    public void MarkStalled(ulong earbuds, LeTarget target, long nowMs) => _stalled[earbuds] = (target, nowMs);

    /// <summary>Forgets the stall once <paramref name="target"/> answered again.</summary>
    public void MarkHealthy(ulong earbuds, LeTarget target)
    {
        if (_stalled.TryGetValue(earbuds, out var entry) && entry.Target == target)
            _stalled.TryRemove(new KeyValuePair<ulong, (LeTarget, long)>(earbuds, entry));
    }

    public LeTarget? RecentlyStalled(ulong earbuds, long nowMs) =>
        _stalled.TryGetValue(earbuds, out var entry) && nowMs - entry.At < (long)Memory.TotalMilliseconds ? entry.Target : null;

    /// <summary>Keeps the connected order but moves the recently stalled identity last; it is still tried.</summary>
    public static IReadOnlyList<LeTarget> Order(IReadOnlyList<LeTarget> connected, LeTarget? stalled) =>
        stalled is null || !connected.Contains(stalled) ? connected : [.. connected.Where(t => t != stalled), stalled];
}
