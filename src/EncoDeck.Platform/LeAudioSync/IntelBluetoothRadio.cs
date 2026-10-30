// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace EncoDeck.Platform.LeAudioSync;

/// <summary>The local Bluetooth radio's manufacturer through bthprops (readable without administrator rights).</summary>
public static partial class IntelBluetoothRadio
{
    /// <summary>Bluetooth SIG company identifier of Intel.</summary>
    public const ushort IntelManufacturer = 2;

    /// <summary>The first radio's manufacturer id, or null when there is no radio.</summary>
    public static unsafe ushort? GetManufacturer()
    {
        var parameters = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)sizeof(BLUETOOTH_FIND_RADIO_PARAMS) };
        nint find = BluetoothFindFirstRadio(ref parameters, out nint radio);
        if (find == 0) return null;
        BluetoothFindRadioClose(find);
        try
        {
            var info = new BLUETOOTH_RADIO_INFO { dwSize = (uint)sizeof(BLUETOOTH_RADIO_INFO) };
            return BluetoothGetRadioInfo(radio, ref info) == 0 ? info.manufacturer : null;
        }
        finally
        {
            CloseHandle(radio);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct BLUETOOTH_RADIO_INFO
    {
        public uint dwSize;
        public ulong address;
        public fixed char szName[248];
        public uint ulClassofDevice;
        public ushort lmpSubversion;
        public ushort manufacturer;
    }

    [LibraryImport("bthprops.cpl", SetLastError = true)]
    private static partial nint BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS parameters, out nint radio);

    [LibraryImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindRadioClose(nint find);

    [LibraryImport("bthprops.cpl")]
    private static partial uint BluetoothGetRadioInfo(nint radio, ref BLUETOOTH_RADIO_INFO info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
