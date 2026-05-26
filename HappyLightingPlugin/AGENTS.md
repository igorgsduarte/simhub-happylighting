# HappyLighting Plugin - Technical Documentation (Current Behavior)

## 1) Goal and runtime context
This plugin runs inside SimHub and controls a HappyLighting BLE light based on racing telemetry (focused on iRacing behavior).

High-level responsibilities:
- Read telemetry from SimHub (`GameData` + `DataCore` fallback).
- Decide which single effect should be active at each update tick.
- Convert effect state into BLE frames.
- Send frames asynchronously to the BLE device with reconnect and rate limiting.

Main runtime host:
- `HappyLightingSimHubPlugin` (`IPlugin`, `IDataPlugin`, `ISettingPlugin`, `IWPFSettings`, `IWPFSettingsV2`).

Core modules:
- `HappyLightingSimHubPlugin`: SimHub entrypoint + telemetry snapshot building.
- `HappyLightingPluginLifecycle`: orchestration, effect test mode, watchdog logic.
- `EffectEngine`: effect priority and frame generation.
- `BleLightController`: BLE discovery/connection/write queue and sender loop.
- `HappyLightingProtocol`: payload encoding.
- `BrightnessProfile`: brightness conversion policy.
- `HappyLightingSettingsControl`: plugin UI.

---

## 2) End-to-end data flow
1. SimHub calls `DataUpdate(...)` on each telemetry tick.
2. `HappyLightingSimHubPlugin.BuildSnapshot(GameData)` creates a normalized `TelemetrySnapshot`.
   - Primary source: `GameData.NewData`.
   - Fallback source: `PluginManager.GetPropertyValue(...)` on DataCore keys.
3. Snapshot is passed to `HappyLightingPluginLifecycle.OnTelemetrySnapshot(...)`.
4. Lifecycle gates output if:
   - debug live send is enabled, or
   - effect test mode is currently running.
5. `EffectEngine.Compute(...)` selects exactly one effect (priority-based).
6. Selected `LightFrame` is enqueued in `BleLightController`.
7. `BleLightController` sender loop dequeues, deduplicates (`_lastSent`) and writes payload variants over GATT.
8. `TickTelemetryWatchdog()` may inject fallback frame when telemetry is stale (>2s), unless test mode/debug mode is active.

---

## 3) Telemetry pipeline details
Current snapshot strategy is hybrid:
- Primary `GameData` values:
  - pit lane: `IsInPitLane` / `IsInPit`
  - pit limiter: `PitLimiterOn`
  - fuel: `Fuel`, fallback `FuelRaw`
  - flags: flag booleans + `Flag_Name`
  - game running: `data.GameRunning`
- Fallback `DataCore` values (via reflection):
  - `DataCorePlugin.GameData.NewData.OnPitRoad`
  - `DataCorePlugin.GameData.NewData.PitLim`
  - `DataCorePlugin.GameData.NewData.FuelLevel`
  - `DataCorePlugin.GameData.NewData.dcHeadlights`
  - `DataCorePlugin.GameData.NewData.IsNight`
  - `DataCorePlugin.GameRunning`
  - `DataCorePlugin.GameData.NewData.Flag`

Flag normalization:
- Normalized to: `black`, `checkered`, `yellow`, `blue`, `green`, `white` when matching by `Contains`.
- Unknown flag text passes through as lowercase string.

Important note:
- `TelemetryReader` exists (DataCore adapter style) but **is not used** in SimHub runtime path today (`DataUpdate` uses `BuildSnapshot` directly).

---

## 4) Effect selection and priority model
`EffectEngine.Compute(...)` is first-match-wins. Order is the real priority:
1. `GameNotRunning` (if enabled and game not running)
2. Critical flags (`black`, `checkered`)
3. Marshal flags (`yellow`, `blue`, `green`, `white`)
4. Pit lane / pit limiter
5. Low fuel
6. Night light
7. Idle ambient
8. Off

Effect rendering:
- Blink uses UTC tick phase (`DateTimeOffset.UtcNow.Ticks / interval`).
- Low fuel pulse uses internal tick counter (`_tick`) sine wave.
- Idle and game-not-running use idle brightness conversion.

---

## 5) Brightness architecture
Brightness has two layers:
1. Effect layer:
   - `ResolveBrightness`: day/night (0-100% to 0-255).
   - `ResolveIdleBrightness`: idle (0-100% to 0-255).
2. Transport layer cap (`BleLightController.ApplyGlobalMaxBrightness`):
   - `MaxBrightness` converted to 0-255 and applied as a **ceiling** (`min(frame.Brightness, maxByte)`).

This means:
- `IdleBrightness` defines requested idle intensity.
- `MaxBrightness` is a hard upper bound for all active frames.

---

## 6) BLE architecture and write behavior
Connection:
- Device selection from UI populates address/device id/name.
- `EnsureConnectedAsync` only connects when not already connected to selected address.
- GATT characteristic is resolved by preferred UUIDs (`fff3`, `fff1`, `ffe1`, `ffe9`) then writable fallback scan.

Sending:
- Producer/consumer queue (`ConcurrentQueue<LightFrame>` + semaphore).
- Sender loop deduplicates identical consecutive frames.
- Each logical frame is encoded into payload variants:
  - ON frame: preamble color command + brightness command + final color command.
  - OFF frame: dedicated off command.
- Rate limiting via `BleRateLimitMs`.
- Reconnect retry on closed BLE object or sender faults.

Recent stabilization:
- `WritePayloadWithReconnectAsync` now calls `EnsureConnectedAsync` only when writer is null, avoiding expensive checks every payload.

---

## 7) Effect test mode behavior
`PlayEffectTestAsync(...)`:
- Ensures device connected once.
- Sets `_isEffectTestRunning=1` for exclusive test mode.
- During test mode:
  - telemetry frames are ignored;
  - watchdog fallback is suppressed.
- Loop runs ~4 seconds with test snapshot.
- For alert-like tests (`CriticalFlags`, `MarshalFlags`, `PitLaneLimiter`, `GameNotRunning`), explicit ON/OFF blinking is forced in the loop.
- Sends OFF at end and clears test mode flag.

Purpose:
- Prevent interference between live telemetry/idle/watchdog and manual Play tests.

---

## 8) Settings and UI flow
Persistence:
- SimHub native settings persistence via `ReadCommonSettings` / `SaveCommonSettings`.
- `SettingsStore` JSON helper exists but is currently not active in main host flow.

UI:
- Device discovery + selection.
- Connection test.
- Global max brightness slider.
- Game-not-running section + color.
- Effect cards with toggles, color editors, and Play button per effect.
- Device status polling every 2 seconds.

---

## 9) Architecture decisions and rationale
1. Hybrid telemetry (`GameData` primary, `DataCore` fallback):
   - Improves resilience across iRacing field availability differences.
2. Asynchronous BLE queue:
   - Prevents blocking SimHub telemetry thread.
3. Single winning effect at a time:
   - Simple mental model and deterministic output.
4. Exclusive test mode:
   - Prevents race conditions between test and live telemetry.
5. Global brightness as cap:
   - Matches user expectation for “maximum allowed brightness”.

---

## 10) Known bugs, risks, and limitations
1. Reflection-based DataCore access is fragile:
   - `PluginManager.GetPropertyValue` is called via reflection each read.
   - If API signature changes, fallback silently fails and stays on GameData only.
2. `EffectPriority` enum numeric values are reverse-intuitive:
   - Lower number is “more critical” semantically, but enum is currently not used for sorting logic.
3. Blink phase is wall-clock based:
   - Blink transitions can appear phase-shifted relative to UI click timing.
4. Build/runtime warning:
   - `System.Runtime.WindowsRuntime` version conflict warning (`MSB3277`) appears during build; currently non-blocking.
5. Some classes are currently redundant:
   - `TelemetryReader` and `SettingsStore` are not used in the primary SimHub execution path.
6. Device/protocol variance:
   - Different HappyLighting firmware variants may require different write characteristics or payload acceptance timing.

---

## 11) Recommended maintenance backlog
1. Replace reflection telemetry reads with typed SimHub API access if available.
2. Consolidate telemetry path (either fully use `TelemetryReader` or remove it).
3. Add structured diagnostics mode showing raw telemetry field values per tick.
4. Add integration tests for:
   - effect priority transitions,
   - test mode exclusion,
   - brightness cap math,
   - flag normalization.
5. Add optional per-effect blink interval settings.

---

## 12) Quick troubleshooting checklist
If effects do not match iRacing telemetry:
1. Confirm plugin is connected and status is `Connected`.
2. Enable detailed logs and verify snapshot fields (`pit/limiter/flag/fuel/gameRunning`).
3. Validate selected device is the expected BLE endpoint.
4. Test `Play` for each effect to confirm command acceptance.
5. Check `MaxBrightness` is not too low.
6. Verify no phone app is simultaneously controlling the light.

