# HappyLightingPlugin

SimHub C# plugin skeleton that controls a HappyLighting BLE device directly by Bluetooth MAC/address.

## Architecture

- `HappyLightingPluginLifecycle`: plugin lifecycle hooks + non-blocking `OnDataUpdate`
- `TelemetryReader`: maps SimHub telemetry keys into normalized `TelemetrySnapshot`
- `EffectEngine`: priority-based effects and frame generation
- `BrightnessProfile`: day/night/idle brightness scaling
- `BleLightController`: async BLE queue sender with dedupe/rate-limit/reconnect
- `HappyLightingProtocol`: isolated BLE payload encoder
- `SettingsStore`: persistent configuration
- `UI/SettingsViewModel`: effect toggles, color configurators, and test buttons

## Effects UI model (similar to Ambient Effect style)

The settings model supports per-effect switches and per-effect colors, so your SimHub settings page can render blocks like:

- Critical Flags (on/off)
- Marshal Flags (on/off)
- Pit Lane Effects (on/off + color)
- Low Fuel (on/off + color)
- Night Light (on/off + color)
- Idle Ambient (on/off + color)
- Effects Brightness Day slider
- Effects Brightness Night slider

`SettingsViewModel` exposes boolean toggles and `#RRGGBB` color fields for easy binding in a WPF/XAML settings panel.

## Build and install in SimHub

References:
- SimHub official SDK wiki: https://github.com/SHWotever/SimHub/wiki/Plugin-and-extensions-SDKs
- Example plugin packaging/repo style: https://github.com/simelation/simhub-plugins/tree/main/packages/simhub-sli-plugin

### 1) Prerequisites
- Visual Studio 2022+
- SimHub installed (default: `C:\Program Files (x86)\SimHub`)
- Build target is `.NET Framework 4.8` (`net48`) for SimHub compatibility.

### 2) Basic build
- Open `HappyLightingPlugin.csproj` in Visual Studio.
- Build in `Release`.
- Output DLL will be generated in `HappyLightingPlugin\bin\Release\net48\` unless you configure `SIMHUB_INSTALL_PATH`.

### 3) Direct build-to-SimHub (optional)
If you define an MSBuild property/environment variable `SIMHUB_INSTALL_PATH`, the project writes output directly to:

`$(SIMHUB_INSTALL_PATH)PluginsData\Common\`

Example value:
`SIMHUB_INSTALL_PATH=C:\Program Files (x86)\SimHub\`

### 4) Manual install
If not using direct output path:
1. Close SimHub.
2. Copy `HappyLightingPlugin.dll` (and companion files if any) to:
   - `C:\Program Files (x86)\SimHub\PluginsData\Common\`
3. Start SimHub.
4. Go to settings/plugins and ensure plugin is enabled.

### 5) Debug flow
- Keep project in a SimHub PluginSdk workspace pattern if desired, and run SimHub while rebuilding.
- Always avoid undocumented Ambient Lights internals; rely only on public SDK extension points.

## Integration notes

- Replace `ITelemetrySource` adapter with SimHub SDK property access from your plugin base class.
- Replace `HappyLightingPluginLifecycle` host wiring with actual SimHub plugin lifecycle methods.
- Implement Windows BLE transport in `BleLightController.ConnectAsync`/writer binding.

The current repository intentionally avoids undocumented SimHub Ambient Lights internals.
