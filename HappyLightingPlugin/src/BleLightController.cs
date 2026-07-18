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
    private static readonly Dictionary<ulong, Guid> KnownWriteCharacteristicByAddress = new();
    private static readonly object KnownCharacteristicLock = new();

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

            _writeGattCharacteristic = await ResolveWithAccessRetryAsync(_bleDevice, target, cancellationToken);
            _writeCharacteristic = WriteGattAsync;
            _connectedAddress = target.Address;
            lock (KnownCharacteristicLock)
            {
                KnownWriteCharacteristicByAddress[target.Address] = _writeGattCharacteristic.Uuid;
            }
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

    private async Task<GattCharacteristic> ResolveWithAccessRetryAsync(BluetoothLEDevice device, ResolvedBluetoothDevice target, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveWriteCharacteristicAsync(device, target.Address, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.IndexOf("AccessDenied", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Some HappyLighting variants expose FFF0 as AccessDenied on first attempt.
            // Reopen with address-only and retry once after a short delay.
            _logger.LogWarning("GATT AccessDenied on first resolve; retrying with address-only reopen");
            await Task.Delay(250, cancellationToken);

            device.Dispose();
            var reopened = await BluetoothLEDevice.FromBluetoothAddressAsync(target.Address);
            cancellationToken.ThrowIfCancellationRequested();
            if (reopened is null)
                throw;

            _bleDevice = reopened;
            return await ResolveWriteCharacteristicAsync(reopened, target.Address, cancellationToken);
        }
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return;

        if (_writeCharacteristic is not null && _writeGattCharacteristic is not null && _connectedAddress.HasValue && _bleDevice is not null)
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

    public async Task ShutdownAsync(CancellationToken cancellationToken)
        => await DisconnectCoreAsync(cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
        => await DisconnectCoreAsync(cancellationToken);

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _writeCharacteristic = null;
            _writeGattCharacteristic = null;
            _connectedAddress = null;
            _pendingFrame = null;
            _lastSent = null;
            _bleDevice?.Dispose();
            _bleDevice = null;
            _connectionState = DeviceConnectionState.Disconnected;
            _lastStatusMessage = "Disconnected.";
        }
        finally
        {
            _connectLock.Release();
        }
    }

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
        var previousFrame = _lastSent;
        var writer = _writeCharacteristic;
        if (writer is null || _writeGattCharacteristic is null)
        {
            await EnsureConnectedAsync(cancellationToken);
            writer = _writeCharacteristic;
            if (writer is null || _writeGattCharacteristic is null)
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

        var changed = !previousFrame.HasValue || !previousFrame.Value.Equals(frame);
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

    private async Task<GattCharacteristic> ResolveWriteCharacteristicAsync(BluetoothLEDevice device, ulong address, CancellationToken cancellationToken)
    {
        // Fast path 1: try last known characteristic UUID for this address using cached mode.
        Guid knownCharacteristicUuid;
        lock (KnownCharacteristicLock)
        {
            KnownWriteCharacteristicByAddress.TryGetValue(address, out knownCharacteristicUuid);
        }
        if (knownCharacteristicUuid != Guid.Empty)
        {
            var knownCached = await TryResolveCharacteristicByUuidAsync(device, knownCharacteristicUuid, BluetoothCacheMode.Cached, cancellationToken);
            if (knownCached is not null)
                return knownCached;
        }

        // Fast path 2: preferred service/characteristics in cached mode.
        var preferredServiceResult = await device.GetGattServicesForUuidAsync(HappyLightingServiceUuid, BluetoothCacheMode.Cached);
        if (preferredServiceResult.Status == GattCommunicationStatus.Success && preferredServiceResult.Services.Count > 0)
        {
            var characteristic = await ResolveWriteCharacteristicFromServicesAsync(preferredServiceResult.Services, BluetoothCacheMode.Cached, cancellationToken);
            if (characteristic is not null) return characteristic;
        }

        // Fast path 3: all services with cached mode.
        var allServicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Cached);
        if (allServicesResult.Status == GattCommunicationStatus.Success && allServicesResult.Services.Count > 0)
        {
            var cachedCharacteristic = await ResolveWriteCharacteristicFromServicesAsync(allServicesResult.Services, BluetoothCacheMode.Cached, cancellationToken);
            if (cachedCharacteristic is not null)
                return cachedCharacteristic;
        }

        // Slow path fallback: uncached full discovery.
        preferredServiceResult = await device.GetGattServicesForUuidAsync(HappyLightingServiceUuid, BluetoothCacheMode.Uncached);
        if (preferredServiceResult.Status == GattCommunicationStatus.Success && preferredServiceResult.Services.Count > 0)
        {
            var uncachedPreferred = await ResolveWriteCharacteristicFromServicesAsync(preferredServiceResult.Services, BluetoothCacheMode.Uncached, cancellationToken);
            if (uncachedPreferred is not null)
                return uncachedPreferred;
        }

        allServicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (allServicesResult.Status != GattCommunicationStatus.Success || allServicesResult.Services.Count == 0)
            throw new InvalidOperationException("No GATT services available.");

        var finalCharacteristic = await ResolveWriteCharacteristicFromServicesAsync(allServicesResult.Services, BluetoothCacheMode.Uncached, cancellationToken);
        if (finalCharacteristic is not null)
            return finalCharacteristic;

        var gattMap = await BuildGattMapSummaryAsync(device, cancellationToken);
        throw new InvalidOperationException("No writable BLE characteristic was found. GATT map: " + gattMap);
    }

    private async Task<GattCharacteristic?> ResolveWriteCharacteristicFromServicesAsync(IReadOnlyList<GattDeviceService> services, BluetoothCacheMode cacheMode, CancellationToken cancellationToken)
    {
        var preferredOrder = PreferredWriteCharacteristicUuids
            .Select((uuid, index) => new { uuid, index })
            .ToDictionary(item => item.uuid, item => item.index);
        GattCharacteristic? firstWritable = null;
        GattCharacteristic? bestPreferred = null;
        var bestPreferredOrder = int.MaxValue;

        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await service.GetCharacteristicsAsync(cacheMode);
            if (result.Status != GattCommunicationStatus.Success)
                continue;

            foreach (var characteristic in result.Characteristics)
            {
                if (!IsWritable(characteristic))
                    continue;

                firstWritable ??= characteristic;
                if (!preferredOrder.TryGetValue(characteristic.Uuid, out var order) || order >= bestPreferredOrder)
                    continue;

                bestPreferred = characteristic;
                bestPreferredOrder = order;
                if (bestPreferredOrder == 0)
                    return bestPreferred;
            }
        }

        return bestPreferred ?? firstWritable;
    }

    private async Task<GattCharacteristic?> TryResolveCharacteristicByUuidAsync(BluetoothLEDevice device, Guid characteristicUuid, BluetoothCacheMode cacheMode, CancellationToken cancellationToken)
    {
        var servicesResult = await device.GetGattServicesAsync(cacheMode);
        if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            return null;

        foreach (var service in servicesResult.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid, cacheMode);
            var characteristic = result.Status == GattCommunicationStatus.Success
                ? result.Characteristics.FirstOrDefault(IsWritable)
                : null;
            if (characteristic is not null)
                return characteristic;
        }

        return null;
    }

    private async Task<string> BuildGattMapSummaryAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        try
        {
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                return $"services-status={result.Status}";

            var parts = new List<string>();
            foreach (var service in result.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (characteristicsResult.Status != GattCommunicationStatus.Success)
                {
                    parts.Add($"{ShortUuid(service.Uuid)}:[status={characteristicsResult.Status}]");
                    continue;
                }

                var characteristicParts = characteristicsResult.Characteristics
                    .Select(c => $"{ShortUuid(c.Uuid)}({c.CharacteristicProperties})");
                parts.Add($"{ShortUuid(service.Uuid)}:[{string.Join(",", characteristicParts)}]");
            }

            return string.Join(" | ", parts);
        }
        catch (Exception ex)
        {
            return "gatt-map-failed: " + ex.Message;
        }
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
