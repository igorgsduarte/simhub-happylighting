namespace HappyLightingPlugin;

public sealed class EffectEngine
{
    private readonly BrightnessProfile _brightness;
    private int _tick;

    public EffectEngine(BrightnessProfile brightness) => _brightness = brightness;

    public (LightFrame Frame, string EffectName, EffectPriority Priority) Compute(TelemetrySnapshot telemetry, PluginSettings settings)
    {
        _tick++;
        var baseBrightness = _brightness.ResolveBrightness(settings, telemetry);

        if (settings.EnableCriticalFlags && IsCriticalFlag(telemetry.MarshalFlag, settings, out var criticalFrame, baseBrightness))
            return (criticalFrame, $"Critical:{telemetry.MarshalFlag}", EffectPriority.Critical);

        if (settings.EnableMarshalFlags && IsMarshalFlag(telemetry.MarshalFlag, settings, out var marshalFrame, baseBrightness))
            return (marshalFrame, $"Marshal:{telemetry.MarshalFlag}", EffectPriority.Marshal);

        if (settings.EnablePitLaneEffects && (telemetry.PitLane || telemetry.PitLimiter))
            return (Blink(settings.PitLaneColor, baseBrightness, 2), "PitLaneLimiter", EffectPriority.PitLane);

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
            frame = Blink(settings.BlackFlagColor, brightness, 1);
            return true;
        }
        if (string.Equals(flag, "checkered", StringComparison.OrdinalIgnoreCase))
        {
            frame = ((_tick / 2) % 2 == 0) ? Solid(settings.CheckeredColor, brightness) : LightFrame.Off;
            return true;
        }
        return false;
    }

    private bool IsMarshalFlag(string? flag, PluginSettings settings, out LightFrame frame, byte brightness)
    {
        frame = LightFrame.Off;
        switch (flag?.ToLowerInvariant())
        {
            case "yellow": frame = Pulse(settings.YellowFlagColor, brightness); return true;
            case "blue": frame = Pulse(settings.BlueFlagColor, brightness); return true;
            case "green": frame = Blink(settings.GreenFlagColor, brightness, 2); return true;
            case "white": frame = Solid(settings.WhiteFlagColor, brightness); return true;
            default: return false;
        }
    }

    private LightFrame Solid(RgbColor color, byte brightness) => new(color.R, color.G, color.B, brightness, true);

    private LightFrame Blink(RgbColor color, byte brightness, int speedDiv)
        => ((_tick / speedDiv) % 2 == 0) ? Solid(color, brightness) : LightFrame.Off;

    private LightFrame Pulse(RgbColor color, byte maxBrightness)
    {
        var phase = (_tick % 20) / 20.0;
        var scale = 0.5 + (Math.Sin(phase * 2 * Math.PI) * 0.5);
        var brightness = (byte)Math.Clamp((int)(maxBrightness * scale), 0, 255);
        return Solid(color, brightness);
    }
}
