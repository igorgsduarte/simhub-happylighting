namespace HappyLightingPlugin.UI;

public sealed class SettingsViewModel
{
    public PluginSettings Settings { get; }
    private readonly HappyLightingPluginLifecycle _plugin;

    public SettingsViewModel(PluginSettings settings, HappyLightingPluginLifecycle plugin)
    {
        Settings = settings;
        _plugin = plugin;
    }

    // Effect switches
    public bool EnableCriticalFlags { get => Settings.EnableCriticalFlags; set => Settings.EnableCriticalFlags = value; }
    public bool EnableMarshalFlags { get => Settings.EnableMarshalFlags; set => Settings.EnableMarshalFlags = value; }
    public bool EnablePitLaneEffects { get => Settings.EnablePitLaneEffects; set => Settings.EnablePitLaneEffects = value; }
    public bool EnableLowFuelEffects { get => Settings.EnableLowFuelEffects; set => Settings.EnableLowFuelEffects = value; }
    public bool EnableNightLightEffects { get => Settings.EnableNightLightEffects; set => Settings.EnableNightLightEffects = value; }
    public bool EnableIdleAmbientEffects { get => Settings.EnableIdleAmbientEffects; set => Settings.EnableIdleAmbientEffects = value; }

    // Color configurators (hex style #RRGGBB)
    public string PitLaneColorHex { get => ToHex(Settings.PitLaneColor); set => Settings.PitLaneColor = ParseHex(value, Settings.PitLaneColor); }
    public string LowFuelColorHex { get => ToHex(Settings.LowFuelColor); set => Settings.LowFuelColor = ParseHex(value, Settings.LowFuelColor); }
    public string NightLightColorHex { get => ToHex(Settings.NightLightColor); set => Settings.NightLightColor = ParseHex(value, Settings.NightLightColor); }
    public string IdleAmbientColorHex { get => ToHex(Settings.IdleAmbientColor); set => Settings.IdleAmbientColor = ParseHex(value, Settings.IdleAmbientColor); }

    public void TestRed() => _plugin.TestRed();
    public void TestGreen() => _plugin.TestGreen();
    public void TestBlue() => _plugin.TestBlue();
    public void TestWhite() => _plugin.TestWhite();
    public void TestOff() => _plugin.TestOff();

    public static string ToHex(RgbColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static RgbColor ParseHex(string? value, RgbColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var hex = value!.Trim().TrimStart('#');
        if (hex.Length != 6) return fallback;

        try
        {
            var r = Convert.ToByte(hex.Substring(0, 2), 16);
            var g = Convert.ToByte(hex.Substring(2, 2), 16);
            var b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new RgbColor(r, g, b);
        }
        catch
        {
            return fallback;
        }
    }
}
