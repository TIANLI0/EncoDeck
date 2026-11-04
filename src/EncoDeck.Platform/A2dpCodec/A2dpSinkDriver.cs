// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Runtime.InteropServices;
using EncoDeck.Core.State;

namespace EncoDeck.Platform.A2dpCodec;

/// <summary>
/// The driver Windows runs for a device's Classic A2DP: the service bound to its Audio Sink node
/// (<c>BTHENUM\{0000110B-…}</c>). That is Windows' own <see cref="CodecDisplay.WindowsA2dpDriver"/> unless a replacement
/// took the device over, such as the LHDC component's <see cref="CodecDisplay.LhdcA2dpDriver"/>; Windows logs codecs
/// (<see cref="WindowsA2dpCodecLog"/>) only for its own. Read through CfgMgr32, without administrator rights.
/// </summary>
public static partial class A2dpSinkDriver
{
    private const string Enumerator = "BTHENUM";
    private const string AudioSink = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}";
    // CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT
    private const uint PresentInEnumerator = 0x00000001 | 0x00000100;
    private const uint CrBufferSmall = 0x1A;
    private const uint DevPropTypeString = 0x12;
    private static readonly DevPropKey ServiceKey = new() { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 6 }; // DEVPKEY_Device_Service

    /// <summary>The service bound to the device's Audio Sink node; null when it has none (not paired over Classic) or it cannot be read.</summary>
    public static string? ServiceFor(ulong address)
    {
        foreach (string instance in BluetoothDevices())
        {
            if (Address(instance) != address || CM_Locate_DevNodeW(out uint node, instance, 0) != 0) continue;
            if (Service(node) is { Length: > 0 } service) return service;
        }
        return null;
    }

    /// <summary>
    /// The device address an Audio Sink node's instance id ends with (<c>…&amp;E498E03A2AA3_C00000000</c>); null for any
    /// other node.
    /// </summary>
    public static ulong? Address(string instance)
    {
        if (instance.Length <= AudioSink.Length || !instance.StartsWith(AudioSink, StringComparison.OrdinalIgnoreCase)
            || instance[AudioSink.Length] is not ('_' or '\\')) return null;
        int suffix = instance.LastIndexOf('_');
        if (suffix < 13 || suffix - 13 <= instance.LastIndexOf('\\') || instance[suffix - 13] != '&') return null;
        return ulong.TryParse(instance.AsSpan(suffix - 12, 12), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address)
            && address != 0 ? address : null;
    }

    private static unsafe string[] BluetoothDevices()
    {
        // A device can arrive between the two calls and outgrow the buffer: ask again then.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, Enumerator, PresentInEnumerator) != 0 || length == 0) return [];
            char[] buffer = new char[length];
            uint result;
            fixed (char* list = buffer) result = CM_Get_Device_ID_ListW(Enumerator, list, length, PresentInEnumerator);
            if (result == CrBufferSmall) continue;
            return result == 0 ? new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        }
        return [];
    }

    private static unsafe string? Service(uint node)
    {
        DevPropKey key = ServiceKey;
        uint type = 0, size = 0;
        if (CM_Get_DevNode_PropertyW(node, &key, &type, null, &size, 0) != CrBufferSmall || type != DevPropTypeString || size is < 2 or > 512) return null;
        byte* buffer = stackalloc byte[(int)size];
        if (CM_Get_DevNode_PropertyW(node, &key, &type, buffer, &size, 0) != 0) return null;
        return new string((char*)buffer, 0, (int)size / sizeof(char)).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint CM_Get_Device_ID_ListW(string filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static unsafe partial uint CM_Get_DevNode_PropertyW(uint node, DevPropKey* key, uint* type, byte* buffer, uint* size, uint flags);
}
