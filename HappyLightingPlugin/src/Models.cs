namespace HappyLightingPlugin;

public readonly record struct LightFrame(byte R, byte G, byte B, byte Brightness, bool IsOn)
{
    public static readonly LightFrame Off = new(0, 0, 0, 0, false);
}

public readonly record struct EffectDecision(
    LightFrame Frame,
    string EffectId,
    EffectPriority Priority,
    string Reason,
    DateTimeOffset HoldUntil);

public readonly record struct BleRuntimeStats(
    DeviceConnectionState ConnectionState,
    long FramesEnqueued,
    long FramesSent,
    long FramesCoalesced,
    long PayloadWrites,
    long WriteFailures,
    double AvgQueueToWriteMs,
    double AvgTelemetryToWriteMs,
    DateTimeOffset LastWriteUtc,
    string LastError);

public struct RgbColor
{
    public RgbColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

public enum EffectPriority
{
    GameNotRunning = 0,
    Critical = 1,
    Marshal = 2,
    PitLane = 3,
    LowFuel = 4,
    NightLight = 5,
    IdleAmbient = 6,
    Off = 7
}

public enum EffectTestKind
{
    CriticalFlags,
    MarshalFlags,
    PitLaneLimiter,
    LowFuel,
    NightLight,
    GameNotRunning
}

public enum DeviceConnectionState
{
    NotConfigured,
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed class DeviceStatusSnapshot
{
    public DeviceConnectionState State { get; init; }
    public string DeviceLabel { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class TelemetrySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool GameRunning { get; init; }
    public bool PitLane { get; init; }
    public bool PitLimiter { get; init; }
    public bool HeadlightsOn { get; init; }
    public bool NightSession { get; init; }
    public bool LowFuel { get; init; }
    public double FuelLiters { get; init; }
    public string? MarshalFlag { get; init; }
}

public sealed class RuntimeDiagnostics
{
    public TelemetrySnapshot LastTelemetry { get; init; } = new();
    public EffectDecision LastDecision { get; init; } = new(LightFrame.Off, "Off", EffectPriority.Off, "NoData", DateTimeOffset.MinValue);
    public BleRuntimeStats BleStats { get; init; }
}

public sealed class PluginSettings
{
    public string BluetoothAddress { get; set; } = string.Empty;
    public string BluetoothDeviceId { get; set; } = string.Empty;
    public string BluetoothDeviceName { get; set; } = string.Empty;

    public bool EnableCriticalFlags { get; set; } = true;
    public bool EnableMarshalFlags { get; set; } = true;
    public bool EnablePitLaneEffects { get; set; } = true;
    public bool EnableLowFuelEffects { get; set; } = true;
    public bool EnableNightLightEffects { get; set; } = true;
    public bool EnableIdleAmbientEffects { get; set; } = false;
    public bool EnableGameNotRunningEffect { get; set; } = true;

    public RgbColor PitLaneColor { get; set; } = new(255, 200, 0);
    public RgbColor LowFuelColor { get; set; } = new(255, 0, 0);
    public RgbColor NightLightColor { get; set; } = new(255, 180, 120);
    public RgbColor IdleAmbientColor { get; set; } = new(70, 90, 120);
    public RgbColor GameNotRunningColor { get; set; } = new(255, 255, 255);

    public int MaxBrightness { get; set; } = 100;

    public RgbColor YellowFlagColor { get; set; } = new(255, 180, 0);
    public RgbColor BlueFlagColor { get; set; } = new(0, 80, 255);
    public RgbColor GreenFlagColor { get; set; } = new(0, 255, 0);
    public RgbColor WhiteFlagColor { get; set; } = new(255, 255, 255);
    public RgbColor BlackFlagColor { get; set; } = new(255, 0, 0);
    public RgbColor CheckeredColor { get; set; } = new(255, 255, 255);

    public int DayBrightness { get; set; } = 85;
    public int NightBrightness { get; set; } = 30;
    public int IdleBrightness { get; set; } = 8;

    public int BlinkOnMs { get; set; } = 300;
    public int BlinkOffMs { get; set; } = 300;
    public int EffectDebounceMs { get; set; } = 120;
    public int EffectMinActiveMs { get; set; } = 300;

    public int BleBurstRateMs { get; set; } = 15;
    public int BleSteadyRateMs { get; set; } = 45;
    public int BleReconnectBackoffBaseMs { get; set; } = 300;

    public double LowFuelThresholdLiters { get; set; } = 5.0;
    public bool AutoNightMode { get; set; } = true;
    public bool KeepIdleOnTelemetryLoss { get; set; }
    public bool EnableDetailedTelemetryLogs { get; set; } = true;

    public bool EnableGammaCorrection { get; set; } = true;
    public double Gamma { get; set; } = 2.0;
}
