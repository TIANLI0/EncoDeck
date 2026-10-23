// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.DokiDoki;

/// <summary>Known DokiDoki actuator command families.</summary>
public enum DokiDokiFamily
{
    Meta2,
    Bh2Pro,
}

/// <summary>Actuator channels exposed by DokiDoki devices.</summary>
public enum DokiDokiChannel
{
    Vibration,
    Linear,
    Rotary,
}

/// <summary>Name matching rules used by the Yilia DokiDoki profile catalog.</summary>
public static class DokiDokiNames
{
    /// <summary>Matches any advertised name carrying one of the known DokiDoki prefixes.</summary>
    public static bool IsSupportedDeviceName(string? deviceName)
    {
        string normalized = Normalize(deviceName);
        return normalized.StartsWith(DokiDokiConstants.TryFunAliasPrefix, StringComparison.Ordinal)
            || normalized.StartsWith(DokiDokiConstants.DokiDokiPrefix, StringComparison.Ordinal)
            || normalized.StartsWith(DokiDokiConstants.TryFunPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves an exact DK-META2 or DK-BH2PRO profile name. As in Yilia, only the TF- alias is
    /// rewritten to DK-; other supported prefixes do not identify a family by themselves.
    /// </summary>
    public static DokiDokiFamily? DetectFamily(string? deviceName)
    {
        string normalized = Normalize(deviceName);
        if (normalized.StartsWith(DokiDokiConstants.TryFunAliasPrefix, StringComparison.Ordinal))
            normalized = DokiDokiConstants.DokiDokiPrefix + normalized[DokiDokiConstants.TryFunAliasPrefix.Length..];

        return normalized switch
        {
            DokiDokiConstants.Meta2DeviceName => DokiDokiFamily.Meta2,
            DokiDokiConstants.Bh2ProDeviceName => DokiDokiFamily.Bh2Pro,
            _ => null,
        };
    }

    private static string Normalize(string? deviceName) =>
        (deviceName ?? string.Empty).Trim().ToUpperInvariant().TrimEnd('\0');
}
