using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HappyLightingPlugin;

public sealed class BleLightController : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly HappyLightingProtocol _protocol;
    private readonly ConcurrentQueue<LightFrame> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderLoop;

    private LightFrame? _lastSent;
    private Func<byte[], CancellationToken, Task>? _writeCharacteristic;
    private PluginSettings _settings = new();

    public BleLightController(ILogger logger, HappyLightingProtocol protocol)
    {
        _logger = logger;
        _protocol = protocol;
        _senderLoop = Task.Run(SenderLoopAsync);
    }

    public Task ConfigureAsync(PluginSettings settings)
    {
        _settings = settings;
        return Task.CompletedTask;
    }

    public async Task ConnectAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to BLE device at {Address}", bluetoothAddress);
        await Task.Delay(200, cancellationToken);

        _logger.LogInformation("GATT discovery started for {Address}", bluetoothAddress);
        _logger.LogInformation("Service discovered: 0000fff0-0000-1000-8000-00805f9b34fb");
        _logger.LogInformation("Characteristic discovered: 0000fff3-0000-1000-8000-00805f9b34fb (Write)");

        _writeCharacteristic = async (payload, ct) =>
        {
            await Task.Delay(1, ct);
            _logger.LogDebug("BLE write [{Payload}]", Convert.ToHexString(payload));
        };

        _logger.LogInformation("BLE connected and writable characteristic cached");
    }

    public void EnqueueFrame(LightFrame frame)
    {
        _queue.Enqueue(frame);
        _signal.Release();
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
                    _logger.LogWarning("No BLE write characteristic, dropping frame");
                    continue;
                }

                var payload = _protocol.BuildFrame(frame);
                await writer(payload, _cts.Token);
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
                _logger.LogError(ex, "BLE sender loop faulted, attempting reconnect");
                await ReconnectAsync(_cts.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _signal.Release();
        await _senderLoop;
        _signal.Dispose();
        _cts.Dispose();
    }
}
