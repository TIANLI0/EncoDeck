// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace EncoDeck.Probe;

/// <summary>Direct WinRT GATT connection used only by the real-device DokiDoki probe.</summary>
internal sealed class DokiDokiConnection : IAsyncDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    private readonly BluetoothLEDevice _device;
    private readonly GattSession _gattSession;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _heartbeatCharacteristic;
    private readonly GattWriteOption _heartbeatWriteOption;
    private readonly GattCharacteristic _controlCharacteristic;
    private readonly GattWriteOption _controlWriteOption;
    private readonly GattCharacteristic _notifyCharacteristic;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly DokiDokiMessageIds _messageIds = new();

    private TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? _notificationHandler;
    private bool _notificationsSubscribed;
    private int _disposed;

    private DokiDokiConnection(
        BluetoothLEDevice device,
        GattSession gattSession,
        GattDeviceService service,
        GattCharacteristic heartbeatCharacteristic,
        GattWriteOption heartbeatWriteOption,
        GattCharacteristic controlCharacteristic,
        GattWriteOption controlWriteOption,
        GattCharacteristic notifyCharacteristic)
    {
        _device = device;
        _gattSession = gattSession;
        _service = service;
        _heartbeatCharacteristic = heartbeatCharacteristic;
        _heartbeatWriteOption = heartbeatWriteOption;
        _controlCharacteristic = controlCharacteristic;
        _controlWriteOption = controlWriteOption;
        _notifyCharacteristic = notifyCharacteristic;
    }

    public static async Task<DokiDokiConnection> OpenAsync(
        ulong address,
        BluetoothAddressType addressType,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattSession? gattSession = null;
        GattDeviceService? service = null;
        try
        {
            Console.WriteLine($"Opening BLE device {DokiDokiProtocol.FormatAddress(address)} ({DokiDokiProtocol.FormatAddressType(addressType)}) ...");
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType)
                .AsTask(cancellationToken);
            if (device is null)
                throw new IOException("Windows returned no Bluetooth LE device for that address and address type.");

            var access = await device.RequestAccessAsync().AsTask(cancellationToken);
            if (access != DeviceAccessStatus.Allowed)
                throw new UnauthorizedAccessException($"Bluetooth LE device access is {access}.");

            gattSession = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)
                .AsTask(cancellationToken);
            if (gattSession is null)
                throw new IOException("Windows could not create a GATT session.");
            gattSession.MaintainConnection = true;
            Console.WriteLine("GattSession.MaintainConnection = true");

            await ListAllServicesAsync(device, cancellationToken);

            var serviceResult = await device
                .GetGattServicesForUuidAsync(DokiDokiProtocol.ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);
            if (serviceResult.Status != GattCommunicationStatus.Success)
                throw new IOException($"Uncached FFAC service discovery failed: {serviceResult.Status}.");

            for (int i = 0; i < serviceResult.Services.Count; i++)
            {
                if (i == 0) service = serviceResult.Services[i];
                else serviceResult.Services[i].Dispose();
            }
            if (service is null)
                throw new IOException("DokiDoki service FFAC was not found.");

            var characteristicResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);
            if (characteristicResult.Status != GattCommunicationStatus.Success)
                throw new IOException($"FFAC characteristic discovery failed: {characteristicResult.Status}.");

            var characteristics = characteristicResult.Characteristics;
            var writeResponse = characteristics.FirstOrDefault(c => c.Uuid == DokiDokiProtocol.WriteWithResponseUuid);
            var writeWithoutResponse = characteristics.FirstOrDefault(c => c.Uuid == DokiDokiProtocol.WriteWithoutResponseUuid);
            var notify = characteristics.FirstOrDefault(c => c.Uuid == DokiDokiProtocol.NotifyUuid);

            if (notify is null)
                throw new IOException("Notify characteristic FFB8 was not found.");
            if (writeResponse is null && writeWithoutResponse is null)
                throw new IOException("Neither control characteristic FFB5 nor FFB7 was found.");

            var heartbeatCharacteristic = writeResponse
                ?? writeWithoutResponse
                ?? throw new IOException("No heartbeat write characteristic is available.");
            var heartbeatWriteOption = SupportsWrite(writeResponse, GattCharacteristicProperties.Write)
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;
            if (heartbeatWriteOption == GattWriteOption.WriteWithoutResponse
                && !SupportsWrite(writeWithoutResponse, GattCharacteristicProperties.WriteWithoutResponse))
                throw new IOException("No writable heartbeat characteristic is available.");

            var controlCharacteristic = writeWithoutResponse
                ?? writeResponse
                ?? throw new IOException("No stop/zero write characteristic is available.");
            var controlWriteOption = SupportsWrite(writeWithoutResponse, GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;
            if (controlWriteOption == GattWriteOption.WriteWithResponse
                && !SupportsWrite(writeResponse, GattCharacteristicProperties.Write))
                throw new IOException("No writable stop/zero characteristic is available.");

            var connection = new DokiDokiConnection(
                device,
                gattSession,
                service,
                heartbeatCharacteristic,
                heartbeatWriteOption,
                controlCharacteristic,
                controlWriteOption,
                notify);
            device = null;
            gattSession = null;
            service = null;
            return connection;
        }
        finally
        {
            service?.Dispose();
            gattSession?.Dispose();
            device?.Dispose();
        }
    }

    public async Task<GattCommunicationStatus> SubscribeNotificationsAsync(
        Action<byte[]> onNotification,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_notificationsSubscribed) throw new InvalidOperationException("Notifications are already subscribed.");

        _notificationHandler = (_, args) =>
        {
            if (Volatile.Read(ref _disposed) == 0 && args.CharacteristicValue.Length > 0)
                onNotification(args.CharacteristicValue.ToArray());
        };
        _notifyCharacteristic.ValueChanged += _notificationHandler;
        try
        {
            var status = await _notifyCharacteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken);
            _notificationsSubscribed = status == GattCommunicationStatus.Success;
            return status;
        }
        finally
        {
            if (!_notificationsSubscribed)
            {
                _notifyCharacteristic.ValueChanged -= _notificationHandler;
                _notificationHandler = null;
            }
        }
    }

    public async Task<GattCommunicationStatus> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            byte[] frame = DokiDokiProtocol.BuildHeartbeatFrame(_messageIds.Next());
            return await WriteAsync(_heartbeatCharacteristic, _heartbeatWriteOption, frame, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Best-effort zero for every known motor channel. Returns the number of failed writes.</summary>
    public async Task<int> SendStopAllAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return DokiDokiProtocol.StopActions.Length;

        int failures = 0;
        foreach (var action in DokiDokiProtocol.StopActions)
        {
            try
            {
                await _writeGate.WaitAsync(cancellationToken);
                try
                {
                    byte[] frame = DokiDokiProtocol.BuildZeroFrame(_messageIds.Next(), action.Command);
                    var status = await WriteAsync(
                        _controlCharacteristic,
                        _controlWriteOption,
                        frame,
                        cancellationToken);
                    Console.WriteLine($"Zero {action.Name,-9} -> {status}  {FormatTx(frame)}");
                    if (status != GattCommunicationStatus.Success) failures++;
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                Console.Error.WriteLine($"Zero {action.Name,-9} -> FAILED: {ex.Message}");
            }
        }
        return failures;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_notificationHandler is not null)
        {
            _notifyCharacteristic.ValueChanged -= _notificationHandler;
            _notificationHandler = null;
        }

        if (_notificationsSubscribed)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _notifyCharacteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or COMException)
            {
                Console.Error.WriteLine($"Could not unsubscribe FFB8: {ex.Message}");
            }
            _notificationsSubscribed = false;
        }

        try
        {
            _gattSession.MaintainConnection = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            Console.Error.WriteLine($"Could not release maintained connection: {ex.Message}");
        }

        _service.Dispose();
        _gattSession.Dispose();
        _device.Dispose();
        _writeGate.Dispose();
        Console.WriteLine("Disconnected BLE device.");
    }

    private async Task<GattCommunicationStatus> WriteAsync(
        GattCharacteristic characteristic,
        GattWriteOption writeOption,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"TX {writeOption,-18} {FormatTx(frame)}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WriteTimeout);
        return await characteristic.WriteValueAsync(frame.AsBuffer(), writeOption).AsTask(timeout.Token);
    }

    private static async Task ListAllServicesAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Uncached service discovery failed: {result.Status}.");

        Console.WriteLine($"Services ({result.Services.Count}):");
        foreach (var service in result.Services)
        {
            try
            {
                string serviceName = service.Uuid == DokiDokiProtocol.ServiceUuid ? "  <-- DokiDoki FFAC" : string.Empty;
                Console.WriteLine($"- {service.Uuid:D}{serviceName}");
                try
                {
                    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                        .AsTask(cancellationToken);
                    if (characteristics.Status != GattCommunicationStatus.Success)
                    {
                        Console.WriteLine($"    characteristics unavailable: {characteristics.Status}");
                        continue;
                    }
                    foreach (var characteristic in characteristics.Characteristics)
                    {
                        Console.WriteLine(
                            $"    {CharacteristicLabel(characteristic.Uuid),-38} {PropertiesText(characteristic.CharacteristicProperties)}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"    characteristics unavailable: {ex.Message}");
                }
            }
            finally
            {
                service.Dispose();
            }
        }
    }

    private static string CharacteristicLabel(Guid uuid)
    {
        string text = uuid.ToString("D");
        return text.StartsWith("0000", StringComparison.Ordinal)
            && text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.Ordinal)
            ? $"{text[4..8].ToUpperInvariant()} ({text})"
            : text;
    }

    private static string PropertiesText(GattCharacteristicProperties properties)
    {
        var names = new List<string>();
        if (properties.HasFlag(GattCharacteristicProperties.Broadcast)) names.Add("Broadcast");
        if (properties.HasFlag(GattCharacteristicProperties.Read)) names.Add("Read");
        if (properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) names.Add("WriteWithoutResponse");
        if (properties.HasFlag(GattCharacteristicProperties.Write)) names.Add("Write");
        if (properties.HasFlag(GattCharacteristicProperties.Notify)) names.Add("Notify");
        if (properties.HasFlag(GattCharacteristicProperties.Indicate)) names.Add("Indicate");
        if (properties.HasFlag(GattCharacteristicProperties.AuthenticatedSignedWrites)) names.Add("SignedWrite");
        if (properties.HasFlag(GattCharacteristicProperties.ExtendedProperties)) names.Add("Extended");
        if (properties.HasFlag(GattCharacteristicProperties.ReliableWrites)) names.Add("ReliableWrite");
        if (properties.HasFlag(GattCharacteristicProperties.WritableAuxiliaries)) names.Add("WritableAuxiliaries");
        return names.Count == 0 ? $"None (0x{(int)properties:X})" : $"{string.Join(" | ", names)} (0x{(int)properties:X})";
    }

    private static bool SupportsWrite(GattCharacteristic? characteristic, GattCharacteristicProperties property) =>
        characteristic is not null && characteristic.CharacteristicProperties.HasFlag(property);

    private static string FormatTx(byte[] frame) => ConsoleTrace.Spaced(frame);
}
