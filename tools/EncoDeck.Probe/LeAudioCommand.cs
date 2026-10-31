// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth;
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Bluetooth.Control;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Probe;

/// <summary>Targeted, bounded LE reconnect test. Pairing is opt-in; the default preserves all bonds/settings.</summary>
internal static class LeAudioCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        var device = await DeviceSelector.SelectAsync(new BluetoothDeviceCatalog(), options, ct);
        if (device is null) return 3;
        void Log(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {message}");
        Log($"Target: {device.Name}; audio codec/CIS streaming cannot be verified from ACL state.");
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }).SetMinimumLevel(LogLevel.Information));
        if (options.Has("pair") || options.Has("restore"))
        {
            if (!options.Has("confirm-write"))
            {
                Console.Error.WriteLine("Replacing this PC's pairings requires --pair or --restore with --confirm-write.");
                return 2;
            }
            // One control runtime for the whole switch, like the app: it carries the Classic prerequisites
            // and reopens over GATT once the primary's LE bond exists.
            await using (var control = await TargetRuntime.CreateAsync(device, loggers, ct))
            {
                if (control is null) return 4;
                control.Supervisor.Start();
                if (options.Has("restore"))
                {
                    var removed = await LeAudioPairing.RemoveLeAudioAsync(device.Address.Value, new Stages(Log), ct,
                        (on, token) => WithControllerAsync(control, c => c.SetLeBrSwitchAsync(on, token)));
                    Log($"Restore result: {removed.Outcome}; {removed.Detail}. Pair the earbuds again in Windows Settings.");
                    return removed.Outcome == LeAudioPairingOutcome.Removed ? 0 : 5;
                }
                var result = await LeAudioPairing.SwitchToLeAudioAsync(device.Address.Value, new Stages(Log), ct,
                    (on, token) => WithControllerAsync(control, c => c.SetLeBrSwitchAsync(on, token)),
                    token => DisableMultipointAsync(control, token),
                    useSetPairing: !options.Has("legacy-pairing"), diagnosticLog: Log,
                    reportLeTransport: token => WithControllerAsync(control, c => c.ReportLeAudioTransportAsync(token)));
                Log($"Pair result: {result.Outcome}; {result.Detail}");
                if (result.AudioEndpoint is { } endpoint)
                    Log($"Endpoint: {endpoint.State}; activeLeRender={endpoint.LeAudioRenderPresent}; residualClassic={endpoint.HasResidualClassicAudio}; {endpoint.Detail}");
                if (result.Outcome is not (LeAudioPairingOutcome.Paired or LeAudioPairingOutcome.PairedWaitingForLinks)) return 5;
            }
        }
        if (!await LeAudioLink.IsBondedAsync(device.Address.Value, ct))
        {
            Console.Error.WriteLine("No primary LE bond. No Classic connection was attempted. Use --pair --confirm-write to pair explicitly.");
            return 4;
        }
        LeAudioLink.MarkPreferred(device.Address.Value);
        await using var runtime = await TargetRuntime.CreateAsync(device, loggers, ct);
        if (runtime is null) return 4;
        var store = runtime.Store;
        (DeviceConnectionState Connection, LeAudioLinkState Links, string? Error)? previous = null;
        bool controlReady = false, bothConnected = false;
        int linkLosses = 0;
        store.StateChanged += (_, change) =>
        {
            var s = change.Current;
            var current = (s.Connection, s.LeAudioLinks, s.LastError?.Message);
            if (previous == current) return;
            if (previous is { } p && p.Links.ConnectedEarbuds > s.LeAudioLinks.ConnectedEarbuds) linkLosses++;
            previous = current;
            controlReady |= s.Connection == DeviceConnectionState.ControlReady;
            bothConnected |= s.LeAudioLinks.BothConnected;
            Log($"control={s.Connection}; primary={s.LeAudioLinks.PrimaryConnected}; secondary={s.LeAudioLinks.SecondaryConnected}; bonds={s.LeAudioLinks.BondedEarbuds}; error={s.LastError?.Message ?? "none"}");
        };
        runtime.Supervisor.Start();
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.GetInt("seconds", 60), 1, 600)), ct);
        var final = store.Current;
        Log($"RESULT controlReadyObserved={controlReady}; bothLinksObserved={bothConnected}; linkLosses={linkLosses}; finalControl={final.Connection}; finalConnectedEars={final.LeAudioLinks.ConnectedEarbuds}; audioStreaming=unverified");
        return final.Connection == DeviceConnectionState.ControlReady && final.LeAudioLinks.BothConnected ? 0 : 5;
    }

    public static async Task<int> DisableMultipointCommandAsync(ProbeOptions options, CancellationToken ct)
    {
        if (!options.Has("confirm-write"))
        {
            Console.Error.WriteLine("Turning multipoint off requires --confirm-write. No setting was changed.");
            return 2;
        }
        var device = await DeviceSelector.SelectAsync(new BluetoothDeviceCatalog(), options, ct);
        if (device is null) return 3;
        CommandResult result;
        await using (var runtime = await TargetRuntime.CreateAsync(device, null, ct))
        {
            if (runtime is null) return 4;
            runtime.Supervisor.Start();
            result = await DisableMultipointAsync(runtime, ct);
        }
        Console.WriteLine($"Multipoint off, device read-back confirmed={result.IsSuccess}; result={result}");
        return result.IsSuccess ? 0 : 5;
    }

    private static async Task<CommandResult> DisableMultipointAsync(TargetRuntime runtime, CancellationToken ct)
    {
        Console.WriteLine("Waiting up to 60 seconds for earbud control, then turning multipoint off with device read-back.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, DeviceStateChangedEventArgs e)
        {
            if (e.Current.Connection == DeviceConnectionState.ControlReady) ready.TrySetResult();
        }
        runtime.Store.StateChanged += OnChanged;
        try
        {
            if (runtime.Store.Current.Connection == DeviceConnectionState.ControlReady) ready.TrySetResult();
            await ready.Task.WaitAsync(timeout.Token);
            // The controller writes SetFeature and verifies QueryFeatureStatus; no cached-state shortcut.
            return runtime.Supervisor.Controller is { } controller
                ? await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, timeout.Token)
                : CommandResult.NotConnected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(CommandStatus.Timeout, "Could not confirm multipoint off; restore the control connection and retry.");
        }
        finally { runtime.Store.StateChanged -= OnChanged; }
    }

    /// <summary>Runs a command on the current controller; NotConnected while control is down (callers retry).</summary>
    private static Task<CommandResult> WithControllerAsync(TargetRuntime runtime, Func<IDeviceController, Task<CommandResult>> action) =>
        runtime.Supervisor.Controller is { } controller ? action(controller) : Task.FromResult(CommandResult.NotConnected);

    /// <summary>A diagnostic command never follows the app's newest-connected-device selection.</summary>
    private sealed class TargetRuntime(BluetoothLinkMonitor link, ControlChannelSupervisor supervisor, DeviceStateStore store) : IAsyncDisposable
    {
        public ControlChannelSupervisor Supervisor { get; } = supervisor;
        public DeviceStateStore Store { get; } = store;

        public static async Task<TargetRuntime?> CreateAsync(PairedDeviceInfo device, ILoggerFactory? loggers, CancellationToken ct)
        {
            var identity = new DeviceIdentity(device.DeviceId, device.Name, device.Address);
            var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
            if (registry.Match(identity) is not IControllableProfile profile) return null;
            var link = await BluetoothLinkMonitor.CreateAsync(device.DeviceId, profile.Form == DeviceForm.Headphones, profile.ManagesLeAudioLink, ct);
            if (link is null) return null;
            var store = new DeviceStateStore();
            store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
            var supervisor = new ControlChannelSupervisor(identity, profile, link,
                new RfcommChannelConnector(registry, loggers?.CreateLogger<RfcommChannelConnector>()), store,
                allowExperimental: () => false, loggers: loggers);
            return new(link, supervisor, store);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Supervisor.DisposeAsync(); }
            finally { link.Dispose(); }
        }
    }

    private sealed class Stages(Action<string> log) : IProgress<LeAudioPairingStage>
    {
        public void Report(LeAudioPairingStage value) => log($"Pair stage: {value}");
    }
}
