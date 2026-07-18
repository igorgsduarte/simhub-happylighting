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
    private readonly object _gameStateLock = new();

    private PluginSettings _settings = new();
    private DateTimeOffset _lastTelemetry = DateTimeOffset.MinValue;
    private int _isEffectTestRunning;
    private int _isConnectionTransitionRunning;
    private bool? _lastGameRunning;
    private TelemetrySnapshot _lastSnapshot = new();
    private EffectDecision _lastDecision = new(LightFrame.Off, "Off", EffectPriority.Off, "startup", DateTimeOffset.MinValue);
    private LightFrame _lastQueuedFrame = LightFrame.Off;
    private DateTimeOffset _lastQueueAt = DateTimeOffset.MinValue;
    private bool _lastManualLedOn = true;

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
        await _bleController.ConfigureAsync(settings);
    }

    public async Task ConnectSavedDeviceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            return;

        try
        {
            await _bleController.ConfigureAsync(_settings);
            await _bleController.EnsureConnectedAsync(ct);
            await PlayConnectionReadySignalAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE connection failed; retry on next running telemetry frame");
        }
    }

    public Task DisconnectDeviceAsync(CancellationToken ct)
        => _bleController.DisconnectAsync(ct);

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        await _bleController.ConfigureAsync(_settings);
        if (string.IsNullOrWhiteSpace(_settings.BluetoothAddress))
            throw new InvalidOperationException("Select a Bluetooth device before running the connection test.");

        try
        {
            await _bleController.ConnectAsync(_settings.BluetoothAddress, ct);
            await PlayConnectionReadySignalAsync(ct);
        }
        finally
        {
            await _bleController.DisconnectAsync(ct);
        }
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

        if (Volatile.Read(ref _isEffectTestRunning) == 1)
            return;

        var transition = DetectGameRunningTransition(snapshot.GameRunning);
        if (transition == GameRunningTransition.Started)
        {
            _ = RunGameStartedSequenceAsync(CancellationToken.None);
            return;
        }

        if (transition == GameRunningTransition.Stopped)
        {
            _ = RunGameStoppedSequenceAsync(CancellationToken.None);
            return;
        }

        if (!snapshot.GameRunning || Volatile.Read(ref _isConnectionTransitionRunning) == 1)
            return;

        var candidate = _effectEngine.Compute(snapshot, _settings);
        var decision = _stateMachine.Update(candidate, _settings, DateTimeOffset.UtcNow);
        _lastDecision = decision;

        if (_settings.EnableDetailedTelemetryLogs)
        {
            _logger.LogInformation("Effect={Effect} priority={Priority} reason={Reason} pit={Pit} lim={Lim} fuel={Fuel:0.00} flag={Flag}",
                decision.EffectId, decision.Priority, decision.Reason, snapshot.PitLane, snapshot.PitLimiter, snapshot.FuelLiters, snapshot.MarshalFlag);
        }

        var now = DateTimeOffset.UtcNow;
        var isAnimated = IsAnimatedEffect(decision.EffectId);
        var sameFrame = decision.Frame.Equals(_lastQueuedFrame);
        var minAnimatedCadenceMs = Math.Max(40, Math.Min(_settings.BleBurstRateMs * 2, 120));

        if (!isAnimated && sameFrame)
            return;

        if (isAnimated && sameFrame && (now - _lastQueueAt).TotalMilliseconds < minAnimatedCadenceMs)
            return;

        _bleController.EnqueueFrame(decision.Frame, snapshot.Timestamp);
        _lastQueuedFrame = decision.Frame;
        _lastQueueAt = now;
    }

    public void TickTelemetryWatchdog()
    {
        if (Volatile.Read(ref _isEffectTestRunning) == 1)
            return;

        if (!_lastSnapshot.GameRunning || Volatile.Read(ref _isConnectionTransitionRunning) == 1)
            return;

        var stale = DateTimeOffset.UtcNow - _lastTelemetry > TimeSpan.FromSeconds(2);
        if (!stale)
            return;

        var fallback = _settings.KeepIdleOnTelemetryLoss
            ? new LightFrame(255, 255, 255, _brightnessProfile.ResolveIdleBrightness(_settings), true)
            : LightFrame.Off;

        _bleController.EnqueueFrame(fallback, DateTimeOffset.UtcNow);
    }

    public Task SetManualLedOnAsync(CancellationToken ct)
    {
        _lastManualLedOn = true;
        return _bleController.SendFrameNowAsync(BuildManualOnFrame(), ct);
    }

    public Task SetManualLedOffAsync(CancellationToken ct)
    {
        _lastManualLedOn = false;
        return _bleController.SendFrameNowAsync(LightFrame.Off, ct);
    }

    public Task ToggleManualLedAsync(CancellationToken ct)
        => _lastManualLedOn ? SetManualLedOffAsync(ct) : SetManualLedOnAsync(ct);

    public int AdjustGlobalBrightness(int deltaPercent)
    {
        _settings.MaxBrightness = Compatibility.Clamp(_settings.MaxBrightness + deltaPercent, 1, 100);
        return _settings.MaxBrightness;
    }

    public async Task RefreshOutputNowAsync(CancellationToken ct)
    {
        var frame = _lastManualLedOn ? BuildManualOnFrame() : _lastDecision.Frame;
        await _bleController.SendFrameNowAsync(frame, ct);
    }

    public void Dispose()
    {
        _bleController.Dispose();
    }

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
        _lastDecision = decision;
        await _bleController.SendFrameNowAsync(decision.Frame, ct);
    }

    private GameRunningTransition DetectGameRunningTransition(bool gameRunning)
    {
        lock (_gameStateLock)
        {
            if (Volatile.Read(ref _isConnectionTransitionRunning) == 1)
                return GameRunningTransition.None;

            var previous = _lastGameRunning;
            _lastGameRunning = gameRunning;

            if (previous != true && gameRunning)
                return GameRunningTransition.Started;

            if (previous == true && !gameRunning)
                return GameRunningTransition.Stopped;

            return GameRunningTransition.None;
        }
    }

    private async Task RunGameStartedSequenceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _isConnectionTransitionRunning, 1, 0) != 0)
            return;

        try
        {
            await _bleController.ConfigureAsync(_settings);
            await _bleController.EnsureConnectedAsync(ct);
            await PlayConnectionReadySignalAsync(ct);
            ResetQueuedFrameTracking();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE game-start connection sequence failed; retry on next running telemetry frame");
            lock (_gameStateLock)
            {
                _lastGameRunning = false;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isConnectionTransitionRunning, 0);
        }
    }

    private async Task RunGameStoppedSequenceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _isConnectionTransitionRunning, 1, 0) != 0)
            return;

        try
        {
            await SendGameNotRunningEffectIfEnabledAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BLE game-stop idle effect failed; disconnecting anyway");
        }
        finally
        {
            try
            {
                await _bleController.DisconnectAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BLE runtime disconnect failed");
            }

            ResetQueuedFrameTracking();
            Interlocked.Exchange(ref _isConnectionTransitionRunning, 0);
        }
    }

    private async Task PlayConnectionReadySignalAsync(CancellationToken ct)
    {
        var green = new LightFrame(0, 255, 0, 255, true);
        for (var i = 0; i < 3; i++)
        {
            await _bleController.SendFrameNowAsync(green, ct);
            await Task.Delay(220, ct);
            await _bleController.SendFrameNowAsync(LightFrame.Off, ct);
            await Task.Delay(220, ct);
        }

        await _bleController.SendFrameNowAsync(green, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    private void ResetQueuedFrameTracking()
    {
        _lastQueuedFrame = LightFrame.Off;
        _lastQueueAt = DateTimeOffset.MinValue;
    }

    private static PluginSettings CloneSettings(PluginSettings source)
    {
        return new PluginSettings
        {
            BluetoothAddress = source.BluetoothAddress,
            BluetoothDeviceId = source.BluetoothDeviceId,
            BluetoothDeviceName = source.BluetoothDeviceName,
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
            MaxBrightness = source.MaxBrightness,
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
            Gamma = source.Gamma
        };
    }

    private LightFrame BuildManualOnFrame()
    {
        var brightness = (byte)Compatibility.Clamp((int)Math.Round(_settings.MaxBrightness * 2.55), 0, 255);
        return new LightFrame(255, 255, 255, brightness, true);
    }

    private static bool IsAnimatedEffect(string effectId)
    {
        return effectId.Equals("PitLaneLimiter", StringComparison.OrdinalIgnoreCase) ||
            effectId.Equals("CriticalFlags", StringComparison.OrdinalIgnoreCase) ||
            effectId.Equals("MarshalFlags", StringComparison.OrdinalIgnoreCase);
    }

    private enum GameRunningTransition
    {
        None,
        Started,
        Stopped
    }
}
