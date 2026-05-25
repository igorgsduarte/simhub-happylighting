using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SimHub.Bluetooth;
using SimHub.Plugins.DataPlugins.RGBDriverCommon.LedBehaviourEditors;
using SimHub.Plugins.Styles;
using System.ComponentModel;
using System.Windows.Threading;
using DrawingColor = System.Drawing.Color;

namespace HappyLightingPlugin;

public sealed class HappyLightingSettingsControl : UserControl
{
    private readonly HappyLightingSimHubPlugin _plugin;
    private TextBox _deviceText = null!;
    private ComboBox _devices = null!;
    private LedColorEditorRGB _gameNotRunningColor = null!;
    private SHToggleCheckbox _enableGameNotRunning = null!;
    private Slider _debugBrightness = null!;
    private TextBlock _debugBrightnessValue = null!;
    private TextBlock _deviceStatus = null!;
    private TextBlock _status = null!;
    private DispatcherTimer? _deviceStatusTimer;
    private bool _updatingDeviceText;

    public HappyLightingSettingsControl(HappyLightingSimHubPlugin plugin)
    {
        _plugin = plugin;

        var panel = new StackPanel
        {
            Margin = new Thickness(16),
            Orientation = Orientation.Vertical
        };

        panel.Children.Add(new SHSection
        {
            Title = "HappyLighting",
            ShowSeparator = true,
            Content = BuildContent()
        });

        Content = new ScrollViewer { Content = panel };

        Loaded += (_, _) => StartDeviceStatusUpdates();
        Unloaded += (_, _) => StopDeviceStatusUpdates();
    }

    private StackPanel BuildContent()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        _deviceStatus = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            FontWeight = FontWeights.SemiBold
        };
        panel.Children.Add(_deviceStatus);
        RefreshDeviceStatus();

        _status = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 12),
            FontWeight = FontWeights.SemiBold
        };
        panel.Children.Add(_status);

        panel.Children.Add(new SHSubSection
        {
            Title = "Connection",
            Content = BuildConnectionSection()
        });

        panel.Children.Add(new SHSectionSeparator());

        panel.Children.Add(new SHSubSection
        {
            Title = "Brightness",
            Content = BuildBrightnessSection()
        });

        panel.Children.Add(new SHSectionSeparator());

        panel.Children.Add(new SHSubSection
        {
            Title = "Game Not Running",
            Content = BuildGameNotRunningSection()
        });

        panel.Children.Add(new SHSectionSeparator());

        panel.Children.Add(new SHSubSection
        {
            Title = "When Game Is Running",
            Content = BuildEffectsSection()
        });

        return panel;
    }

    private StackPanel BuildBrightnessSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        _debugBrightnessValue = new TextBlock
        {
            Text = $"Global max brightness: {_plugin.Settings.MaxBrightness}%",
            Margin = new Thickness(0, 0, 0, 4)
        };
        panel.Children.Add(_debugBrightnessValue);

        _debugBrightness = new Slider
        {
            Minimum = 1,
            Maximum = 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = false,
            Value = Compatibility.Clamp(_plugin.Settings.MaxBrightness, 1, 100),
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _debugBrightness.ValueChanged += (_, _) =>
        {
            UpdateSetting(() => _plugin.Settings.MaxBrightness = (int)Math.Round(_debugBrightness.Value));
            _debugBrightnessValue.Text = $"Global max brightness: {_plugin.Settings.MaxBrightness}%";
        };
        panel.Children.Add(_debugBrightness);

        return panel;
    }

    private StackPanel BuildConnectionSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        panel.Children.Add(new TextBlock { Text = "Selected device" });
        _deviceText = new TextBox
        {
            Text = BuildDeviceText(),
            Margin = new Thickness(0, 4, 0, 12),
            MinWidth = 520
        };
        _deviceText.TextChanged += (_, _) =>
        {
            if (_updatingDeviceText) return;
            UpdateSetting(() =>
            {
                _plugin.Settings.BluetoothAddress = _deviceText.Text.Trim();
                _plugin.Settings.BluetoothDeviceId = _deviceText.Text.Trim();
            });
        };
        panel.Children.Add(_deviceText);

        var discoveryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var discoverButton = new SHButtonSecondary
        {
            Content = "Discover devices",
            Margin = new Thickness(0, 0, 8, 0)
        };
        discoverButton.Click += (_, _) => DiscoverDevices(discoverButton);
        discoveryRow.Children.Add(discoverButton);

        _devices = new ComboBox
        {
            MinWidth = 520,
            DisplayMemberPath = nameof(BluetoothDeviceOption.DisplayName)
        };
        _devices.SelectionChanged += (_, _) =>
        {
            if (_devices.SelectedItem is not BluetoothDeviceOption selected) return;
            UpdateSetting(() =>
            {
                _plugin.Settings.BluetoothAddress = selected.AddressHex;
                _plugin.Settings.BluetoothDeviceId = selected.Id;
                _plugin.Settings.BluetoothDeviceName = selected.Name;
                _plugin.Settings.BluetoothAddressDescription = selected.AddressDescription;
            });
            _updatingDeviceText = true;
            _deviceText.Text = selected.DisplayName;
            _updatingDeviceText = false;
            RefreshDeviceStatus();
            _ = _plugin.ConnectSavedDeviceAsync().ContinueWith(_ => Dispatcher.Invoke(RefreshDeviceStatus));
        };
        discoveryRow.Children.Add(_devices);
        panel.Children.Add(discoveryRow);

        var testButton = new SHButtonPrimary
        {
            Content = "Test connection (blink green 3x)",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 18)
        };
        testButton.Click += (_, _) => RunConnectionTest(testButton);
        panel.Children.Add(testButton);

        return panel;
    }

    private StackPanel BuildGameNotRunningSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        _enableGameNotRunning = new SHToggleCheckbox
        {
            Content = "Enable color when game is not running",
            IsChecked = _plugin.Settings.EnableGameNotRunningEffect,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _enableGameNotRunning.Checked += (_, _) => UpdateSetting(() => _plugin.Settings.EnableGameNotRunningEffect = true);
        _enableGameNotRunning.Unchecked += (_, _) => UpdateSetting(() => _plugin.Settings.EnableGameNotRunningEffect = false);
        panel.Children.Add(_enableGameNotRunning);

        _gameNotRunningColor = new LedColorEditorRGB
        {
            Label = "Game not running color",
            Color = ToDrawingColor(_plugin.Settings.GameNotRunningColor),
            Margin = new Thickness(0, 4, 0, 12)
        };
        DependencyPropertyDescriptor
            .FromProperty(LedColorEditorRGB.ColorProperty, typeof(LedColorEditorRGB))
            ?.AddValueChanged(_gameNotRunningColor, (_, _) =>
        {
            UpdateSetting(() => _plugin.Settings.GameNotRunningColor = ToRgbColor(_gameNotRunningColor.Color));
        });
        panel.Children.Add(_gameNotRunningColor);

        var playButton = new SHButtonSecondary
        {
            Content = "Play",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        };
        playButton.Click += (_, _) => RunEffectTest(playButton, EffectTestKind.GameNotRunning, "Game Not Running");
        panel.Children.Add(playButton);

        return panel;
    }

    private string BuildDeviceText()
    {
        if (!string.IsNullOrWhiteSpace(_plugin.Settings.BluetoothDeviceName) ||
            !string.IsNullOrWhiteSpace(_plugin.Settings.BluetoothDeviceId))
        {
            return $"{_plugin.Settings.BluetoothDeviceName} - {_plugin.Settings.BluetoothDeviceId}".Trim(' ', '-');
        }

        return _plugin.Settings.BluetoothAddress;
    }

    private async void DiscoverDevices(Button button)
    {
        try
        {
            button.IsEnabled = false;
            _status.Text = "Scanning Bluetooth lights and BLE advertisements...";

            var devices = await Task.Run(() =>
            {
                return BleLightController.DiscoverDevicesAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .Select(device => new BluetoothDeviceOption(device))
                    .OrderBy(device => device.Name)
                    .ToList();
            });

            _devices.ItemsSource = devices;
            _status.Text = devices.Count == 0
                ? "No Bluetooth devices found. Power-cycle the light, keep the phone app closed, and try again."
                : $"Found {devices.Count} device(s). Select one from the list.";
        }
        catch (Exception ex)
        {
            _status.Text = "Discovery failed: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
            RefreshDeviceStatus();
        }
    }

    private async void RunConnectionTest(Button button)
    {
        try
        {
            button.IsEnabled = false;
            _status.Text = "Testing connection...";
            await _plugin.TestConnectionAsync();
            _status.Text = "Test sent: green blink 3x.";
        }
        catch (Exception ex)
        {
            _status.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
            RefreshDeviceStatus();
        }
    }

    private StackPanel BuildEffectsSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        panel.Children.Add(BuildEffectCard(
            "Critical flags",
            "Black and checkered flag alerts",
            _plugin.Settings.EnableCriticalFlags,
            value => _plugin.Settings.EnableCriticalFlags = value,
            EffectTestKind.CriticalFlags,
            "Black flag color",
            (Func<RgbColor>)(() => _plugin.Settings.BlackFlagColor),
            (Action<RgbColor>)(value => _plugin.Settings.BlackFlagColor = value),
            "Checkered color",
            (Func<RgbColor>)(() => _plugin.Settings.CheckeredColor),
            (Action<RgbColor>)(value => _plugin.Settings.CheckeredColor = value)));

        panel.Children.Add(BuildEffectCard(
            "Marshal flags",
            "Yellow, blue, green and white flag effects",
            _plugin.Settings.EnableMarshalFlags,
            value => _plugin.Settings.EnableMarshalFlags = value,
            EffectTestKind.MarshalFlags,
            "Yellow flag color",
            (Func<RgbColor>)(() => _plugin.Settings.YellowFlagColor),
            (Action<RgbColor>)(value => _plugin.Settings.YellowFlagColor = value),
            "Blue flag color",
            (Func<RgbColor>)(() => _plugin.Settings.BlueFlagColor),
            (Action<RgbColor>)(value => _plugin.Settings.BlueFlagColor = value),
            "Green flag color",
            (Func<RgbColor>)(() => _plugin.Settings.GreenFlagColor),
            (Action<RgbColor>)(value => _plugin.Settings.GreenFlagColor = value),
            "White flag color",
            (Func<RgbColor>)(() => _plugin.Settings.WhiteFlagColor),
            (Action<RgbColor>)(value => _plugin.Settings.WhiteFlagColor = value)));

        panel.Children.Add(BuildEffectCard(
            "Pit lane / limiter",
            "Blink while in pit lane or pit limiter is active",
            _plugin.Settings.EnablePitLaneEffects,
            value => _plugin.Settings.EnablePitLaneEffects = value,
            EffectTestKind.PitLaneLimiter,
            "Pit lane color",
            (Func<RgbColor>)(() => _plugin.Settings.PitLaneColor),
            (Action<RgbColor>)(value => _plugin.Settings.PitLaneColor = value)));

        panel.Children.Add(BuildEffectCard(
            "Low fuel",
            "Pulse when fuel reaches the configured threshold",
            _plugin.Settings.EnableLowFuelEffects,
            value => _plugin.Settings.EnableLowFuelEffects = value,
            EffectTestKind.LowFuel,
            "Low fuel color",
            (Func<RgbColor>)(() => _plugin.Settings.LowFuelColor),
            (Action<RgbColor>)(value => _plugin.Settings.LowFuelColor = value)));

        panel.Children.Add(BuildEffectCard(
            "Night light",
            "Warm light when night mode/headlights are active",
            _plugin.Settings.EnableNightLightEffects,
            value => _plugin.Settings.EnableNightLightEffects = value,
            EffectTestKind.NightLight,
            "Night light color",
            (Func<RgbColor>)(() => _plugin.Settings.NightLightColor),
            (Action<RgbColor>)(value => _plugin.Settings.NightLightColor = value)));

        return panel;
    }

    private Border BuildEffectCard(
        string title,
        string subtitle,
        bool enabled,
        Action<bool> setEnabled,
        EffectTestKind effectTestKind,
        params object[] colorBindings)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Orientation = Orientation.Vertical };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = subtitle, FontStyle = FontStyles.Italic, Opacity = 0.85, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(labels, 0);
        header.Children.Add(labels);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };

        var playButton = new SHButtonSecondary
        {
            Content = "Play",
            Margin = new Thickness(0, 0, 8, 0)
        };
        playButton.Click += (_, _) => RunEffectTest(playButton, effectTestKind, title);
        actions.Children.Add(playButton);

        var toggle = new SHToggleCheckbox
        {
            IsChecked = enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Checked += (_, _) => UpdateSetting(() => setEnabled(true));
        toggle.Unchecked += (_, _) => UpdateSetting(() => setEnabled(false));
        actions.Children.Add(toggle);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(header);

        var colorGrid = new UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(0, 12, 0, 0)
        };

        for (var i = 0; i < colorBindings.Length; i += 3)
        {
            var label = (string)colorBindings[i];
            var getColor = (Func<RgbColor>)colorBindings[i + 1];
            var setColor = (Action<RgbColor>)colorBindings[i + 2];
            colorGrid.Children.Add(BuildColorEditor(label, getColor, setColor));
        }

        content.Children.Add(colorGrid);

        return new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content
        };
    }

    private LedColorEditorRGB BuildColorEditor(string label, Func<RgbColor> getColor, Action<RgbColor> setColor)
    {
        var editor = new LedColorEditorRGB
        {
            Label = label,
            Color = ToDrawingColor(getColor()),
            Margin = new Thickness(4)
        };

        DependencyPropertyDescriptor
            .FromProperty(LedColorEditorRGB.ColorProperty, typeof(LedColorEditorRGB))
            ?.AddValueChanged(editor, (_, _) => UpdateSetting(() => setColor(ToRgbColor(editor.Color))));

        return editor;
    }

    private async void RunEffectTest(Button button, EffectTestKind effectTestKind, string title)
    {
        try
        {
            button.IsEnabled = false;
            _status.Text = $"Playing {title} effect on the device...";
            await _plugin.PlayEffectTestAsync(effectTestKind);
            _status.Text = $"{title} effect test finished.";
        }
        catch (Exception ex)
        {
            _status.Text = $"{title} effect test failed: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
            RefreshDeviceStatus();
        }
    }

    private void UpdateSetting(Action update)
    {
        update();
        _plugin.SaveSettings();
        RefreshDeviceStatus();
    }

    private void StartDeviceStatusUpdates()
    {
        RefreshDeviceStatus();
        _deviceStatusTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _deviceStatusTimer.Tick -= DeviceStatusTimerTick;
        _deviceStatusTimer.Tick += DeviceStatusTimerTick;
        _deviceStatusTimer.Start();
    }

    private void StopDeviceStatusUpdates()
    {
        _deviceStatusTimer?.Stop();
    }

    private void DeviceStatusTimerTick(object? sender, EventArgs e)
    {
        RefreshDeviceStatus();
    }

    private void RefreshDeviceStatus()
    {
        if (_deviceStatus is null) return;

        var status = _plugin.GetDeviceStatus();
        _deviceStatus.Text = $"Device: {FormatDeviceStatus(status)}";
        _deviceStatus.Foreground = status.State switch
        {
            DeviceConnectionState.Connected => System.Windows.Media.Brushes.LightGreen,
            DeviceConnectionState.Connecting => System.Windows.Media.Brushes.Khaki,
            DeviceConnectionState.Error => System.Windows.Media.Brushes.LightCoral,
            _ => System.Windows.Media.Brushes.LightGray
        };
    }

    private static string FormatDeviceStatus(DeviceStatusSnapshot status)
    {
        var device = string.IsNullOrWhiteSpace(status.DeviceLabel) ? "No device" : status.DeviceLabel;
        var state = status.State switch
        {
            DeviceConnectionState.NotConfigured => "not configured",
            DeviceConnectionState.Disconnected => "disconnected",
            DeviceConnectionState.Connecting => "connecting",
            DeviceConnectionState.Connected => "connected",
            DeviceConnectionState.Error => "error",
            _ => "unknown"
        };

        return string.IsNullOrWhiteSpace(status.Message)
            ? $"{device} - {state}"
            : $"{device} - {state} ({status.Message})";
    }

    private static DrawingColor ToDrawingColor(RgbColor color)
    {
        return DrawingColor.FromArgb(color.R, color.G, color.B);
    }

    private static RgbColor ToRgbColor(DrawingColor color)
    {
        return new RgbColor(color.R, color.G, color.B);
    }

    private sealed class BluetoothDeviceOption
    {
        public BluetoothDeviceOption(BTDevice device)
        {
            Name = device.Name ?? string.Empty;
            Id = device.Id ?? string.Empty;
            AddressDescription = device.AddressDescription ?? string.Empty;
            AddressHex = device.BluetoothAdress.ToString("X12");
        }

        public string Name { get; }
        public string Id { get; }
        public string AddressDescription { get; }
        public string AddressHex { get; }

        public string DisplayName
        {
            get
            {
                var label = string.IsNullOrWhiteSpace(Name) ? "Bluetooth light" : Name;
                var id = string.IsNullOrWhiteSpace(Id) ? AddressDescription : Id;
                return string.IsNullOrWhiteSpace(id) ? $"{label} ({AddressHex})" : $"{label} - {id}";
            }
        }
    }
}
