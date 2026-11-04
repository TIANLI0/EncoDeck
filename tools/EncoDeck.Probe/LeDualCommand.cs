// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace EncoDeck.Probe;

/// <summary>
/// Experiment for keeping both transports of LE Audio earbuds: logs the Classic link and both LE links separately,
/// optionally holds the LE links (MaintainConnection) and the Classic link (OPPO RFCOMM control), switches the
/// earbuds' LE/BR mode (0x0433) over that channel and installs or removes Windows' Classic audio services.
/// Never unpairs anything.
/// </summary>
internal static class LeDualCommand
{
    private static readonly Guid A2dpSink = new("0000110B-0000-1000-8000-00805F9B34FB");
    private static readonly Guid AvrcpTarget = new("0000110C-0000-1000-8000-00805F9B34FB");
    private static readonly Guid AvrcpController = new("0000110E-0000-1000-8000-00805F9B34FB");
    private static readonly Guid Handsfree = new("0000111E-0000-1000-8000-00805F9B34FB");
    private static readonly Guid[] AudioServices = [A2dpSink, AvrcpTarget, AvrcpController, Handsfree];

    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        int? switchTo = options.Has("switch") ? options.GetInt("switch", -1) : null;
        string? services = options.Get("services");
        if (switchTo is not (null or 0 or 1) || services is not (null or "list" or "enable" or "disable"))
        {
            Console.Error.WriteLine("Refused: --switch takes 0 or 1; --services takes list, enable or disable.");
            return 2;
        }
        if ((switchTo is not null || services is "enable" or "disable") && !options.Has("confirm-write"))
        {
            Console.Error.WriteLine("Refused: --switch and --services enable/disable change the earbuds or Windows. Re-run with --confirm-write.");
            return 2;
        }

        var device = await DeviceSelector.SelectAsync(new BluetoothDeviceCatalog(), options, ct);
        if (device is null) return 3;
        ulong address = device.Address.Value;
        var clock = Stopwatch.StartNew();
        var gate = new object();
        void Log(string message)
        {
            lock (gate) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{clock.Elapsed.TotalSeconds,6:0.0}s {message}");
        }

        using var classic = await BluetoothDevice.FromIdAsync(device.DeviceId).AsTask(ct);
        using var primary = await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(ct);
        var secondaries = await LeAudioPairing.FindSecondaryBondsAsync(address, primary?.Name ?? device.Name, ct);
        var sessions = new List<GattSession>();
        try
        {
            Log($"Target {device.Name}: Classic paired={classic?.DeviceInformation.Pairing.IsPaired} {classic?.ConnectionStatus}; " +
                $"LE primary bonded={await LeAudioLink.IsBondedAsync(address, ct)} {primary?.ConnectionStatus}; LE secondaries={secondaries.Count}");

            var links = new LinkTimeline(Log);
            if (classic is not null) links.Watch("BR", () => classic.ConnectionStatus, h => classic.ConnectionStatusChanged += (_, _) => h());
            if (primary is not null) links.Watch("LE-primary", () => primary.ConnectionStatus, h => primary.ConnectionStatusChanged += (_, _) => h());
            foreach (var secondary in secondaries)
                links.Watch("LE-secondary", () => secondary.ConnectionStatus, h => secondary.ConnectionStatusChanged += (_, _) => h());

            if (options.Has("maintain"))
            {
                foreach (var le in secondaries.Prepend(primary).OfType<BluetoothLEDevice>())
                {
                    var session = await GattSession.FromDeviceIdAsync(le.BluetoothDeviceId).AsTask(ct);
                    if (session is null) continue;
                    session.MaintainConnection = true;
                    sessions.Add(session);
                }
                Log($"MaintainConnection held on {sessions.Count} LE identities");
            }

            if (services is not null) ApplyServices(address, services, Log);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var control = new ControlHolder(classic, address, options.Has("hold-rfcomm"), Log);
            var tasks = new List<Task>
            {
                WatchEndpointsAsync(address, Log, stop.Token),
                WatchServiceNodesAsync(address, Log, stop.Token),
                control.RunAsync(stop.Token),
            };
            if (switchTo is { } state)
                tasks.Add(SwitchLaterAsync(control, (byte)state, TimeSpan.FromSeconds(options.GetInt("switch-after", 20)), Log, stop.Token));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.GetInt("seconds", 120), 5, 1800)), ct);
            }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
                await control.DisposeAsync();
            }
            links.PrintSummary();
            return 0;
        }
        finally
        {
            foreach (var session in sessions) session.Dispose();
            foreach (var secondary in secondaries) secondary.Dispose();
        }
    }

    private static async Task SwitchLaterAsync(ControlHolder control, byte state, TimeSpan after, Action<string> log, CancellationToken ct)
    {
        await Task.Delay(after, ct);
        for (int attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
        {
            var result = await control.SetLeBrSwitchAsync(state, ct);
            if (result is not null)
            {
                log($"0x0433 [{state:X2}] -> {result.Status} {(result.Response is { } r ? ConsoleTrace.PayloadText(r) : result.Detail)}");
                await Task.Delay(2000, ct);
                await control.LogSwitchStateAsync("after switch", ct);
                return;
            }
            await Task.Delay(1000, ct);
        }
        log($"0x0433 [{state:X2}] not sent: no control channel");
    }

    /// <summary>Logs each link's transitions and how long every connected period lasted.</summary>
    internal sealed class LinkTimeline(Action<string> log)
    {
        private readonly List<(string Name, List<double> Durations, int Drops)> _summary = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void Watch(string name, Func<BluetoothConnectionStatus> status, Action<Action> subscribe)
        {
            var durations = new List<double>();
            int index = _summary.Count;
            _summary.Add((name, durations, 0));
            var last = status();
            double since = _clock.Elapsed.TotalSeconds;
            log($"{name}: {last}");
            subscribe(() =>
            {
                lock (durations)
                {
                    var now = status();
                    if (now == last) return;
                    double t = _clock.Elapsed.TotalSeconds;
                    if (last == BluetoothConnectionStatus.Connected)
                    {
                        durations.Add(t - since);
                        _summary[index] = (name, durations, _summary[index].Drops + 1);
                        log($"{name}: Disconnected after {t - since:0.0}s");
                    }
                    else log($"{name}: Connected");
                    last = now;
                    since = t;
                }
            });
        }

        public void PrintSummary()
        {
            foreach (var (name, durations, drops) in _summary)
                log($"SUMMARY {name}: drops={drops}; connected periods={string.Join(", ", durations.Select(d => $"{d:0.0}s"))}");
        }
    }

    /// <summary>Optionally keeps the OPPO RFCOMM control channel open (holds the Classic link) and sends 0x0433 over it or GATT.</summary>
    private sealed class ControlHolder(BluetoothDevice? classic, ulong address, bool holdRfcomm, Action<string> log) : IAsyncDisposable
    {
        private OppoSession? _session;
        private string? _transport;

        public async Task RunAsync(CancellationToken ct)
        {
            if (!holdRfcomm || classic is null) return;
            while (!ct.IsCancellationRequested)
            {
                if (_session is null)
                {
                    // Connecting RFCOMM to an unpaired device makes Windows pair it over Classic (just works).
                    BluetoothDevice? fresh;
                    try
                    {
                        // Hangs while the radio is off.
                        fresh = await BluetoothDevice.FromIdAsync(classic.DeviceId).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                    }
                    catch (TimeoutException)
                    {
                        log("Classic device lookup timed out (radio off?)");
                        continue;
                    }
                    using (fresh)
                    {
                        if (fresh?.DeviceInformation.Pairing.IsPaired != true)
                        {
                            log("Classic pairing gone: not opening RFCOMM");
                            await Task.Delay(3000, ct);
                            continue;
                        }
                    }
                    var opened = await OpenRfcommAsync(classic, ct);
                    if (opened is null)
                    {
                        await Task.Delay(3000, ct);
                        continue;
                    }
                    _session = opened;
                    _transport = "RFCOMM";
                    var hello = await opened.RequestAsync(QueryCommands.StandardQueries[0].Request, cancellationToken: ct);
                    log($"RFCOMM control opened; handshake {hello.Status}");
                    await LogSwitchStateAsync("on open", ct);
                }
                await Task.Delay(15000, ct);
                // A cheap read keeps the channel honest; a failure means the Classic side went away.
                var battery = await _session.RequestAsync(QueryCommands.StandardQueries[4].Request, TimeSpan.FromSeconds(5), ct);
                if (!battery.IsSuccess)
                {
                    log($"RFCOMM control lost ({battery.Status} {battery.Detail})");
                    await _session.DisposeAsync();
                    _session = null;
                }
            }
        }

        public async Task LogSwitchStateAsync(string label, CancellationToken ct)
        {
            if (_session is null) return;
            var r = await _session.RequestAsync(new OppoRequest(OppoCommand.QueryLeBrSwitch, []), cancellationToken: ct);
            if (r.Response is { } response && OppoFeatureParsers.TryParseLeAudioSwitch(response, out var s))
                log($"{label}: 0x0433 state={(s.Enabled ? 1 : 0)} owner={(s.Owner ? 1 : 0)} reset={(s.ResetFlag ? 1 : 0)}");
            else
                log($"{label}: 0x0433 query {r.Status} {(r.Response is { } f ? ConsoleTrace.PayloadText(f) : r.Detail)}");
        }

        public async Task<RequestResult?> SetLeBrSwitchAsync(byte state, CancellationToken ct)
        {
            if (_session is null)
            {
                // Not holding Classic: use OPPO GATT, as the app does on LE Audio.
                var gatt = await OppoGattByteChannel.OpenAsync(address, ct);
                if (gatt.Status != ChannelOpenStatus.Opened) return null;
                _session = new OppoSession(gatt.Channel!);
                _session.Start();
                _transport = "GATT";
                await _session.RequestAsync(QueryCommands.StandardQueries[0].Request, cancellationToken: ct);
            }
            log($"sending 0x0433 [{state:X2}] over {_transport}");
            return await _session.RequestAsync(new OppoRequest(OppoCommand.SetLeBrSwitch, [state]), TimeSpan.FromSeconds(5), ct);
        }

        private async Task<OppoSession?> OpenRfcommAsync(BluetoothDevice device, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            StreamSocket? socket = null;
            try
            {
                var found = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(OppoServiceUuids.Secondary), BluetoothCacheMode.Cached).AsTask(timeout.Token);
                var service = found.Services.FirstOrDefault();
                if (service is null)
                {
                    log($"RFCOMM 079A not found ({found.Error})");
                    return null;
                }
                socket = new StreamSocket();
                socket.Control.KeepAlive = false;
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(timeout.Token);
                foreach (var s in found.Services) s.Dispose();
                var session = new OppoSession(new RfcommByteChannel(socket, "RFCOMM 079A"), trace: new ConsoleTrace(printRaw: false) { Quiet = true });
                session.Closed += (_, e) => log($"RFCOMM session closed: {e.Reason}");
                session.Start();
                socket = null;
                return session;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            {
                log($"RFCOMM open failed: 0x{ex.HResult:X8} {ex.Message.Trim()}");
                return null;
            }
            finally { socket?.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_session is not null) await _session.DisposeAsync();
        }
    }

    private static async Task WatchEndpointsAsync(ulong address, Action<string> log, CancellationToken ct)
    {
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            string now;
            try
            {
                var inspection = await LeAudioClassicAudioEndpoint.InspectAsync(address, ct);
                now = $"endpoints: state={inspection.State}; LE={Describe(inspection.LeAudioRender)}; A2DP={Describe(inspection.ClassicA2dpRender)}; HFP={Describe(inspection.ClassicHfpRender)}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                now = $"endpoints: inspection failed 0x{ex.HResult:X8}";
            }
            if (now != last) log(now);
            last = now;
            await Task.Delay(1000, ct);
        }
    }

    private static string Describe(AudioEndpointInfo? endpoint) => endpoint is null
        ? "none"
        : $"{(endpoint.IsDisabled ? "disabled" : endpoint.IsActive ? "active" : "inactive")}";

    private static async Task WatchServiceNodesAsync(ulong address, Action<string> log, CancellationToken ct)
    {
        string text = address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var nodes = await Windows.Devices.Enumeration.Pnp.PnpObject.FindAllAsync(
                Windows.Devices.Enumeration.Pnp.PnpObjectType.Device, ["System.Devices.DeviceInstanceId"],
                $"System.Devices.DeviceInstanceId:~~\"{text}\"").AsTask(ct);
            var guids = nodes
                .Select(n => n.Properties.TryGetValue("System.Devices.DeviceInstanceId", out var id) ? id as string : null)
                .Where(id => id is not null && id.StartsWith("BTHENUM\\{", StringComparison.OrdinalIgnoreCase))
                .Select(id => id!.Substring(9, 8))
                .Order()
                .ToList();
            string now = $"Classic service nodes: {string.Join(" ", guids)}";
            if (now != last) log(now);
            last = now;
            await Task.Delay(3000, ct);
        }
    }

    private static void ApplyServices(ulong address, string action, Action<string> log)
    {
        var find = new FindRadioParams { Size = Marshal.SizeOf<FindRadioParams>() };
        IntPtr search = BluetoothFindFirstRadio(ref find, out IntPtr radio);
        if (search == IntPtr.Zero)
        {
            log($"No Bluetooth radio handle: {Marshal.GetLastWin32Error()}");
            return;
        }
        BluetoothFindRadioClose(search);
        try
        {
            var info = new BluetoothDeviceInfo { Size = Marshal.SizeOf<BluetoothDeviceInfo>(), Address = address };
            int got = BluetoothGetDeviceInfo(radio, ref info);
            log($"BluetoothGetDeviceInfo: {got}; remembered={info.Remembered}; authenticated={info.Authenticated}; connected={info.Connected}");
            PrintInstalled(radio, info, log);
            if (action == "list") return;
            uint flags = action == "enable" ? 1u : 0u;
            foreach (var guid in AudioServices)
            {
                var service = guid;
                int result = BluetoothSetServiceState(radio, ref info, ref service, flags);
                log($"BluetoothSetServiceState {guid:D} {action}: {result}");
            }
            PrintInstalled(radio, info, log);
        }
        finally { CloseHandle(radio); }
    }

    private static void PrintInstalled(IntPtr radio, BluetoothDeviceInfo info, Action<string> log)
    {
        uint count = 32;
        var guids = new Guid[count];
        int result = BluetoothEnumerateInstalledServices(radio, ref info, ref count, guids);
        log($"Installed services ({result}): {string.Join(" ", guids.Take((int)Math.Min(count, 32)).Select(g => g.ToString("D")[..8]))}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams
    {
        public int Size;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams parameters, out IntPtr radio);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindRadioClose(IntPtr search);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int Size;
        public ulong Address;
        public uint ClassOfDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        public SystemTime LastSeen;
        public SystemTime LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothGetDeviceInfo(IntPtr radio, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo info, ref Guid service, uint flags);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothEnumerateInstalledServices(IntPtr radio, ref BluetoothDeviceInfo info, ref uint count, [Out] Guid[] services);
}
