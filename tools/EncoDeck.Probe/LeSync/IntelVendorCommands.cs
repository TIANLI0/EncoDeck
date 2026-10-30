// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EncoDeck.Probe.LeSync;

/// <summary>
/// Research: Intel vendor commands sent with the documented IOCTL_BTH_HCI_VENDOR_COMMAND, which Windows only accepts
/// from an elevated process holding SeLoadDriverPrivilege. Turns the controller's per-second link statistics on and off.
/// </summary>
internal static partial class IntelVendorCommands
{
    /// <summary>Bluetooth SIG company identifier of Intel.</summary>
    private const ushort IntelManufacturer = 2;

    private const uint IoctlBthHciVendorCommand = 0x410050;
    private const int CommandAttempts = 3;

    // DDC 0x0291 = link statistics period (1 = every second), DDC 0x0292 = telemetry event mask, 0xFCA1 = trace
    // on/off; the same items Linux's btintel_set_debug_features() writes. 0x7B is the mask the Windows driver sets.
    private static readonly (ushort Opcode, byte[] Parameters)[] EnableSequence =
    [
        (0xFC8B, [0x04, 0x91, 0x02, 0x01, 0x00]),
        (0xFC8B, [0x0A, 0x92, 0x02, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
        (0xFCA1, [0x02]),
    ];

    // Linux stops the trace before it rewrites the mask.
    private static readonly (ushort Opcode, byte[] Parameters)[] DisableSequence =
    [
        (0xFCA1, [0x00]),
        (0xFC8B, [0x0A, 0x92, 0x02, 0x7B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
        (0xFC8B, [0x04, 0x91, 0x02, 0x00, 0x00]),
    ];

    /// <summary>Turns on per-second link statistics. Needs an elevated process.</summary>
    public static void EnableLinkStatistics() => Run(EnableSequence);

    /// <summary>Restores the driver's defaults: trace off, its event mask, no periodic statistics.</summary>
    public static void DisableLinkStatistics() => Run(DisableSequence);

    private static void Run((ushort Opcode, byte[] Parameters)[] sequence)
    {
        EnableLoadDriverPrivilege();
        foreach (var (opcode, parameters) in sequence)
        {
            byte status = SendCommand(opcode, parameters);
            if (status != 0) throw new InvalidOperationException($"Vendor command 0x{opcode:X4} failed with HCI status 0x{status:X2}");
        }
    }

    /// <summary>Sends one command and returns the Command Complete status. Retries transient IOCTL failures.</summary>
    internal static byte SendCommand(ushort opcode, ReadOnlySpan<byte> parameters)
    {
        byte[] input = BuildCommand(opcode, parameters);
        Win32Exception? last = null;
        for (int attempt = 0; attempt < CommandAttempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(200);
            using var radio = OpenRadio() ?? throw new InvalidOperationException("No Bluetooth radio");
            byte[] output = new byte[512];
            if (DeviceIoControl(radio.Handle, IoctlBthHciVendorCommand, input, (uint)input.Length, output, (uint)output.Length, out uint returned, 0))
                return ParseCommandComplete(output.AsSpan(0, (int)returned), opcode);
            // Seen once on a busy controller (1238) and gone on the next attempt.
            last = new Win32Exception(Marshal.GetLastPInvokeError());
        }
        throw last!;
    }

    /// <summary>BTH_VENDOR_SPECIFIC_COMMAND, packed: ManufacturerId, LmpVersion (0 = any), MatchAnySinglePattern, opcode, length, data.</summary>
    internal static byte[] BuildCommand(ushort opcode, ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(parameters));
        byte[] buffer = new byte[9 + parameters.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, IntelManufacturer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), opcode);
        buffer[8] = (byte)parameters.Length;
        parameters.CopyTo(buffer.AsSpan(9));
        return buffer;
    }

    /// <summary>The output buffer holds the HCI event itself: 0E len ncmd opcode(2) status …</summary>
    internal static byte ParseCommandComplete(ReadOnlySpan<byte> evt, ushort opcode)
    {
        if (evt.Length < 6 || evt[0] != 0x0E || BinaryPrimitives.ReadUInt16LittleEndian(evt[3..]) != opcode)
            throw new InvalidOperationException($"Unexpected reply to vendor command 0x{opcode:X4}: {Convert.ToHexString(evt)}");
        return evt[5];
    }

    private static unsafe void EnableLoadDriverPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (!LookupPrivilegeValueW(null, "SeLoadDriverPrivilege", out LUID luid))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            Marshal.SetLastPInvokeError(0);
            AdjustTokenPrivileges(token, false, &privileges, 0, 0, 0);
            int error = Marshal.GetLastPInvokeError();
            // ERROR_NOT_ALL_ASSIGNED: the token does not hold the privilege, i.e. not elevated.
            if (error != 0) throw new Win32Exception(error);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static unsafe RadioHandle? OpenRadio()
    {
        var parameters = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)sizeof(BLUETOOTH_FIND_RADIO_PARAMS) };
        nint find = BluetoothFindFirstRadio(ref parameters, out nint radio);
        if (find == 0) return null;
        BluetoothFindRadioClose(find);
        return new RadioHandle(radio);
    }

    private sealed class RadioHandle(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;

        public void Dispose() => CloseHandle(Handle);
    }

    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("bthprops.cpl", SetLastError = true)]
    private static partial nint BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS parameters, out nint radio);

    [LibraryImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindRadioClose(nint find);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, byte[] input, uint inputLength, byte[] output, uint outputLength, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TOKEN_PRIVILEGES* state, uint length, nint previous, nint returnLength);
}
