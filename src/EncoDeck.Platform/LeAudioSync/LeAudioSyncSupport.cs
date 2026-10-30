// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.Win32;

namespace EncoDeck.Platform.LeAudioSync;

/// <summary>Whether this PC can use the LE Audio sync guard at all (independent of which earbuds are connected).</summary>
public enum LeAudioSyncSupport
{
    Supported,
    /// <summary>No Bluetooth radio.</summary>
    NoRadio,
    /// <summary>The radio is not Intel's; the one-frame offset was only found on Intel.</summary>
    NotIntel,
    /// <summary>LE Audio does not run through Intel Smart Sound Technology's offload path, where the offset was measured.</summary>
    NoIntelOffload,
}

public static class LeAudioSyncEligibility
{
    // Services\<driver>\Enum lists the devices currently bound to a driver; Count is readable without elevation.
    private const string IntelLeAudioOffloadEnumKey = @"SYSTEM\CurrentControlSet\Services\IntcBtLE\Enum";

    public static LeAudioSyncSupport Check() => Evaluate(IntelBluetoothRadio.GetManufacturer, IntelLeAudioOffloadPresent);

    /// <summary>The driver is only queried when the radio is Intel's.</summary>
    internal static LeAudioSyncSupport Evaluate(Func<ushort?> manufacturer, Func<bool> intelOffload)
    {
        ushort? id = manufacturer();
        if (id is null) return LeAudioSyncSupport.NoRadio;
        if (id != IntelBluetoothRadio.IntelManufacturer) return LeAudioSyncSupport.NotIntel;
        return intelOffload() ? LeAudioSyncSupport.Supported : LeAudioSyncSupport.NoIntelOffload;
    }

    private static bool IntelLeAudioOffloadPresent()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(IntelLeAudioOffloadEnumKey);
        return key?.GetValue("Count") is int count && count > 0;
    }
}
