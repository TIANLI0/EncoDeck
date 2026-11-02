// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>Where a piece of device state came from.</summary>
public enum StateSource
{
    /// <summary>The device pushed it unsolicited (e.g. touch control changed ANC).</summary>
    DeviceNotification,

    /// <summary>Answer to an explicit query.</summary>
    QueryResponse,

    /// <summary>The device acknowledged a command we sent and the state was applied.</summary>
    CommandConfirmation,

    /// <summary>Optimistic local value awaiting device confirmation.</summary>
    Optimistic,

    /// <summary>Restored from a previous session; never shown as live.</summary>
    Cache,

    /// <summary>Written to a device that never reports it back: the value this PC last sent.</summary>
    Sent,
}

/// <summary>A value together with when and how it was learned. Absence (null) means unknown.</summary>
public sealed record Observed<T>(T Value, DateTimeOffset Timestamp, StateSource Source)
{
    public TimeSpan Age(DateTimeOffset now) => now - Timestamp;

    /// <summary>
    /// True when the value should be presented as "last read" rather than live:
    /// it came from a cache, or it is older than <paramref name="maxAge"/>.
    /// </summary>
    public bool IsStale(DateTimeOffset now, TimeSpan maxAge) =>
        Source == StateSource.Cache || Age(now) > maxAge;

    public bool IsConfirmed => Source is StateSource.DeviceNotification or StateSource.QueryResponse or StateSource.CommandConfirmation;
}

public static class Observed
{
    public static Observed<T> From<T>(T value, DateTimeOffset timestamp, StateSource source) => new(value, timestamp, source);
}
