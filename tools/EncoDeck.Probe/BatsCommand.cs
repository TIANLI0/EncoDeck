// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;

namespace EncoDeck.Probe;

/// <summary>
/// Windows' choice between Classic and LE Audio for dual-mode devices. The choice is made per device when the Bluetooth audio
/// service builds its endpoint manager. Reads and sets the global preference through the in-box BluetoothAudioService (the
/// same interface the Settings toggle uses), and can turn the Bluetooth radio off and on to make the service decide again.
/// Research command.
/// </summary>
internal static class BatsCommand
{
    private const string ClassName = "Microsoft.Bluetooth.Audio.Interface.Internal.BluetoothAudioService";
    private static readonly Guid StaticsIid = new("AAD2A511-7689-5A94-B5BB-DC600290A493");
    private const string BatsKey = @"SYSTEM\CurrentControlSet\Services\BthAvctpSvc\Parameters\Bats";

    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        string action = options.Positional.Count > 0 ? options.Positional[0].ToLowerInvariant() : "get";
        var clock = Stopwatch.StartNew();
        void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{clock.Elapsed.TotalSeconds,6:0.0}s {message}");

        if (action is "set" or "rebuild" or "restart" or "authorize" or "revoke" && !options.Has("confirm-write"))
        {
            Console.Error.WriteLine($"Refused: 'bats {action}' changes Windows' Bluetooth audio choice. Re-run with --confirm-write.");
            return 2;
        }

        PrintPreference(Log);
        PrintRegistry(Log);
        switch (action)
        {
            case "get":
                return 0;
            case "set":
                int value = options.GetInt("value", 0);
                if (value is not (1 or 2))
                {
                    Console.Error.WriteLine("Refused: --value must be 1 (LE Audio) or 2 (Classic).");
                    return 2;
                }
                Log($"SetTransportPreference({value}) -> 0x{SetPreference(value):X8}");
                PrintPreference(Log);
                PrintRegistry(Log);
                return 0;
            case "rebuild":
                var access = await Radio.RequestAccessAsync().AsTask(ct);
                var radio = (await Radio.GetRadiosAsync().AsTask(ct)).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                Log($"Radio access {access}; Bluetooth radio {(radio is null ? "missing" : radio.State.ToString())}");
                if (radio is null || access != RadioAccessStatus.Allowed) return 4;
                radio.StateChanged += (r, _) => Log($"radio state {r.State}");
                Log($"set Off -> {await radio.SetStateAsync(RadioState.Off).AsTask(ct)}");
                await Task.Delay(options.GetInt("off-ms", 3000), ct);
                Log($"set On -> {await radio.SetStateAsync(RadioState.On).AsTask(ct)}");
                await Task.Delay(2000, ct);
                PrintPreference(Log);
                PrintRegistry(Log);
                return 0;
            case "restart":
                RestartService(Log);
                await Task.Delay(1000, ct);
                PrintPreference(Log);
                PrintRegistry(Log);
                return 0;
            case "authorize":
            case "revoke":
            {
                string current = ReadSddl();
                string ace = $"(A;;RPWPLC;;;{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value})";
                string updated = action == "authorize"
                    ? current.Contains(ace, StringComparison.OrdinalIgnoreCase) ? current : InsertAce(current, ace)
                    : current.Replace(ace, "", StringComparison.OrdinalIgnoreCase);
                Log($"service SDDL now: {current}");
                if (updated == current)
                {
                    Log("nothing to change");
                    return 0;
                }
                Log($"service SDDL new: {updated} (administrator prompt)");
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"sdset {ServiceName} \"{updated}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var process = Process.Start(start)!)
                {
                    await process.WaitForExitAsync(ct);
                    Log($"sc sdset exit {process.ExitCode}");
                }
                Log($"service SDDL after: {ReadSddl()}");
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: bats [get | set --value 1|2 | rebuild [--off-ms N] | restart | authorize | revoke] (writes need --confirm-write)");
                return 2;
        }
    }

    private const string ServiceName = "BthAvctpSvc";

    private static string ReadSddl()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"sdshow {ServiceName}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("D:", StringComparison.Ordinal));
    }

    /// <summary>Adds the ACE to the DACL, before an S: part if there is one.</summary>
    private static string InsertAce(string sddl, string ace)
    {
        int sacl = sddl.IndexOf("S:", StringComparison.Ordinal);
        return sacl < 0 ? sddl + ace : sddl[..sacl] + ace + sddl[sacl..];
    }

    /// <summary>Stops and starts the Bluetooth audio service with the caller's own rights (after <c>authorize</c>).</summary>
    private static void RestartService(Action<string> log)
    {
        IntPtr scm = OpenSCManager(null, null, 0x0001);
        if (scm == IntPtr.Zero)
        {
            log($"OpenSCManager failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        try
        {
            IntPtr service = OpenService(scm, ServiceName, 0x0004 | 0x0010 | 0x0020);
            if (service == IntPtr.Zero)
            {
                log($"OpenService (query/start/stop) failed: {Marshal.GetLastWin32Error()} (5 = access denied: run 'bats authorize' first)");
                return;
            }
            try
            {
                var clock = Stopwatch.StartNew();
                bool stopped = ControlService(service, 1, out var status);
                log($"stop requested: {(stopped ? "ok" : $"error {Marshal.GetLastWin32Error()}")}; state {status.CurrentState}");
                while (QueryServiceStatus(service, out status) && status.CurrentState != 1 && clock.ElapsedMilliseconds < 15000) Thread.Sleep(100);
                log($"state {status.CurrentState} after {clock.ElapsedMilliseconds} ms");
                bool started = StartService(service, 0, null);
                int error = started ? 0 : Marshal.GetLastWin32Error();
                log($"start requested: {(started ? "ok" : $"error {error}")}{(error == 1056 ? " (already running: trigger-started)" : "")}");
                while (QueryServiceStatus(service, out status) && status.CurrentState != 4 && clock.ElapsedMilliseconds < 30000) Thread.Sleep(100);
                log($"state {status.CurrentState} after {clock.ElapsedMilliseconds} ms");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, int control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartService(IntPtr service, int argc, string[]? argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static void PrintRegistry(Action<string> log)
    {
        using var root = Registry.LocalMachine.OpenSubKey(BatsKey);
        if (root is null)
        {
            log("Bats key missing");
            return;
        }
        log($"Bats: UserPrefersClassicAudio={root.GetValue("UserPrefersClassicAudio")} SystemCurrentlyPrefersClassicAudio={root.GetValue("SystemCurrentlyPrefersClassicAudio")} " +
            $"LEAudioCapableAudioDriverConsecutiveTimeoutCount={root.GetValue("LEAudioCapableAudioDriverConsecutiveTimeoutCount")}");
        foreach (var radio in root.GetSubKeyNames())
        {
            using var radioKey = root.OpenSubKey(radio);
            foreach (var container in radioKey?.GetSubKeyNames() ?? [])
            {
                using var key = radioKey!.OpenSubKey(container);
                log($"  {radio}\\{container}: Classic={key?.GetValue("SupportsClassicAudio")} LE={key?.GetValue("SupportsLEAudio")}");
            }
        }
    }

    private static void PrintPreference(Action<string> log)
    {
        try
        {
            using var service = AudioService.Create();
            log($"TransportPreference={service.Preference} CurrentTransportPreference={service.Current} (1 = LE Audio, 2 = Classic)");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            log($"BluetoothAudioService unavailable: 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static int SetPreference(int value)
    {
        using var service = AudioService.Create();
        return service.SetPreference(value);
    }

    /// <summary>IBluetoothAudioService {680A9AA7-3263-5195-859E-9FE4E4C4E830}, called through its vtable.</summary>
    private sealed unsafe class AudioService : IDisposable
    {
        private IntPtr _service;

        private AudioService(IntPtr service) => _service = service;

        public static AudioService Create()
        {
            Check(WindowsCreateString(ClassName, ClassName.Length, out var name));
            try
            {
                var iid = StaticsIid;
                Check(RoGetActivationFactory(name, ref iid, out var statics));
                try
                {
                    var createDefault = (delegate* unmanaged<IntPtr, IntPtr*, int>)(*(IntPtr**)statics)[6];
                    IntPtr service;
                    Check(createDefault(statics, &service));
                    return new AudioService(service);
                }
                finally { Marshal.Release(statics); }
            }
            finally { WindowsDeleteString(name); }
        }

        public int Preference => Read(6);

        public int Current => Read(8);

        public int SetPreference(int value) => ((delegate* unmanaged<IntPtr, int, int>)(*(IntPtr**)_service)[7])(_service, value);

        private int Read(int slot)
        {
            int value;
            Check(((delegate* unmanaged<IntPtr, int*, int>)(*(IntPtr**)_service)[slot])(_service, &value));
            return value;
        }

        public void Dispose()
        {
            if (_service != IntPtr.Zero) Marshal.Release(_service);
            _service = IntPtr.Zero;
        }

        private static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
