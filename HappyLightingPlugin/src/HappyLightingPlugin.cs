using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HappyLightingPlugin;

public sealed class HappyLightingPluginLifecycle : IDisposable
{
    private readonly ILogger _logger;
    private readonly BrightnessProfile _brightnessProfile;
    private readonly EffectEngine _effectEngine;
    private readonly EffectStateMachine _stateMachine;
    private readonly BleLightController _bleController;

    private PluginSettings _settings = new();
    private DateTimeOffset _lastTelemetry = DateTimeOffset.MinValue;
    private int _isEffectTestRunning;
    private TelemetrySnapshot _lastSnapshot = new();
    private EffectDecision _lastDecision = new(LightFrame.Off, "Off", EffectPriority.Off, "startup", DateTimeOffset.MinValue);

    public HappyLightingPluginLifecycle(ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger("HappyLightingPlugin");
        _brightnessProfile = new BrightnessProfile();
        _effectEngine = new EffectEngine(_brightnessProfile);
        _stateMachine = new EffectStateMachine();
        _bleController = new BleLightController(_logger, new HappyLightingProtocol());
    }

    public async Task InitAsync(PluginSettings settings, CancellationToken ct)
    {
        _settings = settings;
        _settings.EnableLiveDebugColorSend = false;
        await _bleController.ConfigureAsync(settings);
        _ = ConnectSavedDeviceAsync(CancellationToken.None);
    }

    public async Task ConnectSavedDeviceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return;

        try
        {
            await _bleController.ConfigureAsync(_settings);
            await _bleController.EnsureConnectedAsync(ct);
            await SendGameNotRunningEffectIfEnabledAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial BLE connection failed; retry on next frame");
        }
    }

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before running the connection test.");

        await _bleController.ConnectAsync(_settings.BluetoothAddress, ct);
        var green = new LightFrame(0, 255, 0, 255, true);
        for (var i = 0; i < 3; i++)
        {
            await _bleController.SendFrameNowAsync(green, ct);
            await Task.Delay(220, ct);
            await _bleController.SendFrameNowAsync(LightFrame.Off, ct);
            await Task.Delay(220, ct);
        }

        await SendGameNotRunningEffectIfEnabledAsync(ct);
    }

    public async Task SendDebugColorAsync(RgbColor color, int maxBrightnessPercent, CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before sending a debug color.");

        await _bleController.EnsureConnectedAsync(ct);
        var brightness = (byte)Compatibility.Clamp((int)(Compatibility.Clamp(maxBrightnessPercent, 0, 100) * 2.55), 0, 255);
        await _bleController.SendFrameNowAsync(new LightFrame(color.R, color.G, color.B, brightness, true), ct);
    }

    public async Task PlayEffectTestAsync(EffectTestKind effect, CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before testing an effect.");

        await _bleController.EnsureConnectedAsync(ct);
        Interlocked.Exchange(ref _isEffectTestRunning, 1);
        try
        {
            var testSettings = BuildEffectTestSettings(effect);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = _effectEngine.Compute(BuildEffectTestSnapshot(effect), testSettings);
                await _bleController.SendFrameNowAsync(candidate.Frame, ct);
                await Task.Delay(220, ct);
            }

            await _bleController.SendFrameNowAsync(LightFrame.Off, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _isEffectTestRunning, 0);
        }
    }

    public DeviceStatusSnapshot GetDeviceStatus() => _bleController.GetStatus();

    public RuntimeDiagnostics GetDiagnostics() => new()
    {
        LastTelemetry = _lastSnapshot,
        LastDecision = _lastDecision,
        BleStats = _bleController.GetRuntimeStats()
    };

    public void OnTelemetrySnapshot(TelemetrySnapshot snapshot)
    {
        _lastTelemetry = snapshot.Timestamp;
        _lastSnapshot = snapshot;

        if (_settings.EnableLiveDebugColorSend || Volatile.Read(ref _isEffectTestRunning) == 1)
            return;

        var candidate = _effectEngine.Compute(snapshot, _settings);
        var decision = _stateMachine.Update(candidate, _settings, DateTimeOffset.UtcNow);
        _lastDecision = decision;

        if (_settings.EnableDetailedTelemetryLogs)
        {
            _logger.LogInformation("Effect={Effect} priority={Priority} reason={Reason} pit={Pit} lim={Lim} fuel={Fuel:0.00} flag={Flag}",
                decision.EffectId, decision.Priority, decision.Reason, snapshot.PitLane, snapshot.PitLimiter, snapshot.FuelLiters, snapshot.MarshalFlag);
        }

        _bleController.EnqueueFrame(decision.Frame, snapshot.Timestamp);
    }

    public void TickTelemetryWatchdog()
    {
        if (_settings.EnableLiveDebugColorSend || Volatile.Read(ref _isEffectTestRunning) == 1)
            return;

        var stale = DateTimeOffset.UtcNow - _lastTelemetry > TimeSpan.FromSeconds(2);
        if (!stale)
            return;

        var fallback = _settings.KeepIdleOnTelemetryLoss
            ? new LightFrame(255, 255, 255, _brightnessProfile.ResolveIdleBrightness(_settings), true)
            : LightFrame.Off;

        _bleController.EnqueueFrame(fallback, DateTimeOffset.UtcNow);
    }

    public void TestRed() => _bleController.EnqueueFrame(new LightFrame(255, 0, 0, 255, true), DateTimeOffset.UtcNow);
    public void TestGreen() => _bleController.EnqueueFrame(new LightFrame(0, 255, 0, 255, true), DateTimeOffset.UtcNow);
    public void TestBlue() => _bleController.EnqueueFrame(new LightFrame(0, 0, 255, 255, true), DateTimeOffset.UtcNow);
    public void TestWhite() => _bleController.EnqueueFrame(new LightFrame(255, 255, 255, 255, true), DateTimeOffset.UtcNow);
    public void TestOff() => _bleController.EnqueueFrame(LightFrame.Off, DateTimeOffset.UtcNow);

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

        var decision = _effectEngine.Compute(new TelemetrySnapshot { GameRunning = false }, _settings);
        await _bleController.SendFrameNowAsync(decision.Frame, ct);
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
            BlinkOnMs = source.BlinkOnMs,
            BlinkOffMs = source.BlinkOffMs,
            EffectDebounceMs = source.EffectDebounceMs,
            EffectMinActiveMs = source.EffectMinActiveMs,
            BleBurstRateMs = source.BleBurstRateMs,
            BleSteadyRateMs = source.BleSteadyRateMs,
            BleReconnectBackoffBaseMs = source.BleReconnectBackoffBaseMs,
            LowFuelThresholdLiters = source.LowFuelThresholdLiters,
            AutoNightMode = source.AutoNightMode,
            KeepIdleOnTelemetryLoss = source.KeepIdleOnTelemetryLoss,
            EnableDetailedTelemetryLogs = source.EnableDetailedTelemetryLogs,
            EnableGammaCorrection = source.EnableGammaCorrection,
            Gamma = source.Gamma,
            EnableDiagnosticsPanel = source.EnableDiagnosticsPanel
        };
    }
}
