using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HappyLightingPlugin;

// Replace BaseSimHubPlugin with real SimHub SDK base class in production package.
public sealed class HappyLightingPluginLifecycle : IDisposable
{
    private readonly ILogger _logger;
    private readonly TelemetryReader _telemetryReader;
    private readonly BrightnessProfile _brightnessProfile;
    private readonly EffectEngine _effectEngine;
    private readonly BleLightController _bleController;

    private PluginSettings _settings = new();
    private DateTimeOffset _lastTelemetry = DateTimeOffset.MinValue;

    public HappyLightingPluginLifecycle(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger("HappyLightingPlugin");
        _telemetryReader = new TelemetryReader(_logger);
        _brightnessProfile = new BrightnessProfile();
        _effectEngine = new EffectEngine(_brightnessProfile);
        _bleController = new BleLightController(_logger, new HappyLightingProtocol());
    }

    public async Task InitAsync(PluginSettings settings, CancellationToken ct)
    {
        _settings = settings;
        _settings.EnableLiveDebugColorSend = false;
        _settings.EnableIdleAmbientEffects = false;
        await _bleController.ConfigureAsync(settings);
        _ = ConnectSavedDeviceAsync(CancellationToken.None);
    }

    public Task ConnectSavedDeviceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await _bleController.ConfigureAsync(_settings);
                await _bleController.EnsureConnectedAsync(ct);
                await SendGameNotRunningEffectIfEnabledAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial BLE connection failed; the plugin will retry when sending frames");
            }
        }, ct);
    }

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before running the connection test.");

        await _bleController.ConnectAsync(_settings.BluetoothAddress, ct);
        await _bleController.TestBlinkVariantsAsync(ct);
        await SendGameNotRunningEffectIfEnabledAsync(ct);
    }

    public async Task SendDebugColorAsync(RgbColor color, int maxBrightnessPercent, CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before sending a debug color.");

        if (!await _bleController.IsConnectedToAsync(_settings.BluetoothAddress, ct))
            await _bleController.ConnectAsync(_settings.BluetoothAddress, ct);

        var brightness = (byte)Compatibility.Clamp((int)(Compatibility.Clamp(maxBrightnessPercent, 0, 100) * 2.55), 0, 255);
        await _bleController.SendFrameNowAsync(new LightFrame(color.R, color.G, color.B, brightness, true), ct);
    }

    public async Task PlayEffectTestAsync(EffectTestKind effect, CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before testing an effect.");

        if (!await _bleController.IsConnectedToAsync(_settings.BluetoothAddress, ct))
            await _bleController.ConnectAsync(_settings.BluetoothAddress, ct);

        var testSettings = BuildEffectTestSettings(effect);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (frame, _, _) = _effectEngine.Compute(BuildEffectTestSnapshot(effect), testSettings);
            await _bleController.SendFrameNowAsync(frame, ct);
            await Task.Delay(250, ct);
        }

        await _bleController.SendFrameNowAsync(LightFrame.Off, ct);
    }

    public DeviceStatusSnapshot GetDeviceStatus() => _bleController.GetStatus();

    // Must be called by SimHub DataUpdate callback. Non-blocking by design.
    public void OnDataUpdate(ITelemetrySource source)
    {
        var snapshot = _telemetryReader.Read(source, _settings);
        OnTelemetrySnapshot(snapshot);
    }

    public void OnTelemetrySnapshot(TelemetrySnapshot snapshot)
    {
        _lastTelemetry = snapshot.Timestamp;
        if (_settings.EnableLiveDebugColorSend)
        {
            _logger.LogInformation("Telemetry effects skipped because protocol debug live send is enabled");
            return;
        }

        var (frame, effect, priority) = _effectEngine.Compute(snapshot, _settings);

        _logger.LogInformation("Active effect={Effect} priority={Priority}", effect, priority);
        _bleController.EnqueueFrame(frame);
    }

    public void TickTelemetryWatchdog()
    {
        if (_settings.EnableLiveDebugColorSend)
            return;

        var stale = DateTimeOffset.UtcNow - _lastTelemetry > TimeSpan.FromSeconds(2);
        if (!stale) return;

        var fallback = _settings.KeepIdleOnTelemetryLoss
            ? new LightFrame(20, 20, 20, _brightnessProfile.ResolveIdleBrightness(_settings), true)
            : LightFrame.Off;

        _logger.LogWarning("Telemetry stale. Applying safe fallback (keepIdle={KeepIdle})", _settings.KeepIdleOnTelemetryLoss);
        _bleController.EnqueueFrame(fallback);
    }

    public void TestRed() => _bleController.EnqueueFrame(new LightFrame(255, 0, 0, 255, true));
    public void TestGreen() => _bleController.EnqueueFrame(new LightFrame(0, 255, 0, 255, true));
    public void TestBlue() => _bleController.EnqueueFrame(new LightFrame(0, 0, 255, 255, true));
    public void TestWhite() => _bleController.EnqueueFrame(new LightFrame(255, 255, 255, 255, true));
    public void TestOff() => _bleController.EnqueueFrame(LightFrame.Off);

    public void Dispose() => _bleController.Dispose();

    private PluginSettings BuildEffectTestSettings(EffectTestKind effect)
    {
        var settings = CloneSettings(_settings);
        settings.EnableCriticalFlags = effect == EffectTestKind.CriticalFlags;
        settings.EnableMarshalFlags = effect == EffectTestKind.MarshalFlags;
        settings.EnablePitLaneEffects = effect == EffectTestKind.PitLaneLimiter;
        settings.EnableLowFuelEffects = effect == EffectTestKind.LowFuel;
        settings.EnableNightLightEffects = effect == EffectTestKind.NightLight;
        settings.EnableIdleAmbientEffects = false;
        settings.EnableGameNotRunningEffect = effect == EffectTestKind.GameNotRunning;
        return settings;
    }

    private static TelemetrySnapshot BuildEffectTestSnapshot(EffectTestKind effect)
    {
        return effect switch
        {
            EffectTestKind.CriticalFlags => new TelemetrySnapshot { GameRunning = true, MarshalFlag = "black" },
            EffectTestKind.MarshalFlags => new TelemetrySnapshot { GameRunning = true, MarshalFlag = "yellow" },
            EffectTestKind.PitLaneLimiter => new TelemetrySnapshot { GameRunning = true, PitLimiter = true },
            EffectTestKind.LowFuel => new TelemetrySnapshot { GameRunning = true, LowFuel = true, FuelLiters = 1 },
            EffectTestKind.NightLight => new TelemetrySnapshot { GameRunning = true, HeadlightsOn = true, NightSession = true },
            EffectTestKind.GameNotRunning => new TelemetrySnapshot { GameRunning = false },
            _ => new TelemetrySnapshot { GameRunning = true }
        };
    }

    private async Task SendGameNotRunningEffectIfEnabledAsync(CancellationToken ct)
    {
        if (!_settings.EnableGameNotRunningEffect)
            return;

        var (frame, _, _) = _effectEngine.Compute(new TelemetrySnapshot { GameRunning = false }, _settings);
        await _bleController.SendFrameNowAsync(frame, ct);
    }

    private static PluginSettings CloneSettings(PluginSettings source)
    {
        return new PluginSettings
        {
            BluetoothAddress = source.BluetoothAddress,
            BluetoothDeviceId = source.BluetoothDeviceId,
            BluetoothDeviceName = source.BluetoothDeviceName,
            BluetoothAddressDescription = source.BluetoothAddressDescription,
            EnableCriticalFlags = source.EnableCriticalFlags,
            EnableMarshalFlags = source.EnableMarshalFlags,
            EnablePitLaneEffects = source.EnablePitLaneEffects,
            EnableLowFuelEffects = source.EnableLowFuelEffects,
            EnableNightLightEffects = source.EnableNightLightEffects,
            EnableIdleAmbientEffects = source.EnableIdleAmbientEffects,
            EnableGameNotRunningEffect = source.EnableGameNotRunningEffect,
            PitLaneColor = source.PitLaneColor,
            LowFuelColor = source.LowFuelColor,
            NightLightColor = source.NightLightColor,
            IdleAmbientColor = source.IdleAmbientColor,
            GameNotRunningColor = source.GameNotRunningColor,
            DebugColor = source.DebugColor,
            MaxBrightness = source.MaxBrightness,
            EnableLiveDebugColorSend = source.EnableLiveDebugColorSend,
            YellowFlagColor = source.YellowFlagColor,
            BlueFlagColor = source.BlueFlagColor,
            GreenFlagColor = source.GreenFlagColor,
            WhiteFlagColor = source.WhiteFlagColor,
            BlackFlagColor = source.BlackFlagColor,
            CheckeredColor = source.CheckeredColor,
            DayBrightness = source.DayBrightness,
            NightBrightness = source.NightBrightness,
            IdleBrightness = source.IdleBrightness,
            BleRateLimitMs = source.BleRateLimitMs,
            LowFuelThresholdLiters = source.LowFuelThresholdLiters,
            AutoNightMode = source.AutoNightMode,
            KeepIdleOnTelemetryLoss = source.KeepIdleOnTelemetryLoss,
            EnableDetailedTelemetryLogs = source.EnableDetailedTelemetryLogs
        };
    }
}
