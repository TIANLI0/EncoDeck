// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>
/// Production endpoint platform. Endpoints are enumerated through PnP and hidden/shown through
/// IPolicyConfig::SetEndpointVisibility (the same non-admin switch as Sound settings &gt; Disable).
/// Only MMDEVAPI endpoint nodes are touched; disabling the BTHENUM service or parent device tears
/// down the LE Audio endpoint as well and is never done.
/// </summary>
public sealed class WindowsAudioEndpointPlatform : IAudioEndpointPlatform
{
    private static readonly string[] PropertyKeys =
    [
        "System.Devices.DeviceInstanceId",
        "System.Devices.FriendlyName",
        "System.Devices.Parent",
        "System.Devices.ContainerId",
    ];

    private const string RenderPrefix = "SWD\\MMDEVAPI\\";
    private const string RenderNodeMarker = "{0.0.0.";
    private const string CaptureNodeMarker = "{0.0.1.";
    private const string A2dpService = "BTHENUM\\{0000110B-";
    private const string HfpService = "BTHENUM\\{0000111E-";
    private const string LeAudioEnum = "APXENUM\\";

    private const int DeviceStateDisabledMask = 0x10000000;

    public Task<IReadOnlyList<AudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken cancellationToken) =>
        GetEndpointsAsync(RenderNodeMarker, "Render", Classify, cancellationToken);

    public Task<IReadOnlyList<AudioEndpointInfo>> GetCaptureEndpointsAsync(CancellationToken cancellationToken) =>
        GetEndpointsAsync(CaptureNodeMarker, "Capture", ClassifyCapture, cancellationToken);

    private static async Task<IReadOnlyList<AudioEndpointInfo>> GetEndpointsAsync(
        string nodeMarker, string flow, Func<string?, AudioEndpointKind> classify, CancellationToken cancellationToken)
    {
        var objects = await PnpObject.FindAllAsync(PnpObjectType.Device, PropertyKeys)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var found = new List<AudioEndpointInfo>();
        var identities = new Dictionary<string, HashSet<ulong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in objects)
        {
            string? container = GetContainerId(obj);
            string? id = GetString(obj, "System.Devices.DeviceInstanceId") ?? obj.Id;
            if (container is null || id is null) continue;
            string? prefix = id.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase) ? "BTHENUM\\DEV_"
                : id.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase) ? "BTHLE\\DEV_" : null;
            if (prefix is null || id.Length < prefix.Length + 12
                || !ulong.TryParse(id.AsSpan(prefix.Length, 12), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong address)) continue;
            if (!identities.TryGetValue(container, out var addresses)) identities[container] = addresses = [];
            addresses.Add(address);
        }
        foreach (var obj in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? id = GetString(obj, "System.Devices.DeviceInstanceId") ?? obj.Id;
            if (string.IsNullOrEmpty(id)
                || !id.StartsWith(RenderPrefix, StringComparison.OrdinalIgnoreCase)
                || !id.Contains(nodeMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? parent = GetString(obj, "System.Devices.Parent");
            AudioEndpointKind kind = classify(parent);
            if (kind == AudioEndpointKind.Other) continue;
            string name = GetString(obj, "System.Devices.FriendlyName")
                ?? GetFriendlyNameFromCfgMgr(id)
                ?? id;
            int? state = GetDeviceState(id, flow);
            string? containerId = GetContainerId(obj);
            found.Add(new AudioEndpointInfo(id, name, parent, kind, state is { } flags && (flags & (DeviceStateDisabledMask | 2)) != 0)
            {
                ContainerId = containerId,
                AssociatedBluetoothAddresses = containerId is not null && identities.TryGetValue(containerId, out var addresses) ? addresses.ToArray() : [],
                IsStateKnown = state.HasValue,
                IsActive = state is { } active && (active & 1) != 0 && (active & (DeviceStateDisabledMask | 2)) == 0,
            });
        }
        return found;
    }

    public Task<AudioEndpointOperationResult> SetEnabledAsync(AudioEndpointInfo endpoint, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        bool allowed = endpoint.Kind is AudioEndpointKind.ClassicA2dpRender or AudioEndpointKind.ClassicHfpRender
            ? IsEndpointNode(endpoint.InstanceId, RenderNodeMarker)
            : endpoint.Kind is AudioEndpointKind.LeAudioCapture or AudioEndpointKind.ClassicHfpCapture
              && IsEndpointNode(endpoint.InstanceId, CaptureNodeMarker);
        if (!allowed)
            return Task.FromResult(new AudioEndpointOperationResult(false, "Only Classic render or headset capture MMDEVAPI endpoints can be changed"));
        // IPolicyConfig is the same non-admin visibility switch as Sound settings > Disable.
        // CfgMgr32 CM_Disable_DevNode returns CR_ACCESS_DENIED (0x33) for these nodes without elevation.
        int hr = PolicyConfig.SetEndpointVisibility(ToAudioEndpointId(endpoint.InstanceId), enabled);
        return Task.FromResult(hr == 0
            ? AudioEndpointOperationResult.Ok
            : new AudioEndpointOperationResult(false, $"SetEndpointVisibility=0x{hr:X8}"));
    }

    public Task<AudioEndpointOperationResult> SetDefaultAsync(AudioEndpointInfo endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (endpoint.Kind != AudioEndpointKind.LeAudioRender || !IsEndpointNode(endpoint.InstanceId, RenderNodeMarker)
            || endpoint.IsDisabled || !endpoint.IsActive || !endpoint.IsStateKnown)
            return Task.FromResult(new AudioEndpointOperationResult(false, "LE Audio render endpoint is not available"));
        string id = ToAudioEndpointId(endpoint.InstanceId);
        int console = PolicyConfig.SetDefaultEndpoint(id, 0); // eConsole
        int multimedia = PolicyConfig.SetDefaultEndpoint(id, 1); // eMultimedia
        return Task.FromResult(console == 0 && multimedia == 0
            ? AudioEndpointOperationResult.Ok
            : new AudioEndpointOperationResult(false, $"SetDefaultEndpoint=0x{console:X8}/0x{multimedia:X8}"));
    }

    internal static AudioEndpointKind Classify(string? parent)
    {
        if (string.IsNullOrEmpty(parent)) return AudioEndpointKind.Other;
        if (parent.StartsWith(LeAudioEnum, StringComparison.OrdinalIgnoreCase)
            && parent.Contains("&Speaker", StringComparison.OrdinalIgnoreCase))
        {
            return AudioEndpointKind.LeAudioRender;
        }
        if (parent.StartsWith(A2dpService, StringComparison.OrdinalIgnoreCase)) return AudioEndpointKind.ClassicA2dpRender;
        if (parent.StartsWith(HfpService, StringComparison.OrdinalIgnoreCase)) return AudioEndpointKind.ClassicHfpRender;
        return AudioEndpointKind.Other;
    }

    internal static AudioEndpointKind ClassifyCapture(string? parent)
    {
        if (string.IsNullOrEmpty(parent)) return AudioEndpointKind.Other;
        // X4 over LC3: APXENUM\{2D68507B-...}&AUDIO\...&Microphone, sibling of the &Speaker node.
        if (parent.StartsWith(LeAudioEnum, StringComparison.OrdinalIgnoreCase)
            && parent.Contains("&Microphone", StringComparison.OrdinalIgnoreCase))
        {
            return AudioEndpointKind.LeAudioCapture;
        }
        if (parent.StartsWith(HfpService, StringComparison.OrdinalIgnoreCase)) return AudioEndpointKind.ClassicHfpCapture;
        return AudioEndpointKind.Other;
    }

    private static string? GetString(PnpObject obj, string key) =>
        obj.Properties.TryGetValue(key, out object? value) ? value as string : null;

    private static string? GetContainerId(PnpObject obj)
    {
        if (!obj.Properties.TryGetValue("System.Devices.ContainerId", out object? value)) return null;
        return value switch
        {
            Guid guid when guid != Guid.Empty => guid.ToString("B"),
            string text when Guid.TryParse(text, out var guid) && guid != Guid.Empty => guid.ToString("B"),
            _ => null,
        };
    }

    private static string? GetFriendlyNameFromCfgMgr(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return null;
        var key = new DevPropKey
        {
            FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            PropertyId = 14, // DEVPKEY_Device_FriendlyName
        };
        uint size = 0;
        _ = CM_Get_DevNode_PropertyW(devInst, ref key, out _, IntPtr.Zero, ref size, 0);
        if (size < 2 || size > 4096) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0) != 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint propertyType, IntPtr buffer, ref uint size, uint flags);

    /// <summary>
    /// The MMDevice endpoint id that IMMDeviceEnumerator::GetDevice takes ({0.0.0.…}.{guid} for render,
    /// {0.0.1.…}.{guid} for capture) from an SWD\MMDEVAPI instance id.
    /// </summary>
    public static string ToMmDeviceId(string instanceId)
    {
        int index = instanceId.IndexOf("{0.0.", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? instanceId[index..] : instanceId;
    }

    private static string ToAudioEndpointId(string instanceId)
    {
        int index = instanceId.IndexOf("{0.0.0.", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? instanceId[index..] : instanceId;
    }

    private static bool IsEndpointNode(string instanceId, string nodeMarker) =>
        instanceId.StartsWith(RenderPrefix + nodeMarker, StringComparison.OrdinalIgnoreCase)
        && instanceId.LastIndexOf('{') is var index && index >= 0 && Guid.TryParse(instanceId[index..], out _);

    private static int? GetDeviceState(string instanceId, string flow)
    {
        int open = instanceId.LastIndexOf('{');
        int close = instanceId.LastIndexOf('}');
        if (open < 0 || close <= open) return null;
        string guid = instanceId[open..(close + 1)];
        string path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{flow}\{guid}";
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
        if (key?.GetValue("DeviceState") is not int state) return null;
        // Sound settings' "Disable" sets the high bit (observed 0x10000001); keep the endpoint
        // enumerated so Show can restore it.
        return state;
    }

    private static class PolicyConfig
    {
        public static int SetEndpointVisibility(string deviceId, bool visible)
        {
            var client = (IPolicyConfig)new PolicyConfigClient();
            try { return client.SetEndpointVisibility(deviceId, visible); }
            finally { Marshal.ReleaseComObject(client); }
        }

        public static int SetDefaultEndpoint(string deviceId, int role)
        {
            var client = (IPolicyConfig)new PolicyConfigClient();
            try { return client.SetDefaultEndpoint(deviceId, role); }
            finally { Marshal.ReleaseComObject(client); }
        }
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fx, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fx, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }
}
