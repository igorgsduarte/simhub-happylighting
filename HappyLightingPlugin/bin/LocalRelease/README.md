# HappyLightingPlugin

SimHub plugin that controls HappyLighting BLE lights from telemetry with deterministic effect priority and asynchronous BLE delivery.

## Runtime architecture

- `HappyLightingSimHubPlugin`: SimHub host entrypoint, settings persistence, action registration.
- `TelemetryPipeline`: hybrid telemetry normalization (`GameData` primary + `DataCore` fallback).
- `HappyLightingPluginLifecycle`: effect orchestration, watchdog, diagnostics, manual trigger actions.
- `EffectEngine` + `EffectStateMachine`: first-match effect priority with debounce/min-active handling.
- `BleLightController`: async O(1) frame coalescing queue, dedupe, reconnect, and GATT write flow.
- `HappyLightingProtocol`: HappyLighting payload encoder.

## SimHub actions (triggers/buttons)

- `TestConnectionGreenBlink`
- `HappyLightingLedOn`
- `HappyLightingLedOff`
- `HappyLightingLedToggle`
- `HappyLightingBrightnessUp`
- `HappyLightingBrightnessDown`

Brightness actions update `MaxBrightness` with clamp (`1..100`) and persist immediately via SimHub common settings.

## Triggers tab

- The plugin settings UI includes a dedicated `Triggers` tab.
- Input binding is configured using SimHub native `Controls` UI (same picker dialog used by built-in plugins).
- Plugin actions available for mapping:
  - `HappyLightingLedOn`
  - `HappyLightingLedOff`
  - `HappyLightingLedToggle`
  - `HappyLightingBrightnessUp`
  - `HappyLightingBrightnessDown`

## Build

- Target framework: `.NET Framework 4.8` (`net48`)
- Prerequisites: Visual Studio 2022+, SimHub installed
- Build command: `dotnet build HappyLightingPlugin.csproj --no-restore`

The known `MSB3277` warning about `System.Runtime.WindowsRuntime` can appear depending on local references; it is non-blocking unless your environment promotes warnings to errors.
