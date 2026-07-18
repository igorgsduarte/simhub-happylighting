using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DrawingColor = System.Drawing.Color;
using SimHub.Bluetooth;
using SimHub.Plugins.DataPlugins.RGBDriverCommon.LedBehaviourEditors;
using SimHub.Plugins.Styles;

namespace HappyLightingPlugin;

public sealed class HappyLightingSettingsControl : UserControl
{
    private readonly HappyLightingSimHubPlugin _plugin;
    private TextBox _deviceText = null!;
    private ComboBox _devices = null!;
    private TextBlock _deviceStatus = null!;
    private TextBlock _status = null!;
    private TextBlock _diagnostics = null!;
    private DispatcherTimer? _timer;
    private bool _updatingDeviceText;

    public HappyLightingSettingsControl(HappyLightingSimHubPlugin plugin)
    {
        _plugin = plugin;
        var panel = new StackPanel { Margin = new Thickness(16), Orientation = Orientation.Vertical };
        panel.Children.Add(new SHSection { Title = "HappyLighting", ShowSeparator = true, Content = BuildContent() });
        Content = new ScrollViewer { Content = panel };
        Loaded += (_, _) => StartUpdates();
        Unloaded += (_, _) => _timer?.Stop();
    }

    private StackPanel BuildContent()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        _deviceStatus = new TextBlock { Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
        _status = new TextBlock { Margin = new Thickness(0, 0, 0, 12), FontWeight = FontWeights.SemiBold };
        panel.Children.Add(_deviceStatus);
        panel.Children.Add(_status);

        var tabs = new TabControl { Margin = new Thickness(0, 8, 0, 0) };

        var general = new StackPanel { Orientation = Orientation.Vertical };
        general.Children.Add(new SHSubSection { Title = "Conexao BLE", Content = BuildConnectionSection() });
        general.Children.Add(new SHSectionSeparator());
        general.Children.Add(new SHSubSection { Title = "Regras e Prioridade", Content = BuildRulesSection() });
        general.Children.Add(new SHSectionSeparator());
        general.Children.Add(new SHSubSection { Title = "Cores e Brilho", Content = BuildBrightnessAndColorsSection() });
        general.Children.Add(new SHSectionSeparator());
        general.Children.Add(new SHSubSection { Title = "Diagnostico", Content = BuildDiagnosticsSection() });

        tabs.Items.Add(new TabItem { Header = "General", Content = general });
        tabs.Items.Add(new TabItem { Header = "Triggers", Content = BuildTriggersSection() });
        panel.Children.Add(tabs);

        RefreshUi();
        return panel;
    }

    private StackPanel BuildConnectionSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new TextBlock { Text = "Dispositivo selecionado" });
        _deviceText = new TextBox { Text = _plugin.Settings.BluetoothAddress, Margin = new Thickness(0, 4, 0, 12), MinWidth = 520 };
        _deviceText.TextChanged += (_, _) => UpdateSetting(() =>
        {
            if (_updatingDeviceText) return;
            _plugin.Settings.BluetoothAddress = _deviceText.Text.Trim();
        });
        panel.Children.Add(_deviceText);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var discover = new SHButtonSecondary { Content = "Discover devices", Margin = new Thickness(0, 0, 8, 0) };
        discover.Click += (_, _) => DiscoverDevices(discover);
        _devices = new ComboBox { MinWidth = 520, DisplayMemberPath = nameof(BluetoothDeviceOption.DisplayName) };
        _devices.SelectionChanged += (_, _) =>
        {
            if (_devices.SelectedItem is not BluetoothDeviceOption selected) return;
            UpdateSetting(() =>
            {
                _plugin.Settings.BluetoothAddress = selected.AddressHex;
                _plugin.Settings.BluetoothDeviceId = selected.Id;
                _plugin.Settings.BluetoothDeviceName = selected.Name;
            });
            _updatingDeviceText = true;
            _deviceText.Text = selected.AddressHex;
            _updatingDeviceText = false;
            _ = _plugin.ConnectSavedDeviceAsync();
        };
        row.Children.Add(discover);
        row.Children.Add(_devices);
        panel.Children.Add(row);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var test = new SHButtonPrimary { Content = "Test connection", Margin = new Thickness(0, 0, 8, 0) };
        test.Click += (_, _) => RunConnectionTest(test);
        var disconnect = new SHButtonSecondary { Content = "Disconnect" };
        disconnect.Click += (_, _) => RunDisconnect(disconnect);
        controls.Children.Add(test);
        controls.Children.Add(disconnect);
        panel.Children.Add(controls);

        panel.Children.Add(MakeIntSlider("Burst rate (ms)", 5, 80, () => _plugin.Settings.BleBurstRateMs, v => _plugin.Settings.BleBurstRateMs = v));
        panel.Children.Add(MakeIntSlider("Steady rate (ms)", 10, 120, () => _plugin.Settings.BleSteadyRateMs, v => _plugin.Settings.BleSteadyRateMs = v));
        panel.Children.Add(MakeIntSlider("Reconnect base (ms)", 100, 1000, () => _plugin.Settings.BleReconnectBackoffBaseMs, v => _plugin.Settings.BleReconnectBackoffBaseMs = v));
        return panel;
    }

    private StackPanel BuildRulesSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(MakeIntSlider("Blink on (ms)", 80, 1000, () => _plugin.Settings.BlinkOnMs, v => _plugin.Settings.BlinkOnMs = v));
        panel.Children.Add(MakeIntSlider("Blink off (ms)", 80, 1000, () => _plugin.Settings.BlinkOffMs, v => _plugin.Settings.BlinkOffMs = v));
        panel.Children.Add(MakeIntSlider("Effect debounce (ms)", 0, 500, () => _plugin.Settings.EffectDebounceMs, v => _plugin.Settings.EffectDebounceMs = v));
        panel.Children.Add(MakeIntSlider("Min active (ms)", 0, 1000, () => _plugin.Settings.EffectMinActiveMs, v => _plugin.Settings.EffectMinActiveMs = v));
        return panel;
    }

    private StackPanel BuildBrightnessAndColorsSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(MakeIntSlider("Global max brightness (%)", 1, 100, () => _plugin.Settings.MaxBrightness, v => _plugin.Settings.MaxBrightness = v));
        panel.Children.Add(MakeIntSlider("Day brightness (%)", 1, 100, () => _plugin.Settings.DayBrightness, v => _plugin.Settings.DayBrightness = v));
        panel.Children.Add(MakeIntSlider("Night brightness (%)", 1, 100, () => _plugin.Settings.NightBrightness, v => _plugin.Settings.NightBrightness = v));
        panel.Children.Add(MakeIntSlider("Idle brightness (%)", 1, 100, () => _plugin.Settings.IdleBrightness, v => _plugin.Settings.IdleBrightness = v));

        panel.Children.Add(BuildColorEditor("Game not running (white)", () => _plugin.Settings.GameNotRunningColor, c => _plugin.Settings.GameNotRunningColor = c));
        panel.Children.Add(BuildColorEditor("Yellow flag", () => _plugin.Settings.YellowFlagColor, c => _plugin.Settings.YellowFlagColor = c));
        panel.Children.Add(BuildColorEditor("Green flag", () => _plugin.Settings.GreenFlagColor, c => _plugin.Settings.GreenFlagColor = c));
        panel.Children.Add(BuildColorEditor("White flag", () => _plugin.Settings.WhiteFlagColor, c => _plugin.Settings.WhiteFlagColor = c));
        panel.Children.Add(BuildColorEditor("Pit/Limiter", () => _plugin.Settings.PitLaneColor, c => _plugin.Settings.PitLaneColor = c));
        panel.Children.Add(BuildColorEditor("Low fuel", () => _plugin.Settings.LowFuelColor, c => _plugin.Settings.LowFuelColor = c));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var preset = new SHButtonSecondary { Content = "Preset Endurance", Margin = new Thickness(0, 0, 8, 0) };
        preset.Click += (_, _) => UpdateSetting(ApplyEndurancePreset);
        var reset = new SHButtonSecondary { Content = "Reset defaults" };
        reset.Click += (_, _) => UpdateSetting(ResetDefaults);
        buttons.Children.Add(preset);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);

        return panel;
    }

    private StackPanel BuildDiagnosticsSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        _diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        panel.Children.Add(_diagnostics);
        return panel;
    }

    private StackPanel BuildTriggersSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text = "Use SimHub native mapping in Controls to bind keyboard, joystick, joypad, or other inputs to this plugin actions.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Actions: HappyLightingLedOn, HappyLightingLedOff, HappyLightingLedToggle, HappyLightingBrightnessUp, HappyLightingBrightnessDown.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        panel.Children.Add(BuildTriggerTestRow("LED On", _plugin.TriggerLedOn));
        panel.Children.Add(BuildTriggerTestRow("LED Off", _plugin.TriggerLedOff));
        panel.Children.Add(BuildTriggerTestRow("LED Toggle", _plugin.TriggerLedToggle));
        panel.Children.Add(BuildTriggerTestRow("Brightness +5%", _plugin.TriggerBrightnessUp));
        panel.Children.Add(BuildTriggerTestRow("Brightness -5%", _plugin.TriggerBrightnessDown));

        return panel;
    }

    private FrameworkElement MakeIntSlider(string label, int min, int max, Func<int> getter, Action<int> setter)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 6, 0, 6) };
        var text = new TextBlock { Text = $"{label}: {getter()}" };
        var slider = new Slider { Minimum = min, Maximum = max, Value = getter(), Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        slider.ValueChanged += (_, _) =>
        {
            var value = (int)Math.Round(slider.Value);
            text.Text = $"{label}: {value}";
            UpdateSetting(() => setter(value));
        };
        panel.Children.Add(text);
        panel.Children.Add(slider);
        return panel;
    }

    private FrameworkElement BuildColorEditor(string label, Func<RgbColor> getter, Action<RgbColor> setter)
    {
        var editor = new LedColorEditorRGB { Label = label, Color = ToDrawingColor(getter()), Margin = new Thickness(0, 4, 0, 4) };
        DependencyPropertyDescriptor.FromProperty(LedColorEditorRGB.ColorProperty, typeof(LedColorEditorRGB))
            ?.AddValueChanged(editor, (_, _) => UpdateSetting(() => setter(ToRgbColor(editor.Color))));
        return editor;
    }

    private FrameworkElement BuildTriggerTestRow(string label, Action testAction)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center });
        var test = new SHButtonSecondary { Content = "Test action" };
        test.Click += (_, _) =>
        {
            try
            {
                testAction();
                _status.Text = $"{label}: action executed";
            }
            catch (Exception ex)
            {
                _status.Text = $"{label}: test failed - {ex.Message}";
            }
        };
        row.Children.Add(test);
        return row;
    }

    private async void DiscoverDevices(Button button)
    {
        try
        {
            button.IsEnabled = false;
            _status.Text = "Scanning...";
            var devices = await BleLightController.DiscoverDevicesAsync(CancellationToken.None);
            _devices.ItemsSource = devices.Select(d => new BluetoothDeviceOption(d)).ToList();
            _status.Text = $"Found {devices.Count} devices";
        }
        catch (Exception ex)
        {
            _status.Text = "Discovery failed: " + ex.Message;
        }
        finally { button.IsEnabled = true; }
    }

    private async void RunConnectionTest(Button button)
    {
        try
        {
            button.IsEnabled = false;
            await _plugin.TestConnectionAsync();
            _status.Text = "Connection OK";
        }
        catch (Exception ex)
        {
            _status.Text = "Test failed: " + ex.Message;
        }
        finally { button.IsEnabled = true; }
    }

    private async void RunDisconnect(Button button)
    {
        try
        {
            button.IsEnabled = false;
            await _plugin.DisconnectDeviceAsync();
            _status.Text = "Disconnected";
        }
        catch (Exception ex)
        {
            _status.Text = "Disconnect failed: " + ex.Message;
        }
        finally { button.IsEnabled = true; }
    }

    private void ApplyEndurancePreset()
    {
        _plugin.Settings.MaxBrightness = 55;
        _plugin.Settings.DayBrightness = 60;
        _plugin.Settings.NightBrightness = 30;
        _plugin.Settings.BlinkOnMs = 250;
        _plugin.Settings.BlinkOffMs = 250;
        _plugin.Settings.EffectDebounceMs = 120;
        _plugin.Settings.EffectMinActiveMs = 300;
    }

    private void ResetDefaults()
    {
        var currentDevice = (_plugin.Settings.BluetoothAddress, _plugin.Settings.BluetoothDeviceId, _plugin.Settings.BluetoothDeviceName);
        _plugin.Settings = new PluginSettings
        {
            BluetoothAddress = currentDevice.BluetoothAddress,
            BluetoothDeviceId = currentDevice.BluetoothDeviceId,
            BluetoothDeviceName = currentDevice.BluetoothDeviceName
        };
    }

    private void UpdateSetting(Action update)
    {
        update();
        _plugin.SaveSettings();
        RefreshUi();
    }

    private void StartUpdates()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => RefreshUi();
        }
        _timer.Start();
        RefreshUi();
    }

    private void RefreshUi()
    {
        var status = _plugin.GetDeviceStatus();
        _deviceStatus.Text = $"Device: {status.DeviceLabel} - {status.State} ({status.Message})";

        var d = _plugin.GetDiagnostics();
        _diagnostics.Text =
            $"effect={d.LastDecision.EffectId} priority={d.LastDecision.Priority} reason={d.LastDecision.Reason}\n" +
            $"pit={d.LastTelemetry.PitLane} limiter={d.LastTelemetry.PitLimiter} fuel={d.LastTelemetry.FuelLiters:0.00} flag={d.LastTelemetry.MarshalFlag} running={d.LastTelemetry.GameRunning}\n" +
            $"enq={d.BleStats.FramesEnqueued} sent={d.BleStats.FramesSent} coalesced={d.BleStats.FramesCoalesced} writes={d.BleStats.PayloadWrites} failures={d.BleStats.WriteFailures}\n" +
            $"avgQueueToWriteMs={d.BleStats.AvgQueueToWriteMs:0.0} avgTelemetryToWriteMs={d.BleStats.AvgTelemetryToWriteMs:0.0} lastWrite={d.BleStats.LastWriteUtc:HH:mm:ss}";
    }

    private static DrawingColor ToDrawingColor(RgbColor color) => DrawingColor.FromArgb(color.R, color.G, color.B);
    private static RgbColor ToRgbColor(DrawingColor color) => new(color.R, color.G, color.B);

    private sealed class BluetoothDeviceOption
    {
        public BluetoothDeviceOption(BTDevice d)
        {
            Name = d.Name ?? string.Empty;
            Id = d.Id ?? string.Empty;
            AddressHex = d.BluetoothAdress.ToString("X12");
        }

        public string Name { get; }
        public string Id { get; }
        public string AddressHex { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? AddressHex : $"{Name} - {Id}";
    }
}
