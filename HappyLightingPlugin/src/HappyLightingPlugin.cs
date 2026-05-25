using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HappyLightingPlugin;

// Replace BaseSimHubPlugin with real SimHub SDK base class in production package.
public sealed class HappyLightingPluginLifecycle : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly TelemetryReader _telemetryReader;
    private readonly EffectEngine _effectEngine;
    private readonly BleLightController _bleController;

    private PluginSettings _settings = new();
    private DateTimeOffset _lastTelemetry = DateTimeOffset.MinValue;

    public HappyLightingPluginLifecycle(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger("HappyLightingPlugin");
        _telemetryReader = new TelemetryReader(_logger);
        _effectEngine = new EffectEngine(new BrightnessProfile());
        _bleController = new BleLightController(_logger, new HappyLightingProtocol());
    }

    public async Task InitAsync(PluginSettings settings, CancellationToken ct)
    {
        _settings = settings;
        await _bleController.ConfigureAsync(settings);
        if (!string.IsNullOrWhiteSpace(settings.BluetoothAddress))
            await _bleController.ConnectAsync(settings.BluetoothAddress, ct);
    }

    // Must be called by SimHub DataUpdate callback. Non-blocking by design.
    public void OnDataUpdate(ITelemetrySource source)
    {
        var snapshot = _telemetryReader.Read(source, _settings);
        _lastTelemetry = snapshot.Timestamp;
        var (frame, effect, priority) = _effectEngine.Compute(snapshot, _settings);

        _logger.LogInformation("Active effect={Effect} priority={Priority}", effect, priority);
        _bleController.EnqueueFrame(frame);
    }

    public void TickTelemetryWatchdog()
    {
        var stale = DateTimeOffset.UtcNow - _lastTelemetry > TimeSpan.FromSeconds(2);
        if (!stale) return;

        var fallback = _settings.KeepIdleOnTelemetryLoss
            ? new LightFrame(20, 20, 20, (byte)Math.Clamp((int)(_settings.IdleBrightness * 2.55), 0, 255), true)
            : LightFrame.Off;

        _logger.LogWarning("Telemetry stale. Applying safe fallback (keepIdle={KeepIdle})", _settings.KeepIdleOnTelemetryLoss);
        _bleController.EnqueueFrame(fallback);
    }

    public void TestRed() => _bleController.EnqueueFrame(new LightFrame(255, 0, 0, 255, true));
    public void TestGreen() => _bleController.EnqueueFrame(new LightFrame(0, 255, 0, 255, true));
    public void TestBlue() => _bleController.EnqueueFrame(new LightFrame(0, 0, 255, 255, true));
    public void TestWhite() => _bleController.EnqueueFrame(new LightFrame(255, 255, 255, 255, true));
    public void TestOff() => _bleController.EnqueueFrame(LightFrame.Off);

    public ValueTask DisposeAsync() => _bleController.DisposeAsync();
}
