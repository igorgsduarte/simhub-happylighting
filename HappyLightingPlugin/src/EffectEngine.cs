using System.Diagnostics;

namespace HappyLightingPlugin;

public sealed class EffectEngine
{
    private readonly BrightnessProfile _brightness;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public EffectEngine(BrightnessProfile brightness) => _brightness = brightness;

    public EffectDecision Compute(TelemetrySnapshot telemetry, PluginSettings settings)
    {
        if (!telemetry.GameRunning && settings.EnableGameNotRunningEffect)
            return Decision("GameNotRunning", EffectPriority.GameNotRunning, "game not running", Solid(settings.GameNotRunningColor, _brightness.ResolveIdleBrightness(settings)));

        var baseBrightness = _brightness.ResolveBrightness(settings, telemetry);

        if (settings.EnableCriticalFlags && IsCriticalFlag(telemetry.MarshalFlag, settings, out var criticalFrame))
            return Decision("CriticalFlags", EffectPriority.Critical, telemetry.MarshalFlag ?? "critical", criticalFrame with { Brightness = baseBrightness });

        if (settings.EnableMarshalFlags && IsMarshalFlag(telemetry.MarshalFlag, settings, out var marshalFrame))
            return Decision("MarshalFlags", EffectPriority.Marshal, telemetry.MarshalFlag ?? "marshal", marshalFrame with { Brightness = baseBrightness });

        if (settings.EnablePitLaneEffects && (telemetry.PitLane || telemetry.PitLimiter))
            return Decision("PitLaneLimiter", EffectPriority.PitLane, telemetry.PitLimiter ? "pit limiter" : "pit lane", Blink(settings.PitLaneColor, baseBrightness, settings.BlinkOnMs, settings.BlinkOffMs));

        if (settings.EnableLowFuelEffects && telemetry.LowFuel)
            return Decision("LowFuel", EffectPriority.LowFuel, $"fuel={telemetry.FuelLiters:0.0}", Solid(settings.LowFuelColor, baseBrightness));

        if (settings.EnableNightLightEffects && settings.AutoNightMode && (telemetry.HeadlightsOn || telemetry.NightSession))
            return Decision("NightLight", EffectPriority.NightLight, "night/headlights", Solid(settings.NightLightColor, baseBrightness));

        if (settings.EnableIdleAmbientEffects)
            return Decision("IdleAmbient", EffectPriority.IdleAmbient, "idle ambient", Solid(settings.IdleAmbientColor, _brightness.ResolveIdleBrightness(settings)));

        return Decision("Off", EffectPriority.Off, "all effects disabled", LightFrame.Off);
    }

    private EffectDecision Decision(string id, EffectPriority priority, string reason, LightFrame frame)
        => new(frame, id, priority, reason, DateTimeOffset.MinValue);

    private bool IsCriticalFlag(string? flag, PluginSettings settings, out LightFrame frame)
    {
        frame = LightFrame.Off;
        if (string.Equals(flag, "black", StringComparison.OrdinalIgnoreCase))
        {
            frame = Blink(settings.BlackFlagColor, 255, settings.BlinkOnMs, settings.BlinkOffMs);
            return true;
        }

        if (string.Equals(flag, "checkered", StringComparison.OrdinalIgnoreCase))
        {
            frame = Blink(settings.CheckeredColor, 255, settings.BlinkOnMs, settings.BlinkOffMs);
            return true;
        }

        return false;
    }

    private bool IsMarshalFlag(string? flag, PluginSettings settings, out LightFrame frame)
    {
        frame = LightFrame.Off;
        switch (flag?.ToLowerInvariant())
        {
            case "yellow": frame = Blink(settings.YellowFlagColor, 255, settings.BlinkOnMs, settings.BlinkOffMs); return true;
            case "green": frame = Blink(settings.GreenFlagColor, 255, settings.BlinkOnMs, settings.BlinkOffMs); return true;
            case "white": frame = Blink(settings.WhiteFlagColor, 255, settings.BlinkOnMs, settings.BlinkOffMs); return true;
            case "blue": frame = Blink(settings.BlueFlagColor, 255, settings.BlinkOnMs, settings.BlinkOffMs); return true;
            default: return false;
        }
    }

    private LightFrame Solid(RgbColor color, byte brightness) => new(color.R, color.G, color.B, brightness, true);

    private LightFrame Blink(RgbColor color, byte brightness, int onMs, int offMs)
    {
        var on = Math.Max(10, onMs);
        var off = Math.Max(10, offMs);
        var period = on + off;
        var phaseMs = _clock.ElapsedMilliseconds % period;
        return phaseMs < on ? Solid(color, brightness) : LightFrame.Off;
    }
}
