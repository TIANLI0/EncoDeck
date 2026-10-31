// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EncoDeck.Platform.A2dpCodec;

/// <summary>The codec Windows' A2DP driver streamed to a device with, as its log records it.</summary>
/// <param name="Address">The device's Bluetooth address.</param>
/// <param name="At">When that stream ended (Windows records the codec then).</param>
public sealed record WindowsA2dpCodec(ulong Address, string Codec, DateTimeOffset At);

/// <summary>
/// Reads the A2DP codec from Windows itself instead of the earbuds' own report. Windows' A2DP driver logs every stream
/// it ends (<c>A2dpStreaming</c> of the Microsoft.Windows.Bluetooth.BthA2dp ETW provider: the standard codec id, or the
/// vendor and vendor codec id). Recording that log needs administrator rights once: <see cref="AuthorizeAsync"/>
/// registers a boot-time trace session (an autologger, a few small buffers) that writes a 1 MB circular file anyone can
/// read; from then on EncoDeck only reads that file, without rights or a running service of its own.
/// </summary>
public static partial class WindowsA2dpCodecLog
{
    public const string SessionName = "EncoDeck-BthA2dp";
    private const string ProviderText = "{8776ad1e-5022-4451-a566-f47e708b9075}";
    private static readonly Guid Provider = new(ProviderText);

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EncoDeck", "Codec");

    public static string LogPath => Path.Combine(LogDirectory, "a2dp.etl");

    /// <summary>The layouts below are the 64-bit ones (x64 and ARM64, the builds EncoDeck ships).</summary>
    public static bool IsSupported => Environment.Is64BitProcess;

    /// <summary>The session was registered: it writes its file from this boot on.</summary>
    public static bool IsAuthorized => IsSupported && File.Exists(LogPath);

    /// <summary>A2DP codec name from the ids Windows logs; null for one EncoDeck does not know.</summary>
    public static string? CodecName(ulong standard, ulong vendor, ulong vendorCodec) => standard switch
    {
        0x00 => "SBC",
        0x01 => "MP3",
        0x02 => "AAC",
        0xFF => (vendor, vendorCodec) switch
        {
            (0x004F, 0x0001) => "aptX",
            (0x00D7, 0x0024) => "aptX HD",
            (0x000A, 0x00AD) => "aptX Adaptive",
            (0x012D, 0x00AA) => "LDAC",
            // Savitech's codecs (LHDC versions and LLAC) share the vendor id.
            (0x053A, _) => "LHDC",
            _ => null,
        },
        _ => null,
    };

    /// <summary>The latest record per device.</summary>
    public static IReadOnlyDictionary<ulong, WindowsA2dpCodec> Latest(IEnumerable<WindowsA2dpCodec> records) =>
        records.GroupBy(r => r.Address).ToDictionary(g => g.Key, g => g.MaxBy(r => r.At)!);

    /// <summary>Every recorded A2DP stream (the file may be in use by the session).</summary>
    public static unsafe List<WindowsA2dpCodec> Read(string path)
    {
        if (!IsSupported) return [];
        var found = new List<WindowsA2dpCodec>();
        var context = GCHandle.Alloc(found);
        nint name = Marshal.StringToHGlobalUni(path);
        byte* logFile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
        try
        {
            *(nint*)(logFile + LogFileNameOffset) = name;
            *(uint*)(logFile + ProcessTraceModeOffset) = ProcessTraceModeEventRecord;
            *(delegate* unmanaged<byte*, void>*)(logFile + EventRecordCallbackOffset) = &OnEvent;
            *(nint*)(logFile + ContextOffset) = GCHandle.ToIntPtr(context);
            ulong trace = OpenTraceW(logFile);
            if (trace == InvalidTrace) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                uint error = ProcessTrace(&trace, 1, null, null);
                if (error != 0) throw new Win32Exception((int)error);
            }
            finally
            {
                _ = CloseTrace(trace);
            }
        }
        finally
        {
            NativeMemory.Free(logFile);
            Marshal.FreeHGlobal(name);
            context.Free();
        }
        return found;
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnEvent(byte* record)
    {
        // Nothing may escape into ProcessTrace.
        try
        {
            if (*(Guid*)(record + ProviderIdOffset) != Provider) return;
            if (Property(record, "A2dpStandardCodecId") is not { } standard) return;
            if (Property(record, "A2dpIsSink") is not (null or 0)) return;
            ulong address = Property(record, "BTDeviceAddress") ?? 0;
            string? codec = CodecName(standard, Property(record, "A2dpVendorId") ?? 0, Property(record, "A2dpVendorCodecId") ?? 0);
            if (address == 0 || codec is null) return;
            var found = (List<WindowsA2dpCodec>)GCHandle.FromIntPtr(*(nint*)(record + UserContextOffset)).Target!;
            found.Add(new(address, codec, DateTimeOffset.FromFileTime(*(long*)(record + TimeStampOffset))));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>An integer property of the event (TraceLogging names), or null when it has none.</summary>
    private static unsafe ulong? Property(byte* record, string name)
    {
        fixed (char* text = name)
        {
            var descriptor = new PropertyDataDescriptor { PropertyName = (ulong)text, ArrayIndex = uint.MaxValue };
            uint size;
            if (TdhGetPropertySize(record, 0, null, 1, &descriptor, &size) != 0 || size is 0 or > 8) return null;
            ulong value = 0;
            return TdhGetProperty(record, 0, null, 1, &descriptor, size, (byte*)&value) == 0 ? value : null;
        }
    }

    /// <summary>
    /// Shows the administrator prompt once and registers the session (it starts with every boot), starting it right
    /// away too. Works from the Store package: the elevated programs are Windows' own. A declined prompt throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Task AuthorizeAsync()
    {
        string directory = LogDirectory;
        // 8 KB buffers, at most 4, flushed every second: the provider logs a few events per stream.
        string options = $"-p {ProviderText} 0xffffffffffffffff 0xff -o \"{LogPath}\" -f bincirc -max 1 -bs 8 -nb 2 4 -ft 1";
        return RunElevatedAsync($"(if not exist \"{directory}\" mkdir \"{directory}\")"
            + $" & logman create trace \"autosession\\{SessionName}\" {options} --v -y"
            + $" & logman stop {SessionName} -ets"
            + $" & logman start {SessionName} {options} -ets");
    }

    /// <summary>Stops and unregisters the session and deletes its file (administrator prompt).</summary>
    public static Task RemoveAsync() => RunElevatedAsync(
        $"logman stop {SessionName} -ets & logman delete \"autosession\\{SessionName}\" & rmdir /s /q \"{LogDirectory}\"");

    private static async Task RunElevatedAsync(string commands)
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var start = new ProcessStartInfo(cmd, $"/d /s /c \"{commands}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Cannot start the elevated command.");
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator prompt was declined.", ex);
        }
    }

    // EVENT_TRACE_LOGFILEW and EVENT_RECORD (evntrace.h, evntcons.h), 64-bit.
    private const int LogFileSize = 448;
    private const int LogFileNameOffset = 0;
    private const int ProcessTraceModeOffset = 28;
    private const int EventRecordCallbackOffset = 424;
    private const int ContextOffset = 440;
    private const int TimeStampOffset = 16;
    private const int ProviderIdOffset = 24;
    private const int UserContextOffset = 104;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const ulong InvalidTrace = ulong.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyDataDescriptor
    {
        public ulong PropertyName;
        public uint ArrayIndex;
        public uint Reserved;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static unsafe partial ulong OpenTraceW(byte* logFile);

    [LibraryImport("advapi32.dll")]
    private static unsafe partial uint ProcessTrace(ulong* handles, uint count, long* start, long* end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong handle);

    [LibraryImport("tdh.dll")]
    private static unsafe partial uint TdhGetPropertySize(byte* record, uint contextCount, void* context, uint descriptorCount, PropertyDataDescriptor* descriptors, uint* size);

    [LibraryImport("tdh.dll")]
    private static unsafe partial uint TdhGetProperty(byte* record, uint contextCount, void* context, uint descriptorCount, PropertyDataDescriptor* descriptors, uint size, byte* buffer);
}
