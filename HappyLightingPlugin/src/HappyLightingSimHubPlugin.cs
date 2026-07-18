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
    private TelemetryPipeline? _telemetry;

    public PluginManager PluginManager { get; set; } = null!;
    public ImageSource PictureIcon => CreateIcon();
    public string LeftMenuTitle => "HappyLighting";
    public PluginSettings Settings { get; set; } = new();

    public void Init(PluginManager pluginManager)
    {
        PluginManager = pluginManager;
        _telemetry = new TelemetryPipeline(pluginManager);
        Settings = this.ReadCommonSettings("GeneralSettings", () => new PluginSettings());
        _lifecycle.InitAsync(Settings, CancellationToken.None).GetAwaiter().GetResult();
        this.AddAction("TestConnectionGreenBlink", (a, b) => TestConnection());
        this.AddAction("HappyLightingLedOn", (a, b) => _lifecycle.SetManualLedOnAsync(CancellationToken.None).GetAwaiter().GetResult());
        this.AddAction("HappyLightingLedOff", (a, b) => _lifecycle.SetManualLedOffAsync(CancellationToken.None).GetAwaiter().GetResult());
        this.AddAction("HappyLightingLedToggle", (a, b) => _lifecycle.ToggleManualLedAsync(CancellationToken.None).GetAwaiter().GetResult());
        this.AddAction("HappyLightingBrightnessUp", (a, b) => AdjustBrightnessAndRefresh(+5));
        this.AddAction("HappyLightingBrightnessDown", (a, b) => AdjustBrightnessAndRefresh(-5));
        this.AddAction("HappyLightingDisconnect", (a, b) => DisconnectDevice());
    }

    public void DataUpdate(PluginManager pluginManager, ref GameData data)
    {
        if (_telemetry is null)
            return;

        var snapshot = _telemetry.Build(data, Settings);
        _lifecycle.OnTelemetrySnapshot(snapshot);
        _lifecycle.TickTelemetryWatchdog();
    }

    public void End(PluginManager pluginManager)
    {
        this.SaveCommonSettings("GeneralSettings", Settings);
        _lifecycle.Dispose();
    }

    public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        => new HappyLightingSettingsControl(this);

    public void TestConnection() => _lifecycle.TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
    public Task TestConnectionAsync() => _lifecycle.TestConnectionAsync(CancellationToken.None);
    public Task DisconnectDeviceAsync() => _lifecycle.DisconnectDeviceAsync(CancellationToken.None);
    public void DisconnectDevice() => _lifecycle.DisconnectDeviceAsync(CancellationToken.None).GetAwaiter().GetResult();
    public Task PlayEffectTestAsync(EffectTestKind effect) => _lifecycle.PlayEffectTestAsync(effect, CancellationToken.None);
    public Task ConnectSavedDeviceAsync() => _lifecycle.ConnectSavedDeviceAsync(CancellationToken.None);
    public DeviceStatusSnapshot GetDeviceStatus() => _lifecycle.GetDeviceStatus();
    public RuntimeDiagnostics GetDiagnostics() => _lifecycle.GetDiagnostics();
    public void SaveSettings() => this.SaveCommonSettings("GeneralSettings", Settings);
    public void TriggerLedOn() => _lifecycle.SetManualLedOnAsync(CancellationToken.None).GetAwaiter().GetResult();
    public void TriggerLedOff() => _lifecycle.SetManualLedOffAsync(CancellationToken.None).GetAwaiter().GetResult();
    public void TriggerLedToggle() => _lifecycle.ToggleManualLedAsync(CancellationToken.None).GetAwaiter().GetResult();
    public void TriggerBrightnessUp() => AdjustBrightnessAndRefresh(+5);
    public void TriggerBrightnessDown() => AdjustBrightnessAndRefresh(-5);

    private void AdjustBrightnessAndRefresh(int delta)
    {
        _lifecycle.AdjustGlobalBrightness(delta);
        SaveSettings();
        _lifecycle.RefreshOutputNowAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ImageSource CreateIcon()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(12, 12), 8, 8)));
        group.Children.Add(new GeometryDrawing(Brushes.DeepSkyBlue, null, new EllipseGeometry(new Point(12, 12), 4, 4)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
