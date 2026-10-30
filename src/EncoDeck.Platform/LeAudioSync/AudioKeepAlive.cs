// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace EncoDeck.Platform.LeAudioSync;

/// <summary>A silent stream that keeps an endpoint's LE Audio link (its CIS) established.</summary>
public interface IKeepAliveStream : IDisposable
{
    /// <summary>Raised on a background thread when Windows invalidates the stream (the endpoint went away).</summary>
    event EventHandler? Lost;

    /// <summary>
    /// Plays the left/right check on this same stream (so the link is not rebuilt): identical clicks in every channel.
    /// Aligned, they sound in the middle of the head; a one-frame offset pulls them to one side or doubles them.
    /// </summary>
    void PlayCheck(TimeSpan duration);

    void StopCheck();
}

/// <summary>
/// Shared-mode WASAPI render stream that submits zero samples (and, on request, the check clicks). While any stream is
/// open on the LE Audio endpoint Windows keeps the CIS; streams other apps open later join it and inherit its alignment.
/// </summary>
public sealed class AudioKeepAlive : IKeepAliveStream
{
    private const int ClsctxAll = 0x17;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const float ClickLevel = 0.2f;

    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private ushort _blockAlign;
    private int _channels;
    private int _sampleRate;
    private SampleType _sampleType;
    // Frames of check clicks left to play; written by PlayCheck/StopCheck, read by the stream thread.
    private long _checkFramesLeft;
    private long _checkPosition;

    private enum SampleType { Unsupported, Float32, Int16, Int32 }

    private AudioKeepAlive(string endpointId)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(endpointId, started)) { IsBackground = true, Name = "LE Audio keep-alive" };
        _thread.Start();
        started.Task.GetAwaiter().GetResult();
    }

    public event EventHandler? Lost;

    /// <summary>Opens the stream on an MMDevice endpoint id ({0.0.0.00000000}.{guid}); throws when it cannot start.</summary>
    public static AudioKeepAlive Open(string endpointId) => new(endpointId);

    private void Run(string endpointId, TaskCompletionSource started)
    {
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        uint bufferFrames;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Check(enumerator.GetDevice(endpointId, out var device));
            Guid audioClient = typeof(IAudioClient).GUID;
            Check(device.Activate(ref audioClient, ClsctxAll, 0, out object clientObject));
            client = (IAudioClient)clientObject;
            Check(client.GetMixFormat(out nint format));
            try
            {
                ReadFormat(format);
                // Event-driven with the engine's own period (buffer duration 0), like an ordinary playback app.
                Check(client.Initialize(0, StreamFlagsEventCallback, 0, 0, format, 0));
            }
            finally
            {
                Marshal.FreeCoTaskMem(format);
            }
            Check(client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
            Check(client.GetBufferSize(out bufferFrames));
            Guid renderClient = typeof(IAudioRenderClient).GUID;
            Check(client.GetService(ref renderClient, out object renderObject));
            render = (IAudioRenderClient)renderObject;
            Check(Fill(client, render, bufferFrames));
            Check(client.Start());
            started.SetResult();
        }
        catch (Exception ex)
        {
            Release(render);
            Release(client);
            started.TrySetException(ex);
            return;
        }

        try
        {
            WaitHandle[] handles = [_stop, _ready];
            // The engine signals once per period; the timeout only catches a stream that stopped signalling.
            while (WaitHandle.WaitAny(handles, 500) != 0)
            {
                // Any failure (AUDCLNT_E_DEVICE_INVALIDATED when the endpoint goes away) ends the stream.
                if (Fill(client, render, bufferFrames) < 0)
                {
                    Lost?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
            client.Stop();
        }
        finally
        {
            Release(render);
            Release(client);
        }
    }

    /// <summary>WAVEFORMATEX(TENSIBLE): tag @0, channels @2, rate @4, block align @12, bits @14, sub-format GUID @24.</summary>
    private void ReadFormat(nint format)
    {
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        _channels = (ushort)Marshal.ReadInt16(format, 2);
        _sampleRate = Marshal.ReadInt32(format, 4);
        _blockAlign = (ushort)Marshal.ReadInt16(format, 12);
        ushort bits = (ushort)Marshal.ReadInt16(format, 14);
        Guid subtype = tag == 0xFFFE ? Marshal.PtrToStructure<Guid>(format + 24) : Guid.Empty;
        bool isFloat = tag == 3 || subtype == SubtypeIeeeFloat;
        bool isPcm = tag == 1 || subtype == SubtypePcm;
        _sampleType = (isFloat && bits == 32) ? SampleType.Float32
            : (isPcm && bits == 16) ? SampleType.Int16
            : (isPcm && bits == 32) ? SampleType.Int32
            : SampleType.Unsupported;
    }

    public void PlayCheck(TimeSpan duration)
    {
        if (_sampleType == SampleType.Unsupported) throw new NotSupportedException("The endpoint's mix format is not supported");
        Interlocked.Exchange(ref _checkPosition, 0);
        Interlocked.Exchange(ref _checkFramesLeft, (long)(duration.TotalSeconds * _sampleRate));
    }

    public void StopCheck() => Interlocked.Exchange(ref _checkFramesLeft, 0);

    /// <summary>Tops the buffer up with zero samples, or with the check clicks while they play.</summary>
    private unsafe int Fill(IAudioClient client, IAudioRenderClient render, uint bufferFrames)
    {
        int hr = client.GetCurrentPadding(out uint padding);
        if (hr < 0) return hr;
        uint frames = bufferFrames - padding;
        if (frames == 0) return 0;
        hr = render.GetBuffer(frames, out nint data);
        if (hr < 0) return hr;
        new Span<byte>((void*)data, checked((int)(frames * _blockAlign))).Clear();
        long left = Interlocked.Read(ref _checkFramesLeft);
        if (left > 0)
        {
            int count = (int)Math.Min(frames, left);
            WriteClicks((byte*)data, count);
            Interlocked.Add(ref _checkFramesLeft, -count);
        }
        return render.ReleaseBuffer(frames, 0);
    }

    /// <summary>Four 1 ms clicks a second, the same sample in every channel.</summary>
    private unsafe void WriteClicks(byte* data, int frames)
    {
        int interval = _sampleRate / 4;
        int length = Math.Max(8, _sampleRate / 1000);
        long position = Interlocked.Read(ref _checkPosition);
        for (int f = 0; f < frames; f++, position++)
        {
            long phase = position % interval;
            if (phase >= length) continue;
            // One sine cycle under a Hann window: a short, broadband click that is easy to localise.
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase / length);
            float value = (float)(ClickLevel * window * Math.Sin(2 * Math.PI * phase / length));
            byte* frame = data + (long)f * _blockAlign;
            for (int c = 0; c < _channels; c++)
            {
                switch (_sampleType)
                {
                    case SampleType.Float32: ((float*)frame)[c] = value; break;
                    case SampleType.Int16: ((short*)frame)[c] = (short)(value * short.MaxValue); break;
                    case SampleType.Int32: ((int*)frame)[c] = (int)(value * int.MaxValue); break;
                }
            }
        }
        Interlocked.Exchange(ref _checkPosition, position);
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _stop.Dispose();
            _ready.Dispose();
        }
    }

    /// <summary>Whether another process (or system sounds) is playing on the endpoint right now.</summary>
    public static bool HasOtherActiveSessions(string endpointId) => HasActiveSessions(endpointId, excludeSelf: true);

    /// <summary>
    /// Whether any program records from the earbuds' LE Audio microphone endpoint. Windows then runs a different
    /// (bidirectional) link, so the alignment checked before no longer applies.
    /// </summary>
    public static bool HasActiveCapture(string captureEndpointId) => HasActiveSessions(captureEndpointId, excludeSelf: false);

    private static bool HasActiveSessions(string endpointId, bool excludeSelf)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Check(enumerator.GetDevice(endpointId, out var device));
        Guid managerId = typeof(IAudioSessionManager2).GUID;
        Check(device.Activate(ref managerId, ClsctxAll, 0, out object managerObject));
        var manager = (IAudioSessionManager2)managerObject;
        Check(manager.GetSessionEnumerator(out var sessions));
        Check(sessions.GetCount(out int count));
        uint self = (uint)Environment.ProcessId;
        for (int i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out object sessionObject) < 0) continue;
            var session = (IAudioSessionControl2)sessionObject;
            try
            {
                if (session.GetState(out int state) < 0 || state != AudioSessionStateActive) continue;
                if (excludeSelf && session.GetProcessId(out uint pid) >= 0 && pid == self) continue;
                return true;
            }
            finally
            {
                Release(session);
            }
        }
        return false;
    }

    private const int AudioSessionStateActive = 1;

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out nint data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(nint notification);
        [PreserveSig] int UnregisterSessionNotification(nint notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint notification);
        [PreserveSig] int UnregisterDuckNotification(nint notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint eventContext);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, nint eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
