// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using EncoDeck.Core.State;
using EncoDeck.Platform.A2dpCodec;
using Microsoft.Extensions.Logging;

namespace EncoDeck.App.Services;

/// <summary>
/// Shows the A2DP codec Windows logs (<see cref="WindowsA2dpCodecLog"/>) on the active device instead of the earbuds'
/// own report. Checks the log's write time every few seconds and reads it (a few milliseconds) only when it changed.
/// Windows logs a codec when a stream ends, and the session starts a new file at every boot, so the latest codec per
/// device is remembered in <c>a2dp-codecs.txt</c>.
/// </summary>
public sealed class WindowsCodecCompanion : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IDeviceStateStore _store;
    private readonly ILogger _logger;
    private readonly string _memoryPath;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<ulong, string> _codecs;
    private DateTime _written;
    private bool _authorized;

    public WindowsCodecCompanion(string root, IDeviceStateStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
        _memoryPath = Path.Combine(root, "a2dp-codecs.txt");
        _codecs = Load(_memoryPath);
        _store.StateChanged += OnStateChanged;
    }

    /// <summary>The log was authorized: it exists and is read.</summary>
    public bool Authorized
    {
        get { lock (_gate) return _authorized; }
    }

    /// <summary>Devices with a codec Windows logged.</summary>
    public int KnownDevices
    {
        get { lock (_gate) return _codecs.Count; }
    }

    /// <summary>Raised on a background thread when <see cref="Authorized"/> or a logged codec changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (!WindowsA2dpCodecLog.IsSupported) return;
        _ = PollAsync(_cts.Token);
    }

    /// <summary>Checks again now, after authorizing or removing the log.</summary>
    public Task RefreshAsync() => Task.Run(Check);

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do Check();
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Check()
    {
        lock (_gate)
        {
            try
            {
                bool authorized = WindowsA2dpCodecLog.IsAuthorized;
                DateTime written = authorized ? File.GetLastWriteTimeUtc(WindowsA2dpCodecLog.LogPath) : default;
                if (authorized == _authorized && written == _written) return;
                bool changed = authorized != _authorized;
                _authorized = authorized;
                _written = written;
                if (authorized)
                {
                    foreach (var latest in WindowsA2dpCodecLog.Latest(WindowsA2dpCodecLog.Read(WindowsA2dpCodecLog.LogPath)).Values)
                    {
                        if (_codecs.TryGetValue(latest.Address, out string? known) && known == latest.Codec) continue;
                        _codecs[latest.Address] = latest.Codec;
                        changed = true;
                        _logger.LogInformation("Windows logged A2DP codec {Codec}", latest.Codec);
                    }
                }
                else if (_codecs.Count > 0)
                {
                    // Removed: forget what it logged too.
                    _codecs.Clear();
                    changed = true;
                }
                if (!changed) return;
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "Could not read Windows' A2DP codec log");
                // Try again on the next tick.
                _written = default;
                return;
            }
        }
        Project();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        var (logged, codec) = For(e.Current);
        if (e.Current.WindowsCodecLogged != logged || e.Current.WindowsCodec != codec) Project();
    }

    private (bool Logged, string? Codec) For(DeviceState state)
    {
        lock (_gate)
            return (_authorized, _authorized && state.Device is { } device && _codecs.TryGetValue(device.Address.Value, out string? codec) ? codec : null);
    }

    private void Project() => _store.TryUpdate(null, s =>
    {
        var (logged, codec) = For(s);
        return s.WindowsCodecLogged == logged && s.WindowsCodec == codec ? s : s with { WindowsCodecLogged = logged, WindowsCodec = codec };
    });

    /// <summary>One "address codec" line per device.</summary>
    private static Dictionary<ulong, string> Load(string path)
    {
        var codecs = new Dictionary<ulong, string>();
        try
        {
            if (!File.Exists(path)) return codecs;
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[1].Length > 0 && ulong.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
                    codecs[address] = parts[1];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return codecs;
    }

    private void Save()
    {
        if (_codecs.Count == 0)
        {
            File.Delete(_memoryPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_memoryPath)!);
        File.WriteAllLines(_memoryPath, _codecs.Select(c => $"{c.Key.ToString("X12", CultureInfo.InvariantCulture)} {c.Value}"));
    }

    public void Dispose()
    {
        _store.StateChanged -= OnStateChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
