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

        value = Math.Clamp(value, 0, 100);
        return (byte)Math.Round(value * 2.55);
    }

    public byte ResolveIdleBrightness(PluginSettings settings)
    {
        var value = Math.Clamp(settings.IdleBrightness, 0, 100);
        return (byte)Math.Round(value * 2.55);
    }
}
