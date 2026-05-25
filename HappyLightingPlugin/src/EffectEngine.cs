namespace HappyLightingPlugin;

public sealed class EffectEngine
{
    private static readonly TimeSpan OneSecondBlinkInterval = TimeSpan.FromSeconds(1);
    private readonly BrightnessProfile _brightness;
    private int _tick;

    public EffectEngine(BrightnessProfile brightness) => _brightness = brightness;

    public (LightFrame Frame, string EffectName, EffectPriority Priority) Compute(TelemetrySnapshot telemetry, PluginSettings settings)
    {
        _tick++;

        if (!telemetry.GameRunning && settings.EnableGameNotRunningEffect)
            return (new LightFrame(settings.GameNotRunningColor.R, settings.GameNotRunningColor.G, settings.GameNotRunningColor.B, _brightness.ResolveIdleBrightness(settings), true), "GameNotRunning", EffectPriority.GameNotRunning);

        var baseBrightness = _brightness.ResolveBrightness(settings, telemetry);

        if (settings.EnableCriticalFlags && IsCriticalFlag(telemetry.MarshalFlag, settings, out var criticalFrame, baseBrightness))
            return (criticalFrame, $"Critical:{telemetry.MarshalFlag}", EffectPriority.Critical);

        if (settings.EnableMarshalFlags && IsMarshalFlag(telemetry.MarshalFlag, settings, out var marshalFrame, baseBrightness))
            return (marshalFrame, $"Marshal:{telemetry.MarshalFlag}", EffectPriority.Marshal);

        if (settings.EnablePitLaneEffects && (telemetry.PitLane || telemetry.PitLimiter))
            return (Blink(settings.PitLaneColor, baseBrightness, OneSecondBlinkInterval), "PitLaneLimiter", EffectPriority.PitLane);

        if (settings.EnableLowFuelEffects && (telemetry.LowFuel || telemetry.FuelLiters <= settings.LowFuelThresholdLiters))
            return (Pulse(settings.LowFuelColor, baseBrightness), "LowFuelPulse", EffectPriority.LowFuel);

        if (settings.EnableNightLightEffects && settings.AutoNightMode && (telemetry.HeadlightsOn || telemetry.NightSession))
            return (Solid(settings.NightLightColor, baseBrightness), "NightWarmWhite", EffectPriority.NightLight);

        if (settings.EnableIdleAmbientEffects)
            return (new LightFrame(settings.IdleAmbientColor.R, settings.IdleAmbientColor.G, settings.IdleAmbientColor.B, _brightness.ResolveIdleBrightness(settings), true), "IdleAmbient", EffectPriority.IdleAmbient);

        return (LightFrame.Off, "AllEffectsDisabled", EffectPriority.IdleAmbient);
    }

    private bool IsCriticalFlag(string? flag, PluginSettings settings, out LightFrame frame, byte brightness)
    {
        frame = LightFrame.Off;
        if (string.Equals(flag, "black", StringComparison.OrdinalIgnoreCase))
        {
            frame = Blink(settings.BlackFlagColor, brightness, OneSecondBlinkInterval);
            return true;
        }
        if (string.Equals(flag, "checkered", StringComparison.OrdinalIgnoreCase))
        {
            frame = Blink(settings.CheckeredColor, brightness, OneSecondBlinkInterval);
            return true;
        }
        return false;
    }

    private bool IsMarshalFlag(string? flag, PluginSettings settings, out LightFrame frame, byte brightness)
    {
        frame = LightFrame.Off;
        switch (flag?.ToLowerInvariant())
        {
            case "yellow": frame = Blink(settings.YellowFlagColor, brightness, OneSecondBlinkInterval); return true;
            case "blue": frame = Blink(settings.BlueFlagColor, brightness, OneSecondBlinkInterval); return true;
            case "green": frame = Blink(settings.GreenFlagColor, brightness, OneSecondBlinkInterval); return true;
            case "white": frame = Blink(settings.WhiteFlagColor, brightness, OneSecondBlinkInterval); return true;
            default: return false;
        }
    }

    private LightFrame Solid(RgbColor color, byte brightness) => new(color.R, color.G, color.B, brightness, true);

    private LightFrame Blink(RgbColor color, byte brightness, TimeSpan interval)
        => ((DateTimeOffset.UtcNow.Ticks / interval.Ticks) % 2 == 0) ? Solid(color, brightness) : LightFrame.Off;

    private LightFrame Pulse(RgbColor color, byte maxBrightness)
    {
        var phase = (_tick % 20) / 20.0;
        var scale = 0.5 + (Math.Sin(phase * 2 * Math.PI) * 0.5);
        var brightness = (byte)Compatibility.Clamp((int)(maxBrightness * scale), 0, 255);
        return Solid(color, brightness);
    }
}
