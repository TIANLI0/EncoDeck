// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Windows' preference for earbuds that offer both Classic audio and LE Audio.</summary>
public enum AudioTransportPreference
{
    LeAudio = 1,
    Classic = 2,
}

/// <summary>
/// Which audio Windows uses for earbuds bonded over both Classic and LE. The Bluetooth audio service (BthAvctpSvc)
/// decides once per device when it starts: with the preference "LE Audio" it waits for the LE Audio links and then
/// removes the Classic audio services; with "Classic" it keeps Classic audio. It never revisits that choice while it
/// runs, so a switch sets the preference (the setting behind Settings' "Use LE Audio when available", no rights needed)
/// and restarts the service. Restarting needs start/stop rights on that one service, which <see cref="AuthorizeAsync"/>
/// grants this user once through the administrator prompt. Verified on an Enco X4, Windows build 26340.
/// </summary>
public static class WindowsAudioTransport
{
    public const string ServiceName = "BthAvctpSvc";
    private const string ClassName = "Microsoft.Bluetooth.Audio.Interface.Internal.BluetoothAudioService";
    private static readonly Guid StaticsIid = new("AAD2A511-7689-5A94-B5BB-DC600290A493");

    /// <summary>A2DP sink, AVRCP target and controller, Hands-Free: what Windows removes when LE Audio is chosen.</summary>
    private static readonly Guid[] ClassicAudioServices =
    [
        new("0000110B-0000-1000-8000-00805F9B34FB"),
        new("0000111E-0000-1000-8000-00805F9B34FB"),
        new("0000110E-0000-1000-8000-00805F9B34FB"),
        new("0000110C-0000-1000-8000-00805F9B34FB"),
    ];

    /// <summary>The preference Windows will apply on the next service start; null when the interface is unavailable.</summary>
    public static AudioTransportPreference? ReadPreference() => Call(service => service.Read(6));

    /// <summary>The preference the running service applied; null when the interface is unavailable.</summary>
    public static AudioTransportPreference? ReadApplied() => Call(service => service.Read(8));

    /// <summary>Global for every dual-mode device; takes effect when the service next starts.</summary>
    public static void SetPreference(AudioTransportPreference preference)
    {
        using var service = AudioService.Create();
        Marshal.ThrowExceptionForHR(service.Write(7, (int)preference));
    }

    private static AudioTransportPreference? Call(Func<AudioService, int> read)
    {
        try
        {
            using var service = AudioService.Create();
            return read(service) is var value and (1 or 2) ? (AudioTransportPreference)value : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Whether this user may stop and start the Bluetooth audio service without the administrator prompt.</summary>
    public static bool CanRestartService
    {
        get
        {
            using var scm = ServiceHandle.Open(null, ScmConnect);
            using var service = scm is null ? null : ServiceHandle.Open(scm, ServiceQueryStatus | ServiceStart | ServiceStop);
            return service is not null;
        }
    }

    /// <summary>
    /// The access entry <see cref="AuthorizeAsync"/> adds: query status, start and stop for this user only, in the
    /// order Windows writes the rights back (so it is found again when revoking).
    /// </summary>
    internal static string AccessEntry(string sid) => $"(A;;LCRPWP;;;{sid})";

    /// <summary>Written once the permission was granted; the uninstaller revokes the permission when it finds this file.</summary>
    public static string AccessMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EncoDeck", "bluetooth-audio-service-access");

    /// <summary>Adds <paramref name="entry"/> to the DACL part of a service SDDL (before any SACL), once.</summary>
    internal static string WithEntry(string sddl, string entry)
    {
        if (sddl.Contains(entry, StringComparison.OrdinalIgnoreCase)) return sddl;
        int sacl = sddl.IndexOf("S:", StringComparison.Ordinal);
        return sacl < 0 ? sddl + entry : sddl[..sacl] + entry + sddl[sacl..];
    }

    /// <summary>
    /// Shows the administrator prompt once and lets this user restart the Bluetooth audio service from then on. Only
    /// that service's permissions change. A declined prompt throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Task AuthorizeAsync() => ChangeAccessAsync(add: true);

    /// <summary>Removes what <see cref="AuthorizeAsync"/> added (administrator prompt).</summary>
    public static Task RevokeAsync() => ChangeAccessAsync(add: false);

    private static async Task ChangeAccessAsync(bool add)
    {
        string sddl = await ReadSecurityAsync().ConfigureAwait(false);
        string entry = AccessEntry(WindowsIdentity.GetCurrent().User!.Value);
        string updated = add ? WithEntry(sddl, entry) : sddl.Replace(entry, "", StringComparison.OrdinalIgnoreCase);
        if (updated != sddl) await SetSecurityAsync(updated).ConfigureAwait(false);
        try
        {
            if (add)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AccessMarkerPath)!);
                File.WriteAllText(AccessMarkerPath, entry);
            }
            else File.Delete(AccessMarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the uninstaller's cleanup depends on it.
        }
    }

    private static async Task SetSecurityAsync(string updated)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"sdset {ServiceName} \"{updated}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Cannot start the elevated command.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"sc sdset failed ({process.ExitCode}).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator prompt was declined.", ex);
        }
    }

    private static async Task<string> ReadSecurityAsync()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"sdshow {ServiceName}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("Cannot read the service permissions.");
        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("D:", StringComparison.Ordinal))
            ?? throw new IOException("The service permissions could not be read.");
    }

    /// <summary>
    /// Stops and starts the Bluetooth audio service so it decides again for every device. All Bluetooth audio pauses for
    /// a moment; other Bluetooth devices stay connected. Throws <see cref="UnauthorizedAccessException"/> before
    /// <see cref="AuthorizeAsync"/>.
    /// </summary>
    public static async Task RestartServiceAsync(CancellationToken cancellationToken)
    {
        using var scm = ServiceHandle.Open(null, ScmConnect) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = ServiceHandle.Open(scm, ServiceQueryStatus | ServiceStart | ServiceStop)
            ?? throw new UnauthorizedAccessException("EncoDeck may not restart the Bluetooth audio service yet.");
        // Stopping waits for running LE Audio streams to close (about 10 s on the X4).
        if (!ControlService(service, ControlStop, out _) && Marshal.GetLastWin32Error() is not (ErrorServiceNotActive or ErrorCannotAcceptControl))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        await WaitForStateAsync(service, Stopped, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        // A Bluetooth trigger may already have started it again.
        if (!StartService(service, 0, null) && Marshal.GetLastWin32Error() != ErrorServiceAlreadyRunning)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        await WaitForStateAsync(service, Running, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForStateAsync(ServiceHandle service, int state, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (QueryServiceStatus(service, out var status) && status.CurrentState != state)
        {
            if (clock.Elapsed > timeout) throw new TimeoutException($"The Bluetooth audio service did not reach state {state}.");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Installs Windows' Classic audio services for the earbuds again (A2DP first). Windows removes them when it chooses
    /// LE Audio and does not bring them back when it later chooses Classic. Each takes about 3 s; no rights needed.
    /// </summary>
    public static Task EnableClassicAudioAsync(ulong address, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var find = new FindRadioParams { Size = Marshal.SizeOf<FindRadioParams>() };
        IntPtr search = BluetoothFindFirstRadio(ref find, out IntPtr radio);
        if (search == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        BluetoothFindRadioClose(search);
        try
        {
            var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>(), Address = address };
            int found = BluetoothGetDeviceInfo(radio, ref info);
            if (found != 0) throw new Win32Exception(found);
            foreach (var guid in ClassicAudioServices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var service = guid;
                // Already enabled (or not offered) is fine; only the end state matters.
                _ = BluetoothSetServiceState(radio, ref info, ref service, ServiceEnable);
            }
        }
        finally { CloseHandle(radio); }
    }, cancellationToken);

    /// <summary>IBluetoothAudioService {680A9AA7-3263-5195-859E-9FE4E4C4E830} through its vtable (slots 6, 7, 8).</summary>
    private sealed class AudioService : IDisposable
    {
        private IntPtr _service;

        private AudioService(IntPtr service) => _service = service;

        public static AudioService Create()
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out var name));
            try
            {
                var iid = StaticsIid;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out var statics));
                try
                {
                    var createDefault = Slot<CreateDefault>(statics, 6);
                    Marshal.ThrowExceptionForHR(createDefault(statics, out var service));
                    return new AudioService(service);
                }
                finally { Marshal.Release(statics); }
            }
            finally { WindowsDeleteString(name); }
        }

        public int Read(int slot)
        {
            Marshal.ThrowExceptionForHR(Slot<GetInt>(_service, slot)(_service, out int value));
            return value;
        }

        public int Write(int slot, int value) => Slot<SetInt>(_service, slot)(_service, value);

        private static T Slot<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        public void Dispose()
        {
            if (_service != IntPtr.Zero) Marshal.Release(_service);
            _service = IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDefault(IntPtr self, out IntPtr service);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInt(IntPtr self, out int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetInt(IntPtr self, int value);
    }

    private sealed class ServiceHandle : SafeHandle
    {
        private ServiceHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public static ServiceHandle? Open(ServiceHandle? scm, int access)
        {
            var result = scm is null ? OpenSCManager(null, null, access) : OpenService(scm, ServiceName, access);
            if (!result.IsInvalid) return result;
            result.Dispose();
            return null;
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private const int ScmConnect = 0x0001;
    private const int ServiceQueryStatus = 0x0004;
    private const int ServiceStart = 0x0010;
    private const int ServiceStop = 0x0020;
    private const int ControlStop = 1;
    private const int Stopped = 1;
    private const int Running = 4;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorCannotAcceptControl = 1061;
    private const uint ServiceEnable = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams
    {
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern ServiceHandle OpenService(ServiceHandle scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(ServiceHandle service, int control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartServiceW")]
    private static extern bool StartService(ServiceHandle service, int argc, string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr search);

    [DllImport("bthprops.cpl")]
    private static extern int BluetoothGetDeviceInfo(IntPtr radio, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl")]
    private static extern int BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo info, ref Guid service, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
