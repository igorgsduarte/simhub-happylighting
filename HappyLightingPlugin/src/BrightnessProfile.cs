namespace HappyLightingPlugin;

public sealed class BrightnessProfile
{
    public byte ResolveBrightness(PluginSettings settings, TelemetrySnapshot telemetry)
    {
        var value = settings.DayBrightness;
        if (settings.AutoNightMode && (telemetry.HeadlightsOn || telemetry.NightSession))
        {
            value = settings.NightBrightness;
        }

        return (byte)Math.Round(value * 2.55);
    }

    public byte ResolveIdleBrightness(PluginSettings settings)
    {
        var value = Compatibility.Clamp(settings.IdleBrightness, 0, 100);
        return (byte)Math.Round(value * 2.55);
    }

    public int ApplyGlobalMax(PluginSettings settings, int brightnessPercent)
    {
        var globalMax = Compatibility.Clamp(settings.MaxBrightness, 0, 100);
        return Math.Min(Compatibility.Clamp(brightnessPercent, 0, 100), globalMax);
    }
}
