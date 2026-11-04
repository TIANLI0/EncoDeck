// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace EncoDeck.Bluetooth.Link;

/// <summary>What Windows' Bluetooth audio drivers are asked to do (KSPROPSETID_BtAudio property ids).</summary>
public enum BluetoothAudioLinkRequest
{
    /// <summary>KSPROPERTY_ONESHOT_RECONNECT: page the device and connect this audio profile.</summary>
    Connect = 0,
    /// <summary>KSPROPERTY_ONESHOT_DISCONNECT: disconnect this audio profile.</summary>
    Disconnect = 1,
}

/// <summary>One audio driver filter a request reached, and the driver's answer.</summary>
public sealed record BluetoothAudioLinkResult(string EndpointId, string? FilterId, int HResult)
{
    public bool IsSuccess => HResult == 0;
}

/// <summary>
/// Connects and disconnects a device's Bluetooth audio the way Windows' audio drivers expect it: the endpoint's
/// topology leads to the driver's KS filter, which takes KSPROPERTY_ONESHOT_RECONNECT / _DISCONNECT (BthA2dp,
/// BthHfAud and Microsoft.Bluetooth.Audio all implement KSPROPSETID_BtAudio). No rights needed. Windows tears
/// the Classic link down itself once no profile holds it. <see cref="DisconnectLink"/> is the forced fallback.
/// </summary>
public static class BluetoothAudioLink
{
    private static readonly Guid KsPropSetIdBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private const uint KsPropertyTypeGet = 1;
    private const int ClsctxAll = 0x17;
    private const uint IoctlBthDisconnectDevice = 0x41000C;

    /// <summary>
    /// Sends <paramref name="request"/> to the driver filter behind each endpoint (MMDevice ids, {0.0.x.…}.{guid}),
    /// once per filter: a headset's render and capture endpoints share one.
    /// </summary>
    public static IReadOnlyList<BluetoothAudioLinkResult> Send(IEnumerable<string> endpointIds, BluetoothAudioLinkRequest request) =>
        Visit(endpointIds, (enumerator, filterId) => SendToFilter(enumerator, filterId, request));

    /// <summary>The driver filters behind the endpoints, without sending anything; HResult is the lookup's.</summary>
    public static IReadOnlyList<BluetoothAudioLinkResult> FindFilters(IEnumerable<string> endpointIds) =>
        Visit(endpointIds, (_, _) => 0);

    private static List<BluetoothAudioLinkResult> Visit(IEnumerable<string> endpointIds, Func<IMMDeviceEnumerator, string, int> action)
    {
        ArgumentNullException.ThrowIfNull(endpointIds);
        var results = new List<BluetoothAudioLinkResult>();
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            foreach (string endpointId in endpointIds)
            {
                foreach (var (filterId, hr) in FiltersOf(enumerator, endpointId))
                {
                    if (filterId is not null && !filters.Add(filterId)) continue;
                    results.Add(new BluetoothAudioLinkResult(endpointId, filterId,
                        hr != 0 || filterId is null ? hr : action(enumerator, filterId)));
                }
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return results;
    }

    /// <summary>
    /// Forces the Classic link to <paramref name="address"/> down (IOCTL_BTH_DISCONNECT_DEVICE on the radio, what
    /// BluetoothApis' BluetoothDisconnectDevice does), whatever L2CAP and SCO channels are open. Returns the Win32 error.
    /// </summary>
    public static int DisconnectLink(ulong address)
    {
        var find = new FindRadioParams { Size = Marshal.SizeOf<FindRadioParams>() };
        IntPtr search = BluetoothFindFirstRadio(ref find, out IntPtr radio);
        if (search == IntPtr.Zero) return Marshal.GetLastWin32Error();
        BluetoothFindRadioClose(search);
        try
        {
            return DeviceIoControl(radio, IoctlBthDisconnectDevice, ref address, sizeof(ulong), IntPtr.Zero, 0, out _, IntPtr.Zero)
                ? 0
                : Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(radio); }
    }

    private static List<(string? FilterId, int HResult)> FiltersOf(IMMDeviceEnumerator enumerator, string endpointId)
    {
        var found = new List<(string?, int)>();
        int hr = enumerator.GetDevice(endpointId, out IMMDevice? endpoint);
        if (hr != 0 || endpoint is null) return [(null, hr)];
        try
        {
            var iid = typeof(IDeviceTopology).GUID;
            hr = endpoint.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object? activated);
            if (hr != 0 || activated is not IDeviceTopology topology) return [(null, hr)];
            try
            {
                hr = topology.GetConnectorCount(out uint count);
                if (hr != 0) return [(null, hr)];
                for (uint i = 0; i < count; i++)
                {
                    if (topology.GetConnector(i, out IConnector? connector) != 0 || connector is null) continue;
                    try
                    {
                        // Not connected (E_NOTFOUND) is normal for the endpoint's other connector.
                        if (connector.GetDeviceIdConnectedTo(out string? filterId) == 0 && filterId is not null)
                            found.Add((filterId, 0));
                    }
                    finally { Marshal.ReleaseComObject(connector); }
                }
            }
            finally { Marshal.ReleaseComObject(topology); }
        }
        finally { Marshal.ReleaseComObject(endpoint); }
        return found.Count > 0 ? found : [(null, unchecked((int)0x80070490))]; // E_NOTFOUND
    }

    private static int SendToFilter(IMMDeviceEnumerator enumerator, string filterId, BluetoothAudioLinkRequest request)
    {
        int hr = enumerator.GetDevice(filterId, out IMMDevice? filter);
        if (hr != 0 || filter is null) return hr;
        try
        {
            var iid = typeof(IKsControl).GUID;
            hr = filter.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object? activated);
            if (hr != 0 || activated is not IKsControl control) return hr;
            try
            {
                var property = new KsProperty { Set = KsPropSetIdBtAudio, Id = (uint)request, Flags = KsPropertyTypeGet };
                return control.KsProperty(ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
            }
            finally { Marshal.ReleaseComObject(control); }
        }
        finally { Marshal.ReleaseComObject(filter); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams
    {
        public int Size;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector? connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector? other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr search);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, ref ulong input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
