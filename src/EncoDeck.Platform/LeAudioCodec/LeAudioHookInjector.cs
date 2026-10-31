// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EncoDeck.Platform.LeAudioCodec;

/// <summary>
/// Loads EncoDeck.LeHook.dll into a process with LoadLibraryW on a remote thread and calls its exported LeHookEnable
/// the same way. Needs an elevated process (debug privilege). A loaded DLL cannot be replaced, so every build is copied
/// under a name carrying its content hash; enabling one build first disables any other (LeHookDisable).
/// </summary>
internal static partial class LeAudioHookInjector
{
    private const string ModulePrefix = "EncoDeck.LeHook.";

    public static int Enable(int pid, string hookDll, string directory, Action<string> log)
    {
        Process.EnterDebugMode();
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hookDll)))[..8].ToLowerInvariant();
        string name = $"{ModulePrefix}{hash}.dll";
        string copy = Path.Combine(directory, name);
        // An older build may still be patched in: restore Windows' code first, the new build's prologue check fails otherwise.
        foreach (var (otherName, otherBase) in Modules(pid).Where(m => !m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            CallExport(pid, otherBase, Path.Combine(directory, otherName), "LeHookDisable");
            log($"disabled {otherName}");
        }
        nint remote = Find(pid, name);
        if (remote == 0)
        {
            File.Copy(hookDll, copy, overwrite: true);
            // The service runs as LOCAL SERVICE: it loads the DLL and appends to the log in this folder.
            GrantLocalService(directory);
            Load(pid, copy);
            remote = Find(pid, name);
            if (remote == 0) throw new InvalidOperationException("The Bluetooth audio service did not load the hook.");
            log($"loaded {name} into pid {pid}");
        }
        uint result = CallExport(pid, remote, copy, "LeHookEnable");
        log($"LeHookEnable returned {result}");
        return result switch
        {
            0 => LeAudioCodecHook.Result.Ok,
            2 => LeAudioCodecHook.Result.TargetNotLoaded,
            3 => LeAudioCodecHook.Result.UnsupportedWindows,
            _ => LeAudioCodecHook.Result.HookFailed,
        };
    }

    private static List<(string Name, nint Base)> Modules(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.Modules.Cast<ProcessModule>()
            .Where(m => m.ModuleName.StartsWith(ModulePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(m => (m.ModuleName, m.BaseAddress))
            .ToList();
    }

    private static nint Find(int pid, string name) =>
        Modules(pid).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Base;

    private static void GrantLocalService(string directory)
    {
        using var icacls = Process.Start(new ProcessStartInfo("icacls.exe", $"\"{directory}\" /grant *S-1-5-19:(OI)(CI)M")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        }) ?? throw new IOException("Cannot start icacls.");
        icacls.StandardOutput.ReadToEnd();
        icacls.WaitForExit();
    }

    private static void Load(int pid, string dll)
    {
        nint process = Open(pid);
        try
        {
            byte[] path = System.Text.Encoding.Unicode.GetBytes(dll + "\0");
            nint memory = VirtualAllocEx(process, 0, (nuint)path.Length, MemCommit | MemReserve, PageReadWrite);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!WriteProcessMemory(process, memory, path, (nuint)path.Length, out _))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                // kernel32 is mapped at the same address in every process of a boot session.
                nint loadLibrary = NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "LoadLibraryW");
                RunThread(process, loadLibrary, memory);
            }
            finally
            {
                VirtualFreeEx(process, memory, 0, MemRelease);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static uint CallExport(int pid, nint remoteBase, string dll, string export)
    {
        // The export's offset from the module base, from a local copy mapped without running any of its code.
        nint local = LoadLibraryExW(dll, 0, DontResolveDllReferences);
        if (local == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint offset;
        try
        {
            offset = NativeLibrary.GetExport(local, export) - local;
        }
        finally
        {
            FreeLibrary(local);
        }
        nint process = Open(pid);
        try
        {
            return RunThread(process, remoteBase + offset, 0);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static nint Open(int pid)
    {
        nint process = OpenProcess(ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead, false, (uint)pid);
        return process != 0 ? process : throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static uint RunThread(nint process, nint start, nint parameter)
    {
        nint thread = CreateRemoteThread(process, 0, 0, start, parameter, 0, out _);
        if (thread == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (WaitForSingleObject(thread, 10_000) != 0) throw new TimeoutException("The remote thread did not finish.");
            return GetExitCodeThread(thread, out uint code) ? code : throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint DontResolveDllReferences = 0x01;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAllocEx(nint process, nint address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(nint process, nint address, nuint size, uint type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint start, nint parameter, uint flags, out uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeThread(nint thread, out uint code);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string path, nint file, uint flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>Process id of a running Windows service, readable without elevation.</summary>
internal static partial class ServiceProcess
{
    public static unsafe int GetProcessId(string serviceName)
    {
        nint manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0) return 0;
        try
        {
            nint service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
            if (service == 0) return 0;
            try
            {
                SERVICE_STATUS_PROCESS status;
                return QueryServiceStatusEx(service, ScStatusProcessInfo, &status, (uint)sizeof(SERVICE_STATUS_PROCESS), out _)
                    && status.dwCurrentState == ServiceRunning ? (int)status.dwProcessId : 0;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceStatusEx(nint service, int level, SERVICE_STATUS_PROCESS* buffer, uint size, out uint needed);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
