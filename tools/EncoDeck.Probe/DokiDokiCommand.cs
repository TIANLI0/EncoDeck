// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace EncoDeck.Probe;

/// <summary>
/// Real-device Probe for experimental DokiDoki/TryFun BLE devices. Only heartbeats and
/// zero/stop frames are implemented; no nonzero motor command exists in this command.
/// </summary>
internal static class DokiDokiCommand
{
    private static readonly TimeSpan AddressTypeScanTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        string? subcommand = options.Positional.FirstOrDefault()?.ToLowerInvariant();
        try
        {
            return subcommand switch
            {
                "scan" => await ScanAsync(options, cancellationToken),
                "connect" => await ConnectAsync(options, cancellationToken),
                "stop" => await StopAsync(options, cancellationToken),
                _ => Usage(subcommand),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"DokiDoki probe failed: 0x{ex.HResult:X8} {ex.Message}");
            return 5;
        }
    }

    private static async Task<int> ScanAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        int seconds = Math.Clamp(options.GetInt("seconds", 5), 1, 120);
        Console.WriteLine($"Scanning for DokiDoki advertisements for {seconds}s (active scan, no pairing) ...");
        var devices = await DokiDokiAdvertisementScanner.ScanAsync(
            TimeSpan.FromSeconds(seconds),
            cancellationToken);

        if (devices.Count == 0)
        {
            Console.WriteLine("No matching DokiDoki advertisements found.");
            return 0;
        }

        Console.WriteLine($"Matched {devices.Count} unique device(s):");
        foreach (var device in devices)
        {
            Console.WriteLine($"- name: {(string.IsNullOrWhiteSpace(device.Name) ? "(unknown)" : device.Name)}");
            Console.WriteLine($"  address: {DokiDokiProtocol.FormatAddress(device.Address)}");
            Console.WriteLine($"  address type: {DokiDokiProtocol.FormatAddressType(device.AddressType)}");
            Console.WriteLine($"  rssi: {device.Rssi} dBm");
            Console.WriteLine(device.ServiceUuids.Count == 0
                ? "  services: (none)"
                : $"  services: {string.Join(", ", device.ServiceUuids.Select(u => u.ToString("D")).Order(StringComparer.Ordinal))}");
            if (device.ManufacturerData.Count == 0)
            {
                Console.WriteLine("  manufacturer data: (none)");
            }
            else
            {
                foreach (var entry in device.ManufacturerData.OrderBy(e => e.Key))
                {
                    Console.WriteLine(
                        $"  manufacturer data: company=0x{entry.Key:X4} hex={(entry.Value.Length == 0 ? "(empty)" : entry.Value)}");
                }
            }
        }
        return 0;
    }

    private static async Task<int> ConnectAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        if (!TryReadAddressTarget(options, out ulong address, out string? requestedType))
            return 2;

        DokiDokiConnection? connection = null;
        try
        {
            var addressType = await ResolveAddressTypeAsync(address, requestedType, cancellationToken);
            connection = await DokiDokiConnection.OpenAsync(address, addressType, cancellationToken);

            var started = DateTimeOffset.UtcNow;
            var subscribeStatus = await connection.SubscribeNotificationsAsync(
                data => Console.WriteLine(
                    $"{(DateTimeOffset.UtcNow - started).TotalSeconds,7:F1}s RX FFB8 len={data.Length} {ConsoleTrace.Spaced(data)}"),
                cancellationToken);
            Console.WriteLine($"Subscribe FFB8 -> {subscribeStatus}");
            if (subscribeStatus != GattCommunicationStatus.Success) return 5;

            int seconds = Math.Clamp(options.GetInt("seconds", 30), 1, 3600);
            int heartbeatMs = Math.Clamp(options.GetInt("heartbeat-ms", 2000), 500, 60_000);
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task heartbeatTask = HeartbeatLoopAsync(
                connection,
                TimeSpan.FromMilliseconds(heartbeatMs),
                heartbeatCts.Token);

            Console.WriteLine(
                $"Connected for {seconds}s; heartbeat every {heartbeatMs}ms. Ctrl+C to stop. Only heartbeat and zero frames are sent.");
            try
            {
                Task durationTask = Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                Task completed = await Task.WhenAny(durationTask, heartbeatTask);
                await completed;
            }
            finally
            {
                heartbeatCts.Cancel();
                try
                {
                    await heartbeatTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    // Cancellation is the normal heartbeat-loop exit path.
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"Heartbeat loop stopped: {ex.Message}");
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Connect failed: 0x{ex.HResult:X8} {ex.Message}");
            return 4;
        }
        finally
        {
            if (connection is not null) await ShutdownAsync(connection);
        }
    }

    private static async Task<int> StopAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        if (!TryReadAddressTarget(options, out ulong address, out string? requestedType))
            return 2;

        DokiDokiConnection? connection = null;
        int failures = DokiDokiProtocol.StopActions.Length;
        bool attempted = false;
        try
        {
            var addressType = await ResolveAddressTypeAsync(address, requestedType, cancellationToken);
            connection = await DokiDokiConnection.OpenAsync(address, addressType, cancellationToken);
            attempted = true;
            failures = await connection.SendStopAllAsync(cancellationToken);
            return failures == 0 ? 0 : 5;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Stop failed: 0x{ex.HResult:X8} {ex.Message}");
            return 4;
        }
        finally
        {
            if (connection is not null)
            {
                if (!attempted || failures > 0)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await connection.SendStopAllAsync(cleanup.Token);
                }
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"Disconnect failed: {ex.Message}");
                }
            }
        }
    }

    private static async Task HeartbeatLoopAsync(
        DokiDokiConnection connection,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var status = await connection.SendHeartbeatAsync(cancellationToken);
            if (status != GattCommunicationStatus.Success)
                throw new IOException($"Heartbeat write failed: {status}.");
        }
    }

    private static async Task ShutdownAsync(DokiDokiConnection connection)
    {
        Console.WriteLine("Exit cleanup: sending zero/stop frames before disconnect.");
        int failures;
        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            failures = await connection.SendStopAllAsync(cleanup.Token);
        }
        Console.WriteLine(failures == 0
            ? "Zero/stop frames completed."
            : $"Warning: {failures} zero/stop frame(s) failed.");
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Disconnect failed: {ex.Message}");
        }
    }

    private static async Task<BluetoothAddressType> ResolveAddressTypeAsync(
        ulong address,
        string? requestedType,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedType)
            && !requestedType.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            DokiDokiProtocol.TryParseAddressType(requestedType, out var explicitType);
            Console.WriteLine($"Address type: {DokiDokiProtocol.FormatAddressType(explicitType)} (explicit)");
            return explicitType;
        }

        Console.WriteLine("Address type not specified; scanning advertisements to identify it ...");
        var observedType = await DokiDokiAdvertisementScanner.FindAddressTypeAsync(
            address,
            AddressTypeScanTimeout,
            cancellationToken);
        if (observedType is { } type)
        {
            Console.WriteLine($"Address type: {DokiDokiProtocol.FormatAddressType(type)} (from advertisement)");
            return type;
        }

        Console.WriteLine(
            $"Address was not advertised within {AddressTypeScanTimeout.TotalSeconds:F0}s; trying Unspecified. " +
            "Use --address-type public|random when needed.");
        return BluetoothAddressType.Unspecified;
    }

    private static bool TryReadAddressTarget(
        ProbeOptions options,
        out ulong address,
        out string? requestedType)
    {
        requestedType = options.Get("address-type");
        if (requestedType is not null
            && !requestedType.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !DokiDokiProtocol.TryParseAddressType(requestedType, out _))
        {
            Console.Error.WriteLine("Invalid --address-type. Use public, random, unspecified, or auto.");
            address = 0;
            return false;
        }
        if (!DokiDokiProtocol.TryParseAddress(options.Get("address"), out address))
        {
            Console.Error.WriteLine("Usage: dokidoki <scan|connect|stop> --address XX:XX:XX:XX:XX:XX");
            return false;
        }
        return true;
    }

    private static int Usage(string? subcommand)
    {
        if (subcommand is not null)
            Console.Error.WriteLine($"Unknown DokiDoki subcommand '{subcommand}'.");
        Console.Error.WriteLine("Usage: EncoDeck.Probe dokidoki <scan|connect|stop> [options]");
        Console.Error.WriteLine("  dokidoki scan [--seconds N]");
        Console.Error.WriteLine("  dokidoki connect --address XX:XX:XX:XX:XX:XX [--address-type public|random|unspecified|auto]");
        Console.Error.WriteLine("                    [--seconds N] [--heartbeat-ms N]");
        Console.Error.WriteLine("  dokidoki stop --address XX:XX:XX:XX:XX:XX [--address-type public|random|unspecified|auto]");
        return 2;
    }
}
