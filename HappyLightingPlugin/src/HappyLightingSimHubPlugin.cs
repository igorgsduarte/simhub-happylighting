using GameReaderCommon;
using SimHub.Plugins;
using System.Windows;
using System.Windows.Media;

namespace HappyLightingPlugin;

[PluginDescription("Controls HappyLighting BLE lights from SimHub telemetry")]
[PluginAuthor("Igor")]
[PluginName("HappyLighting")]
public sealed class HappyLightingSimHubPlugin : IPlugin, IDataPlugin, ISettingPlugin, IWPFSettings, IWPFSettingsV2
{
    private readonly HappyLightingPluginLifecycle _lifecycle = new();

    public PluginManager PluginManager { get; set; } = null!;

    public ImageSource PictureIcon => CreateIcon();

    public string LeftMenuTitle => "HappyLighting";

    public PluginSettings Settings { get; private set; } = new();

    public void Init(PluginManager pluginManager)
    {
        PluginManager = pluginManager;
        Settings = this.ReadCommonSettings("GeneralSettings", () => new PluginSettings());
        _lifecycle.InitAsync(Settings, CancellationToken.None).GetAwaiter().GetResult();
        this.AddAction(
            actionName: "TestConnectionGreenBlink",
            actionStart: (a, b) => TestConnection());
        SimHub.Logging.Current.Info("HappyLighting plugin started");
    }

    public void DataUpdate(PluginManager pluginManager, ref GameData data)
    {
        var snapshot = BuildSnapshot(data);
        _lifecycle.OnTelemetrySnapshot(snapshot);
        _lifecycle.TickTelemetryWatchdog();
    }

    public void End(PluginManager pluginManager)
    {
        this.SaveCommonSettings("GeneralSettings", Settings);
        _lifecycle.Dispose();
        SimHub.Logging.Current.Info("HappyLighting plugin stopped");
    }

    public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
    {
        return new HappyLightingSettingsControl(this);
    }

    public void TestConnection()
    {
        _lifecycle.TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task TestConnectionAsync()
    {
        return _lifecycle.TestConnectionAsync(CancellationToken.None);
    }

    public Task SendDebugColorAsync(RgbColor color, int maxBrightnessPercent)
    {
        return _lifecycle.SendDebugColorAsync(color, maxBrightnessPercent, CancellationToken.None);
    }

    public Task PlayEffectTestAsync(EffectTestKind effect)
    {
        return _lifecycle.PlayEffectTestAsync(effect, CancellationToken.None);
    }

    public Task ConnectSavedDeviceAsync()
    {
        return _lifecycle.ConnectSavedDeviceAsync(CancellationToken.None);
    }

    public DeviceStatusSnapshot GetDeviceStatus()
    {
        return _lifecycle.GetDeviceStatus();
    }

    public void SaveSettings()
    {
        this.SaveCommonSettings("GeneralSettings", Settings);
    }

    private static ImageSource CreateIcon()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            Brushes.Transparent,
            null,
            new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(
            Brushes.White,
            null,
            new EllipseGeometry(new Point(12, 12), 8, 8)));
        group.Children.Add(new GeometryDrawing(
            Brushes.DeepSkyBlue,
            null,
            new EllipseGeometry(new Point(12, 12), 4, 4)));

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private TelemetrySnapshot BuildSnapshot(GameData data)
    {
        var current = data.NewData;
        if (current is null)
        {
            return new TelemetrySnapshot();
        }

        var flag = ResolveFlag(current);
        var fuel = current.Fuel > 0 ? current.Fuel : current.FuelRaw;

        return new TelemetrySnapshot
        {
            PitLane = current.IsInPitLane > 0 || current.IsInPit > 0,
            GameRunning = data.GameRunning,
            PitLimiter = current.PitLimiterOn > 0,
            FuelLiters = fuel,
            LowFuel = fuel > 0 && fuel <= Settings.LowFuelThresholdLiters,
            MarshalFlag = flag
        };
    }

    private static string? ResolveFlag(StatusDataBase data)
    {
        if (data.Flag_Black > 0) return "black";
        if (data.Flag_Checkered > 0) return "checkered";
        if (data.Flag_Yellow > 0) return "yellow";
        if (data.Flag_Blue > 0) return "blue";
        if (data.Flag_Green > 0) return "green";
        if (data.Flag_White > 0) return "white";
        return string.IsNullOrWhiteSpace(data.Flag_Name) ? null : data.Flag_Name;
    }
}
