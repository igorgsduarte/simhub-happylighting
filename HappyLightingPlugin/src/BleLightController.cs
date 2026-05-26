using Microsoft.Extensions.Logging;
using SimHub.Bluetooth;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace HappyLightingPlugin;

public sealed class BleLightController : IDisposable
{
    private static readonly Guid HappyLightingServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid[] PreferredWriteCharacteristicUuids =
    {
        Guid.Parse("0000fff3-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000ffe9-0000-1000-8000-00805f9b34fb")
    };

    private readonly ILogger _logger;
    private readonly HappyLightingProtocol _protocol;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderLoop;

    private PluginSettings _settings = new();
    private Func<byte[], CancellationToken, Task>? _writeCharacteristic;
    private BluetoothLEDevice? _bleDevice;
    private GattCharacteristic? _writeGattCharacteristic;
    private ulong? _connectedAddress;
    private DeviceConnectionState _connectionState = DeviceConnectionState.NotConfigured;
    private string _lastStatusMessage = "No device selected.";

    private LightFrame? _pendingFrame;
    private DateTimeOffset _pendingFrameAt;
    private LightFrame? _lastSent;

    private long _framesEnqueued;
    private long _framesSent;
    private long _framesCoalesced;
    private long _payloadWrites;
    private long _writeFailures;
    private long _queueLatencySamples;
    private long _telemetryLatencySamples;
    private double _sumQueueToWriteMs;
    private double _sumTelemetryToWriteMs;
    private DateTimeOffset _lastWriteUtc = DateTimeOffset.MinValue;
    private string _lastError = string.Empty;

    public BleLightController(ILogger logger, HappyLightingProtocol protocol)
    {
        _logger = logger;
        _protocol = protocol;
        _senderLoop = Task.Run(SenderLoopAsync);
    }

    public Task ConfigureAsync(PluginSettings settings)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(settings.BluetoothAddress))
        {
            _connectionState = DeviceConnectionState.NotConfigured;
            _lastStatusMessage = "No device selected.";
        }

        return Task.CompletedTask;
    }

    public void EnqueueFrame(LightFrame frame, DateTimeOffset telemetryAt)
    {
        Interlocked.Increment(ref _framesEnqueued);
        var hadPending = _pendingFrame.HasValue;
        _pendingFrame = frame;
        _pendingFrameAt = telemetryAt;
        if (hadPending)
            Interlocked.Increment(ref _framesCoalesced);
        _signal.Release();
    }

    public async Task SendFrameNowAsync(LightFrame frame, CancellationToken cancellationToken)
    {
        await SendFrameInternalAsync(frame, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task ConnectAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _connectionState = DeviceConnectionState.Connecting;
            _lastStatusMessage = "Connecting...";
            var target = await ResolveBluetoothDeviceAsync(bluetoothAddress, cancellationToken);

            _writeCharacteristic = null;
            _writeGattCharacteristic = null;
            _connectedAddress = null;
            _bleDevice?.Dispose();

            _bleDevice = await OpenBluetoothDeviceAsync(target, cancellationToken);
            if (_bleDevice is null)
                throw new InvalidOperationException("Windows could not open the selected BLE device.");

            _writeGattCharacteristic = await ResolveWriteCharacteristicAsync(_bleDevice, cancellationToken);
            _writeCharacteristic = WriteGattAsync;
            _connectedAddress = target.Address;
            _connectionState = DeviceConnectionState.Connected;
            _lastStatusMessage = "Connected.";
            _lastError = string.Empty;
        }
        catch (Exception ex)
        {
            InvalidateConnection("Connection failed: " + ex.Message);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return;

        if (_writeCharacteristic is not null && _connectedAddress.HasValue && _bleDevice is not null)
            return;

        await ConnectAsync(_settings.BluetoothAddress, cancellationToken);
    }

    public DeviceStatusSnapshot GetStatus() => new()
    {
        State = _connectionState,
        DeviceLabel = BuildDeviceLabel(),
        Message = _lastStatusMessage
    };

    public BleRuntimeStats GetRuntimeStats() => new(
        _connectionState,
        Interlocked.Read(ref _framesEnqueued),
        Interlocked.Read(ref _framesSent),
        Interlocked.Read(ref _framesCoalesced),
        Interlocked.Read(ref _payloadWrites),
        Interlocked.Read(ref _writeFailures),
        _queueLatencySamples == 0 ? 0 : _sumQueueToWriteMs / _queueLatencySamples,
        _telemetryLatencySamples == 0 ? 0 : _sumTelemetryToWriteMs / _telemetryLatencySamples,
        _lastWriteUtc,
        _lastError);

    private async Task SenderLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token);
                var frame = _pendingFrame;
                if (!frame.HasValue)
                    continue;

                var telemetryAt = _pendingFrameAt;
                _pendingFrame = null;

                if (_lastSent.HasValue && _lastSent.Value.Equals(frame.Value))
                {
                    await Task.Delay(Math.Max(5, _settings.BleSteadyRateMs), _cts.Token);
                    continue;
                }

                await SendFrameInternalAsync(frame.Value, telemetryAt, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeFailures);
                _lastError = ex.Message;
                InvalidateConnection("Send failed: " + ex.Message);
                await ReconnectWithBackoffAsync(_cts.Token);
            }
        }
    }

    private async Task SendFrameInternalAsync(LightFrame frame, DateTimeOffset telemetryAt, CancellationToken cancellationToken)
    {
        frame = ApplyGlobalMaxBrightness(frame);
        var writer = _writeCharacteristic;
        if (writer is null)
        {
            await EnsureConnectedAsync(cancellationToken);
            writer = _writeCharacteristic;
            if (writer is null)
                throw new InvalidOperationException("No BLE write characteristic is connected.");
        }

        var beforeWrite = DateTimeOffset.UtcNow;
        foreach (var payload in _protocol.BuildFrameVariants(frame))
        {
            await WritePayloadWithReconnectAsync(payload, writer, cancellationToken);
            Interlocked.Increment(ref _payloadWrites);
            await Task.Delay(12, cancellationToken);
        }

        Interlocked.Increment(ref _framesSent);
        _lastSent = frame;
        _lastWriteUtc = DateTimeOffset.UtcNow;

        _sumQueueToWriteMs += (_lastWriteUtc - beforeWrite).TotalMilliseconds;
        _queueLatencySamples++;
        _sumTelemetryToWriteMs += (_lastWriteUtc - telemetryAt).TotalMilliseconds;
        _telemetryLatencySamples++;

        var changed = !_lastSent.HasValue || !_lastSent.Value.Equals(frame);
        var delayMs = changed ? Math.Max(5, _settings.BleBurstRateMs) : Math.Max(5, _settings.BleSteadyRateMs);
        await Task.Delay(delayMs, cancellationToken);
    }

    private async Task WritePayloadWithReconnectAsync(byte[] payload, Func<byte[], CancellationToken, Task> writer, CancellationToken cancellationToken)
    {
        try
        {
            await writer(payload, cancellationToken);
            _connectionState = DeviceConnectionState.Connected;
            _lastStatusMessage = "Connected.";
        }
        catch (Exception ex) when (IsClosedBleObject(ex))
        {
            InvalidateConnection("BLE connection closed. Reconnecting...");
            await ReconnectWithBackoffAsync(cancellationToken);
            if (_writeCharacteristic is null)
                throw;

            await _writeCharacteristic(payload, cancellationToken);
        }
    }

    private async Task ReconnectWithBackoffAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return;

        var attempt = 0;
        var random = new Random();
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await ConnectAsync(_settings.BluetoothAddress, cancellationToken);
                return;
            }
            catch
            {
                var baseMs = Math.Max(100, _settings.BleReconnectBackoffBaseMs);
                var wait = Math.Min(5000, baseMs * (int)Math.Pow(2, Math.Min(attempt, 5))) + random.Next(30, 250);
                await Task.Delay(wait, cancellationToken);
            }
        }
    }

    private LightFrame ApplyGlobalMaxBrightness(LightFrame frame)
    {
        if (!frame.IsOn) return frame;

        var maxByte = (byte)Compatibility.Clamp((int)Math.Round(Compatibility.Clamp(_settings.MaxBrightness, 0, 100) * 2.55), 0, 255);
        return frame with { Brightness = (byte)Math.Min(frame.Brightness, maxByte) };
    }

    private async Task WriteGattAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var characteristic = _writeGattCharacteristic ?? throw new InvalidOperationException("No BLE characteristic.");
        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        var status = await characteristic.WriteValueAsync(writer.DetachBuffer(), characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse);
        cancellationToken.ThrowIfCancellationRequested();

        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"BLE write failed with status {status}.");
    }

    private async Task<GattCharacteristic> ResolveWriteCharacteristicAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        var preferredServiceResult = await device.GetGattServicesForUuidAsync(HappyLightingServiceUuid, BluetoothCacheMode.Uncached);
        if (preferredServiceResult.Status == GattCommunicationStatus.Success && preferredServiceResult.Services.Count > 0)
        {
            var characteristic = await ResolveWriteCharacteristicFromServicesAsync(preferredServiceResult.Services, cancellationToken);
            if (characteristic is not null) return characteristic;
        }

        var allServicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (allServicesResult.Status != GattCommunicationStatus.Success || allServicesResult.Services.Count == 0)
            throw new InvalidOperationException("No GATT services available.");

        return await ResolveWriteCharacteristicFromServicesAsync(allServicesResult.Services, cancellationToken)
            ?? throw new InvalidOperationException("No writable BLE characteristic was found.");
    }

    private async Task<GattCharacteristic?> ResolveWriteCharacteristicFromServicesAsync(IReadOnlyList<GattDeviceService> services, CancellationToken cancellationToken)
    {
        foreach (var characteristicUuid in PreferredWriteCharacteristicUuids)
        {
            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached);
                var characteristic = result.Status == GattCommunicationStatus.Success
                    ? result.Characteristics.FirstOrDefault(IsWritable)
                    : null;
                if (characteristic is not null) return characteristic;
            }
        }

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            var characteristic = result.Status == GattCommunicationStatus.Success
                ? result.Characteristics.FirstOrDefault(IsWritable)
                : null;
            if (characteristic is not null) return characteristic;
        }

        return null;
    }

    private async Task<ResolvedBluetoothDevice> ResolveBluetoothDeviceAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        if (TryParseBluetoothAddress(bluetoothAddress, out var directAddress))
            return new ResolvedBluetoothDevice(directAddress, IsWindowsBluetoothDeviceId(_settings.BluetoothDeviceId) ? _settings.BluetoothDeviceId : null);

        var devices = await DiscoverDevicesAsync(cancellationToken);
        var query = bluetoothAddress.Trim();
        var match = devices.FirstOrDefault(device => Contains(device.Name, query) || Contains(device.Id, query) || Contains(device.AddressDescription, query));
        if (match is null)
            throw new InvalidOperationException("Bluetooth device not found.");

        return new ResolvedBluetoothDevice(match.BluetoothAdress, IsWindowsBluetoothDeviceId(match.Id) ? match.Id : null);
    }

    private static async Task<BluetoothLEDevice?> OpenBluetoothDeviceAsync(ResolvedBluetoothDevice target, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(target.DeviceId))
        {
            var device = await BluetoothLEDevice.FromIdAsync(target.DeviceId);
            cancellationToken.ThrowIfCancellationRequested();
            if (device is not null && (device.BluetoothAddress == 0 || device.BluetoothAddress == target.Address))
                return device;
            device?.Dispose();
        }

        return await BluetoothLEDevice.FromBluetoothAddressAsync(target.Address);
    }

    public static async Task<IReadOnlyList<BTDevice>> DiscoverDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = new List<BTDevice>();
        using (var lightLister = new LightDeviceLister { UseHardScan = true })
        {
            lightLister.Start();
            await Task.Delay(3000, cancellationToken);
            devices.AddRange(lightLister.GetAllFoundDevices());
        }

        devices.AddRange(await DiscoverWindowsBluetoothLeDevicesAsync(cancellationToken));
        devices.AddRange(await DiscoverBluetoothAdvertisementsAsync(cancellationToken));

        return devices
            .GroupBy(device => device.BluetoothAdress)
            .Select(SelectBestDiscoveredDevice)
            .OrderBy(d => d.Name)
            .ToList();
    }

    private static BTDevice SelectBestDiscoveredDevice(IGrouping<ulong, BTDevice> group)
    {
        return group
            .OrderByDescending(device => IsWindowsBluetoothDeviceId(device.Id))
            .ThenByDescending(device => !string.IsNullOrWhiteSpace(device.Name))
            .ThenByDescending(device => !string.IsNullOrWhiteSpace(device.AddressDescription))
            .First();
    }

    private static async Task<IReadOnlyList<BTDevice>> DiscoverWindowsBluetoothLeDevicesAsync(CancellationToken cancellationToken)
    {
        var found = new List<BTDevice>();
        var selector = BluetoothLEDevice.GetDeviceSelector();
        var infos = await DeviceInformation.FindAllAsync(selector);

        foreach (var info in infos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothLEDevice? bt = null;
            try
            {
                bt = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (bt is null || bt.BluetoothAddress == 0) continue;
                found.Add(new BTDevice { Name = string.IsNullOrWhiteSpace(info.Name) ? bt.Name : info.Name, Id = info.Id, BluetoothAdress = bt.BluetoothAddress });
            }
            catch { }
            finally { bt?.Dispose(); }
        }

        return found;
    }

    private static async Task<IReadOnlyList<BTDevice>> DiscoverBluetoothAdvertisementsAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<ulong, BTDevice>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            if (args.BluetoothAddress == 0 || found.ContainsKey(args.BluetoothAddress)) return;
            found[args.BluetoothAddress] = new BTDevice
            {
                Name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName) ? $"BLE {args.BluetoothAddress:X12}" : args.Advertisement.LocalName,
                Id = args.BluetoothAddress.ToString("X12"),
                BluetoothAdress = args.BluetoothAddress
            };
        };

        try
        {
            watcher.Start();
            await Task.Delay(6000, cancellationToken);
        }
        finally
        {
            watcher.Stop();
        }

        return found.Values.ToList();
    }

    private static bool TryParseBluetoothAddress(string bluetoothAddress, out ulong address)
    {
        var hex = bluetoothAddress.Replace(":", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex.Substring(2);
        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static bool Contains(string? value, string? query)
        => !string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(query) && value!.IndexOf(query!, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsWindowsBluetoothDeviceId(string? id)
    {
        return !string.IsNullOrWhiteSpace(id) &&
            id!.IndexOf("BluetoothLE", StringComparison.OrdinalIgnoreCase) >= 0 &&
            id.IndexOf("#", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWritable(GattCharacteristic characteristic)
        => characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) || characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

    private void InvalidateConnection(string message)
    {
        _writeCharacteristic = null;
        _writeGattCharacteristic = null;
        _connectedAddress = null;
        _bleDevice?.Dispose();
        _bleDevice = null;
        _connectionState = DeviceConnectionState.Error;
        _lastStatusMessage = message;
        _lastError = message;
    }

    private string BuildDeviceLabel()
    {
        if (!string.IsNullOrWhiteSpace(_settings.BluetoothDeviceName)) return _settings.BluetoothDeviceName;
        if (!string.IsNullOrWhiteSpace(_settings.BluetoothAddress)) return _settings.BluetoothAddress;
        return "No device";
    }

    private static bool IsClosedBleObject(Exception ex)
    {
        return ex is ObjectDisposedException || ex.HResult == unchecked((int)0x80000013) || (ex.Message?.IndexOf("object has been closed", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private sealed record ResolvedBluetoothDevice(ulong Address, string? DeviceId);

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();
        try { _senderLoop.GetAwaiter().GetResult(); } catch { }
        _signal.Dispose();
        _connectLock.Dispose();
        _cts.Dispose();
        _bleDevice?.Dispose();
    }
}
