namespace HappyLightingPlugin;

public sealed class BrightnessProfile
{
    public byte ResolveBrightness(PluginSettings settings, TelemetrySnapshot telemetry)
    {
        var value = settings.DayBrightness;
        if (settings.AutoNightMode && (telemetry.HeadlightsOn || telemetry.NightSession))
            value = settings.NightBrightness;

        return ToByte(value, settings);
    }

    public byte ResolveIdleBrightness(PluginSettings settings)
    {
        return ToByte(settings.IdleBrightness, settings);
    }

    private static byte ToByte(int percent, PluginSettings settings)
    {
        var normalized = Compatibility.Clamp(percent, 0, 100) / 100.0;
        if (settings.EnableGammaCorrection)
        {
            var gamma = Math.Max(0.1, settings.Gamma);
            normalized = Math.Pow(normalized, 1.0 / gamma);
        }

        return (byte)Compatibility.Clamp((int)Math.Round(normalized * 255), 0, 255);
    }
}
