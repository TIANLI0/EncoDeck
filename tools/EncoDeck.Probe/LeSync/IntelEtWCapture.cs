// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EncoDeck.Probe.LeSync;

/// <summary>
/// Research: a real-time ETW session on the Intel Bluetooth driver provider that forwards every event the
/// controller emits through its HCI trace channel, unfiltered. Link statistics are one such event; the audio
/// path's ABxx firmware log entries may be another. Needs an elevated process. One instance per process.
/// </summary>
internal sealed unsafe partial class IntelEtWCapture : IDisposable
{
    public const string SessionName = "EncoDeck-LeAudioTrace";

    private static readonly Guid IntelBluetoothProvider = new("a2cc0e88-0753-4e5e-83a4-b8a789f53fb1");
    private static IntelEtWCapture? _active;

    private const int PropertiesSize = 120;
    private const int PropertiesBufferSize = PropertiesSize + 1024;
    private const int LogFileSize = 448;
    private const int LoggerNameOffset = 8;
    private const int ProcessTraceModeOffset = 28;
    private const int EventRecordCallbackOffset = 424;

    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint EventTraceControlStop = 1;
    private const uint EventControlCodeEnableProvider = 1;
    private const int ErrorAlreadyExists = 183;
    private const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    private readonly Action<ushort, byte[]> _onEvent;
    private readonly nint _properties;
    private readonly nint _logFile;
    private readonly nint _loggerName;
    private ulong _session;
    private ulong _trace = InvalidProcessTraceHandle;
    private Thread? _thread;
    private int _disposed;

    private IntelEtWCapture(Action<ushort, byte[]> onEvent)
    {
        _onEvent = onEvent;
        _properties = Marshal.AllocHGlobal(PropertiesBufferSize);
        _logFile = Marshal.AllocHGlobal(LogFileSize);
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
    }

    public static IntelEtWCapture Start(Action<ushort, byte[]> onEvent)
    {
        if (_active is not null) throw new InvalidOperationException("Only one trace per process");
        var trace = new IntelEtWCapture(onEvent);
        try
        {
            trace.StartSession();
            _active = trace;
            trace.OpenAndProcess();
            return trace;
        }
        catch
        {
            trace.Dispose();
            throw;
        }
    }

    private void StartSession()
    {
        InitProperties();
        int status = StartTraceW(out _session, SessionName, _properties);
        if (status == ErrorAlreadyExists)
        {
            InitProperties();
            ControlTraceW(0, SessionName, _properties, EventTraceControlStop);
            InitProperties();
            status = StartTraceW(out _session, SessionName, _properties);
        }
        if (status != 0) throw new Win32Exception(status, "StartTrace failed");
        Guid provider = IntelBluetoothProvider;
        status = EnableTraceEx2(_session, &provider, EventControlCodeEnableProvider, 0xFF, ulong.MaxValue, 0, 0, 0);
        if (status != 0) throw new Win32Exception(status, "EnableTraceEx2 failed");
    }

    private void InitProperties()
    {
        byte* p = (byte*)_properties;
        new Span<byte>(p, PropertiesBufferSize).Clear();
        *(uint*)(p + 0) = PropertiesBufferSize;
        *(Guid*)(p + 24) = Guid.NewGuid();
        *(uint*)(p + 40) = 1;
        *(uint*)(p + 44) = WnodeFlagTracedGuid;
        *(uint*)(p + 48) = 64;
        *(uint*)(p + 52) = 2;
        *(uint*)(p + 56) = 8;
        *(uint*)(p + 64) = EventTraceRealTimeMode;
        *(uint*)(p + 68) = 1;
        *(uint*)(p + 116) = PropertiesSize;
    }

    private void OpenAndProcess()
    {
        byte* logFile = (byte*)_logFile;
        new Span<byte>(logFile, LogFileSize).Clear();
        *(nint*)(logFile + LoggerNameOffset) = _loggerName;
        *(uint*)(logFile + ProcessTraceModeOffset) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
        *(nint*)(logFile + EventRecordCallbackOffset) = (nint)(delegate* unmanaged[Stdcall]<byte*, void>)&OnEvent;
        _trace = OpenTraceW(_logFile);
        if (_trace == InvalidProcessTraceHandle) throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenTrace failed");
        _thread = new Thread(() =>
        {
            ulong handle = _trace;
            ProcessTrace(&handle, 1, 0, 0);
        })
        { IsBackground = true, Name = "Intel ETW trace" };
        _thread.Start();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(byte* record)
    {
        try
        {
            if (*(Guid*)(record + 24) != IntelBluetoothProvider) return;
            ushort length = *(ushort*)(record + 86);
            byte* data = *(byte**)(record + 96);
            if (data is null || length == 0) return;
            ushort id = *(ushort*)(record + 40);
            _active?._onEvent(id, new ReadOnlySpan<byte>(data, length).ToArray());
        }
        catch
        {
            // Exceptions must not unwind into the native callback.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_session != 0)
        {
            InitProperties();
            ControlTraceW(_session, null, _properties, EventTraceControlStop);
        }
        if (_trace != InvalidProcessTraceHandle) CloseTrace(_trace);
        _thread?.Join(TimeSpan.FromSeconds(5));
        if (ReferenceEquals(_active, this)) _active = null;
        Marshal.FreeHGlobal(_properties);
        Marshal.FreeHGlobal(_logFile);
        Marshal.FreeHGlobal(_loggerName);
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StartTraceW(out ulong session, string name, nint properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ControlTraceW(ulong session, string? name, nint properties, uint control);

    [LibraryImport("advapi32.dll")]
    private static partial int EnableTraceEx2(ulong session, Guid* provider, uint controlCode, byte level, ulong matchAny, ulong matchAll, uint timeout, nint parameters);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial ulong OpenTraceW(nint logFile);

    [LibraryImport("advapi32.dll")]
    private static partial int ProcessTrace(ulong* handles, uint count, nint start, nint end);

    [LibraryImport("advapi32.dll")]
    private static partial int CloseTrace(ulong trace);
}
