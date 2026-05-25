using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SimHub.Bluetooth;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
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
    private readonly ConcurrentQueue<LightFrame> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderLoop;

    private LightFrame? _lastSent;
    private Func<byte[], CancellationToken, Task>? _writeCharacteristic;
    private BluetoothLEDevice? _bleDevice;
    private GattCharacteristic? _writeGattCharacteristic;
    private ulong? _connectedAddress;
    private PluginSettings _settings = new();
    private DeviceConnectionState _connectionState = DeviceConnectionState.NotConfigured;
    private string _lastStatusMessage = "No device selected.";

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

    public async Task ConnectAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Connecting to BLE device at {Address}", bluetoothAddress);
            _connectionState = DeviceConnectionState.Connecting;
            _lastStatusMessage = "Connecting...";
            var target = await ResolveBluetoothDeviceAsync(bluetoothAddress, cancellationToken);
            _writeCharacteristic = null;
            _writeGattCharacteristic = null;
            _connectedAddress = null;
            _bleDevice?.Dispose();
            _bleDevice = await OpenBluetoothDeviceAsync(target, cancellationToken);

            if (_bleDevice is null)
                throw new InvalidOperationException("Windows could not open the selected BLE device. Remove/re-pair it in Windows Bluetooth settings, close any phone lighting app, then click Discover devices again.");

            _writeGattCharacteristic = await ResolveWriteCharacteristicAsync(_bleDevice, cancellationToken);
            _writeCharacteristic = async (payload, ct) =>
            {
                await WriteGattAsync(payload, ct);
            };
            _connectedAddress = target.Address;
            _connectionState = DeviceConnectionState.Connected;
            _lastStatusMessage = "Connected.";

            _logger.LogInformation("BLE connected to {Name} ({Address:X12}) using write characteristic {CharacteristicUuid}", _bleDevice.Name, target.Address, _writeGattCharacteristic.Uuid);
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

    public async Task<bool> IsConnectedToAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        if (_writeCharacteristic is null || !_connectedAddress.HasValue || _bleDevice is null)
            return false;

        var target = await ResolveBluetoothDeviceAsync(bluetoothAddress, cancellationToken);
        return _connectedAddress.Value == target.Address;
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return;

        if (await IsConnectedToAsync(_settings.BluetoothAddress, cancellationToken))
            return;

        await ConnectAsync(_settings.BluetoothAddress, cancellationToken);
    }

    public DeviceStatusSnapshot GetStatus()
    {
        var state = _connectionState;
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
        {
            state = DeviceConnectionState.NotConfigured;
            _lastStatusMessage = "No device selected.";
        }
        else if (state == DeviceConnectionState.Connected && (_writeCharacteristic is null || _bleDevice is null))
        {
            state = DeviceConnectionState.Disconnected;
            _connectionState = state;
            _lastStatusMessage = "Disconnected.";
        }

        return new DeviceStatusSnapshot
        {
            State = state,
            DeviceLabel = BuildDeviceLabel(),
            Message = _lastStatusMessage
        };
    }

    public void EnqueueFrame(LightFrame frame)
    {
        _queue.Enqueue(frame);
        _signal.Release();
    }

    public async Task BlinkAsync(RgbColor color, byte brightness, int count, TimeSpan onDuration, TimeSpan offDuration, CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueFrame(new LightFrame(color.R, color.G, color.B, brightness, true));
            await Task.Delay(onDuration, cancellationToken);
            EnqueueFrame(LightFrame.Off);
            await Task.Delay(offDuration, cancellationToken);
        }
    }

    public async Task TestBlinkVariantsAsync(CancellationToken cancellationToken)
    {
        var onFrame = ApplyGlobalMaxBrightness(new LightFrame(0, 255, 0, 255, true));
        var offFrame = ApplyGlobalMaxBrightness(new LightFrame(0, 0, 0, 255, true));

        for (var i = 0; i < 3; i++)
        {
            foreach (var payload in _protocol.BuildFrameVariants(onFrame))
            {
                await WritePayloadWithReconnectAsync(payload, cancellationToken);
                await Task.Delay(50, cancellationToken);
            }

            await Task.Delay(450, cancellationToken);

            foreach (var payload in _protocol.BuildFrameVariants(offFrame))
            {
                await WritePayloadWithReconnectAsync(payload, cancellationToken);
                await Task.Delay(50, cancellationToken);
            }

            await Task.Delay(300, cancellationToken);
        }
    }

    public async Task SendFrameNowAsync(LightFrame frame, CancellationToken cancellationToken)
    {
        frame = ApplyGlobalMaxBrightness(frame);
        foreach (var payload in _protocol.BuildFrameVariants(frame))
        {
            await WritePayloadWithReconnectAsync(payload, cancellationToken);
            await Task.Delay(20, cancellationToken);
        }

        _lastSent = frame;
        _logger.LogInformation("Debug frame sent R={R} G={G} B={B} Br={Br} On={On}", frame.R, frame.G, frame.B, frame.Brightness, frame.IsOn);
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
                {
                    await ConnectAsync(_settings.BluetoothAddress, cancellationToken);
                    return;
                }

                _logger.LogWarning("Reconnect skipped: no Bluetooth address configured");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect failed, retrying in 2s");
                await Task.Delay(2000, cancellationToken);
            }
        }
    }

    private async Task SenderLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token);
                if (!_queue.TryDequeue(out var frame)) continue;
                if (_lastSent.HasValue && _lastSent.Value.Equals(frame)) continue;

                var writer = _writeCharacteristic;
                if (writer is null)
                {
                    await EnsureConnectedAsync(_cts.Token);
                    writer = _writeCharacteristic;
                    if (writer is null)
                    {
                        _logger.LogWarning("No BLE write characteristic, dropping frame");
                        continue;
                    }
                }

                frame = ApplyGlobalMaxBrightness(frame);
                foreach (var payload in _protocol.BuildFrameVariants(frame))
                {
                    await SendFrameAsync(frame, payload, writer, _cts.Token);
                    await Task.Delay(20, _cts.Token);
                }
                _lastSent = frame;

                _logger.LogInformation("Frame sent R={R} G={G} B={B} Br={Br} On={On}", frame.R, frame.G, frame.B, frame.Brightness, frame.IsOn);

                await Task.Delay(Math.Max(10, _settings.BleRateLimitMs), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                InvalidateConnection("Send failed: " + ex.Message);
                _logger.LogError(ex, "BLE sender loop faulted, attempting reconnect");
                await ReconnectAsync(_cts.Token);
            }
        }
    }

    private async Task SendFrameAsync(LightFrame frame, byte[] payload, Func<byte[], CancellationToken, Task> fallbackWriter, CancellationToken cancellationToken)
    {
        await fallbackWriter(payload, cancellationToken);
    }

    private async Task WritePayloadWithReconnectAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);

        try
        {
            var writer = _writeCharacteristic;
            if (writer is null)
                throw new InvalidOperationException("No BLE write characteristic is connected.");

            await writer(payload, cancellationToken);
            _connectionState = DeviceConnectionState.Connected;
            _lastStatusMessage = "Connected.";
        }
        catch (Exception ex) when (IsClosedBleObject(ex))
        {
            _logger.LogWarning(ex, "BLE object was closed; reconnecting and retrying write once");
            InvalidateConnection("BLE connection was closed; reconnecting...");
            await EnsureConnectedAsync(cancellationToken);

            var writer = _writeCharacteristic;
            if (writer is null)
                throw new InvalidOperationException("No BLE write characteristic is connected after reconnect.");

            await writer(payload, cancellationToken);
            _connectionState = DeviceConnectionState.Connected;
            _lastStatusMessage = "Connected.";
        }
        catch (Exception ex)
        {
            InvalidateConnection("Send failed: " + ex.Message);
            throw;
        }
    }

    private LightFrame ApplyGlobalMaxBrightness(LightFrame frame)
    {
        if (!frame.IsOn)
            return frame;

        var globalScale = Compatibility.Clamp(_settings.MaxBrightness, 0, 100) / 100.0;
        var filteredBrightness = (byte)Compatibility.Clamp((int)Math.Round(frame.Brightness * globalScale), 0, 255);
        return new LightFrame(frame.R, frame.G, frame.B, filteredBrightness, frame.IsOn);
    }

    private async Task WriteGattAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var characteristic = _writeGattCharacteristic;
        if (characteristic is null)
            throw new InvalidOperationException("No BLE write characteristic is connected.");

        using var writer = new DataWriter();
        writer.WriteBytes(payload);
        var buffer = writer.DetachBuffer();

        var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        var status = await characteristic.WriteValueAsync(buffer, option);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("BLE GATT write [{Payload}] status={Status}", Compatibility.ToHexString(payload), status);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"BLE write failed with status {status}.");
    }

    private async Task<GattCharacteristic> ResolveWriteCharacteristicAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        var preferredServiceResult = await device.GetGattServicesForUuidAsync(HappyLightingServiceUuid, BluetoothCacheMode.Uncached);
        if (preferredServiceResult.Status == GattCommunicationStatus.Success && preferredServiceResult.Services.Count > 0)
        {
            var characteristic = await ResolveWriteCharacteristicFromServicesAsync(preferredServiceResult.Services, cancellationToken);
            if (characteristic is not null)
                return characteristic;
        }

        var allServicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (allServicesResult.Status != GattCommunicationStatus.Success || allServicesResult.Services.Count == 0)
        {
            throw new InvalidOperationException("Connected to the BLE device, but no GATT services were available.");
        }

        var fallbackCharacteristic = await ResolveWriteCharacteristicFromServicesAsync(allServicesResult.Services, cancellationToken);
        if (fallbackCharacteristic is not null)
            return fallbackCharacteristic;

        var services = string.Join(", ", allServicesResult.Services.Select(service => ShortUuid(service.Uuid)));
        throw new InvalidOperationException("Connected to the BLE device, but no writable BLE characteristic was found. Services: " + services);
    }

    private async Task<GattCharacteristic?> ResolveWriteCharacteristicFromServicesAsync(IReadOnlyList<GattDeviceService> services, CancellationToken cancellationToken)
    {
        foreach (var characteristicUuid in PreferredWriteCharacteristicUuids)
        {
            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached);
                if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
                    continue;

                var characteristic = result.Characteristics.FirstOrDefault(IsWritable);
                if (characteristic is not null)
                    return characteristic;
            }
        }

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success)
                continue;

            var characteristic = result.Characteristics.FirstOrDefault(IsWritable);
            if (characteristic is not null)
                return characteristic;
        }

        return null;
    }

    private async Task<ResolvedBluetoothDevice> ResolveBluetoothDeviceAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        if (TryParseBluetoothAddress(bluetoothAddress, out var directAddress))
        {
            var configuredDeviceId =
                string.Equals(_settings.BluetoothAddress, bluetoothAddress, StringComparison.OrdinalIgnoreCase) &&
                IsWindowsBluetoothDeviceId(_settings.BluetoothDeviceId)
                    ? _settings.BluetoothDeviceId
                    : null;

            return new ResolvedBluetoothDevice(directAddress, configuredDeviceId);
        }

        var query = bluetoothAddress.Trim();
        var devices = await DiscoverDevicesAsync(cancellationToken);
        var match = devices.FirstOrDefault(device =>
            Contains(device.Name, query) ||
            Contains(device.Id, query) ||
            Contains(device.AddressDescription, query) ||
            Contains(device.BluetoothAdress.ToString("X12"), query) ||
            Contains(query, device.Id) ||
            Contains(query, device.Name));

        if (match is null)
            throw new InvalidOperationException("Bluetooth device not found. Click Discover devices and select OA000B/HappyLighting from the list.");

        return new ResolvedBluetoothDevice(
            match.BluetoothAdress,
            IsWindowsBluetoothDeviceId(match.Id) ? match.Id : null);
    }

    private static async Task<BluetoothLEDevice?> OpenBluetoothDeviceAsync(ResolvedBluetoothDevice target, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(target.DeviceId))
        {
            var device = await BluetoothLEDevice.FromIdAsync(target.DeviceId);
            cancellationToken.ThrowIfCancellationRequested();
            if (device is not null)
            {
                if (device.BluetoothAddress == 0 || device.BluetoothAddress == target.Address)
                    return device;

                device.Dispose();
            }
        }

        var fallbackDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(target.Address);
        cancellationToken.ThrowIfCancellationRequested();
        return fallbackDevice;
    }

    public static async Task<IReadOnlyList<BTDevice>> DiscoverDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = new List<BTDevice>();

        using (var lightLister = new LightDeviceLister { UseHardScan = true })
        {
            lightLister.Start();
            await Task.Delay(4500, cancellationToken);
            devices.AddRange(lightLister.GetAllFoundDevices());
        }

        devices.AddRange(await DiscoverWindowsBluetoothLeDevicesAsync(cancellationToken));
        devices.AddRange(await DiscoverBluetoothAdvertisementsAsync(cancellationToken));

        return devices
            .GroupBy(device => device.BluetoothAdress)
            .Select(SelectBestDiscoveredDevice)
            .OrderBy(device => device.Name)
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
        var deviceInfos = await DeviceInformation.FindAllAsync(selector);

        foreach (var deviceInfo in deviceInfos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BluetoothLEDevice? bluetoothDevice = null;
            try
            {
                bluetoothDevice = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                if (bluetoothDevice is null || bluetoothDevice.BluetoothAddress == 0)
                    continue;

                found.Add(new BTDevice
                {
                    Name = string.IsNullOrWhiteSpace(deviceInfo.Name) ? bluetoothDevice.Name : deviceInfo.Name,
                    Id = deviceInfo.Id,
                    BluetoothAdress = bluetoothDevice.BluetoothAddress
                });
            }
            catch
            {
                // Some BLE devices expose incomplete metadata and can fail while resolving.
            }
            finally
            {
                bluetoothDevice?.Dispose();
            }
        }

        return found;
    }

    private static async Task<IReadOnlyList<BTDevice>> DiscoverBluetoothAdvertisementsAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<ulong, BTDevice>();
        using var registration = cancellationToken.Register(() => { });

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            var address = args.BluetoothAddress;
            if (address == 0 || found.ContainsKey(address))
                return;

            var name = args.Advertisement.LocalName;
            found[address] = new BTDevice
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"BLE {address:X12}" : name,
                Id = address.ToString("X12"),
                BluetoothAdress = address
            };
        };

        try
        {
            watcher.Start();
            await Task.Delay(10000, cancellationToken);
        }
        finally
        {
            watcher.Stop();
        }

        return found.Values.ToList();
    }

    private static bool TryParseBluetoothAddress(string bluetoothAddress, out ulong address)
    {
        var hex = bluetoothAddress
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();

        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex.Substring(2);

        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static bool Contains(string? value, string? query)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query))
            return false;

        return value!.IndexOf(query!, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWindowsBluetoothDeviceId(string? id)
    {
        return !string.IsNullOrWhiteSpace(id) &&
            id!.IndexOf("BluetoothLE", StringComparison.OrdinalIgnoreCase) >= 0 &&
            id.IndexOf("#", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWritable(GattCharacteristic characteristic)
    {
        return characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
    }

    private static string ShortUuid(Guid uuid)
    {
        var value = uuid.ToString();
        return value.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)
            ? value.Substring(4, 4).ToUpperInvariant()
            : value;
    }

    private void InvalidateConnection(string message)
    {
        _writeCharacteristic = null;
        _writeGattCharacteristic = null;
        _connectedAddress = null;
        _bleDevice?.Dispose();
        _bleDevice = null;
        _connectionState = DeviceConnectionState.Error;
        _lastStatusMessage = message;
    }

    private string BuildDeviceLabel()
    {
        if (!string.IsNullOrWhiteSpace(_settings.BluetoothDeviceName))
            return _settings.BluetoothDeviceName;

        if (!string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return _settings.BluetoothAddress;

        return "No device";
    }

    private static bool IsClosedBleObject(Exception ex)
    {
        return ex is ObjectDisposedException ||
            ex.HResult == unchecked((int)0x80000013) ||
            (ex.Message?.IndexOf("object has been closed", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private sealed record ResolvedBluetoothDevice(ulong Address, string? DeviceId);

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();
        try
        {
            _senderLoop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _signal.Dispose();
        _connectLock.Dispose();
        _cts.Dispose();
        _bleDevice?.Dispose();
        _connectedAddress = null;
    }
}
