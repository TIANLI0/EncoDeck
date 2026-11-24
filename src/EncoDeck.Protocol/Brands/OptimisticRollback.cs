// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// Undoes a failed command's optimistic values. Only fields the command itself made optimistic are touched,
/// and only while they are still optimistic: a value the device confirmed or reported meanwhile wins, and so
/// does another command's optimistic value for a different field.
/// </summary>
internal static class OptimisticRollback
{
    /// <param name="before">The state just before the command applied its optimistic values.</param>
    /// <param name="optimistic">The state with the command's optimistic values applied.</param>
    /// <param name="current">The state to roll back.</param>
    public static DeviceState Restore(DeviceState before, DeviceState optimistic, DeviceState current)
    {
        if (ReferenceEquals(before, optimistic)) return current;
        return current with
        {
            NoiseControl = Field(before.NoiseControl, optimistic.NoiseControl, current.NoiseControl),
            Equalizer = Field(before.Equalizer, optimistic.Equalizer, current.Equalizer),
            CustomEqualizers = Field(before.CustomEqualizers, optimistic.CustomEqualizers, current.CustomEqualizers),
            Spatial = Field(before.Spatial, optimistic.Spatial, current.Spatial),
            GameSound = Field(before.GameSound, optimistic.GameSound, current.GameSound),
            PromptVolume = Field(before.PromptVolume, optimistic.PromptVolume, current.PromptVolume),
            Gain = Field(before.Gain, optimistic.Gain, current.Gain),
            BassEngine = Field(before.BassEngine, optimistic.BassEngine, current.BassEngine),
            KeyFunctions = Field(before.KeyFunctions, optimistic.KeyFunctions, current.KeyFunctions),
            NoiseCycle = Field(before.NoiseCycle, optimistic.NoiseCycle, current.NoiseCycle),
            HeadMotionType = Field(before.HeadMotionType, optimistic.HeadMotionType, current.HeadMotionType),
            Toggles = Map(before.Toggles, optimistic.Toggles, current.Toggles),
            Options = Map(before.Options, optimistic.Options, current.Options),
        };
    }

    private static bool MadeOptimistic<T>(Observed<T>? before, Observed<T>? optimistic) =>
        optimistic is { Source: StateSource.Optimistic } && !ReferenceEquals(before, optimistic);

    private static Observed<T>? Field<T>(Observed<T>? before, Observed<T>? optimistic, Observed<T>? current) =>
        MadeOptimistic(before, optimistic) && current?.Source == StateSource.Optimistic ? before : current;

    private static ImmutableDictionary<TKey, Observed<TValue>> Map<TKey, TValue>(
        ImmutableDictionary<TKey, Observed<TValue>> before,
        ImmutableDictionary<TKey, Observed<TValue>> optimistic,
        ImmutableDictionary<TKey, Observed<TValue>> current)
        where TKey : notnull
    {
        if (ReferenceEquals(before, optimistic)) return current;
        var result = current;
        foreach (var (key, value) in optimistic)
        {
            var old = before.GetValueOrDefault(key);
            if (!MadeOptimistic(old, value) || current.GetValueOrDefault(key)?.Source != StateSource.Optimistic) continue;
            result = old is null ? result.Remove(key) : result.SetItem(key, old);
        }
        return result;
    }
}
