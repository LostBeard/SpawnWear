# SpawnWear Architecture

A small wearable OS, Android-shaped, ESP32-sized. Five layers, bottom up:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Apps (managed C#, run in-process under the SpawnWear app host)              │
│  • Launcher (home / clock / app grid)    • Settings        • Clock           │
│  • AI Assistant (voice + text → home PC over WebRTC)                         │
│  • Media Player                          • Voice Recorder  • Activity (IMU)  │
├──────────────────────────────────────────────────────────────────────────────┤
│  UI Framework                                                                │
│  • Drawing primitives + framebuffer    • Touch / button input dispatch       │
│  • Navigation stack + lifecycle        • Theme + system widgets              │
├──────────────────────────────────────────────────────────────────────────────┤
│  System Services (singletons, started at boot)                               │
│  • Power (AXP2101)        • WiFi (station + soft-AP)    • BLE (GATT server) │
│  • RTC (PCF85063)         • Audio (ES8311 + ES7210)     • Storage (TF/flash)│
│  • Sensors (QMI8658)      • Update (OTA, app payloads)  • Logger            │
├──────────────────────────────────────────────────────────────────────────────┤
│  HAL / Drivers (hand-rolled C# unless upstream nanoFramework already covers) │
│  • CO5300 AMOLED via QSPI              • FT3168 touch via I²C                │
│  • AXP2101 PMIC                        • PCF85063 RTC                        │
│  • QMI8658 IMU                         • ES8311 + ES7210 audio + PDM mics    │
│  • TF / microSD via SDMMC              • USB-CDC                             │
├──────────────────────────────────────────────────────────────────────────────┤
│  .NET nanoFramework runtime + Espressif ESP32-S3 firmware                    │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Layer rules

### HAL / Drivers

Hand-rolled C# against the chip datasheets unless `nanoFramework` already ships a driver. Currently in the repo:
- `Drivers/Power/Axp2101Driver.cs` - PMIC, battery percent + voltage + VBUS detect
- `Drivers/Rtc/Pcf85063Driver.cs` - RTC, read / write date+time
- `Drivers/Touch/Ft3168Driver.cs` - capacitive touch, finger position
- `Drivers/BoardSetup.cs` - one-shot I²C bus initialization, pin assignments from `BoardPins.cs`

Drivers DO NOT own state shared with apps. Drivers are I/O - they read and write registers on demand. The state lives in the System Service that owns the driver.

### System Services

Singletons started at boot. Each owns exactly one piece of hardware and exposes an interface that apps consume. Today:
- `Drivers/Wifi/WifiService.cs` - WiFi connect via `WifiNetworkHelper.ConnectDhcp`, exposes `IsConnected` + `IpAddress`
- `Services/HttpServer.cs` - raw socket on port 8080 serving `/screenshot.bin` and an index page
- `Services/EventLoop.cs` - the host event loop that drives Tick + sleep state machine

The full Phase 3 service host (singletons with lifecycle and inter-service events) is still TODO; today services are top-level statics in `Program.cs`.

**Rule: one owner per resource.** The display backlight, BLE radio, audio output, AXP2101 — each has exactly one system service that owns it. Apps ask through the service. Two apps grabbing the speaker simultaneously is a service-design failure, not an "apps fight it out" feature.

### UI Framework

Drawing primitives, navigation stack, input dispatch, and system widgets. Today:
- `UI/SmallFont.cs` - 5x7 ASCII bitmap font with `DrawString` + `MeasureString`
- `UI/SegmentFont.cs` - 7-segment digit font for clock readouts
- `UI/StatusBar.cs` - top 64-px title bar (time + WiFi + USB + BLE + battery)
- `UI/PageDots.cs` - bottom-center page indicator (Android-style pill for active screen)
- `UI/LauncherScreen.cs` - 3x3 grid of app tiles with gradient backgrounds + rounded corner masks
- `UI/Watchface.cs` - HH:MM:SS digital readout + battery bar + date label
- `UI/StatsScreen.cs`, `UI/SettingsScreen.cs` - example app screens
- `UI/IScreen.cs` + `UI/ScreenNavigator.cs` - screen interface and rotation logic

Phase 2's full lifecycle (`OnCreate` / `OnResume` / `OnPause` / `OnDestroy`) is partly implemented as `OnResume` / `OnPause` on `IScreen`. Phase 8 will extend it to per-app sandboxes for SD-card-loadable apps.

### Apps

Managed C# classes implementing the screen lifecycle. Run in-process under the SpawnWear app host. Built-ins are compiled into the firmware initially; Phase 8 adds SD-card-loadable apps.

**Rule: lifecycle discipline.** Apps must implement `OnPause` / `OnResume` correctly. Anything an app starts (timer, BLE notify subscription, sensor stream, audio session) it must stop in `OnPause` and restart in `OnResume`. Background services keep ticking through pause / resume.

## Boot sequence

`Program.cs::Main` does the following, in order:

1. **Power rails** - open AXP2101 over I²C, defensively enable display rails (DC1 + ALDO1/2/3), enable ADC channels, log battery state
2. **Touch** - reset FT3168, probe for a valid device id, hook the INT pin
3. **WiFi** - construct `WifiService`, attempt connect against stored credentials in `Config/WifiCredentials.cs` (gitignored - file contains real password)
4. **Display** - `DisplayControl.Initialize`, allocate framebuffer (411 KB at 410x502 RGB565)
5. **HTTP server** - if WiFi is up, bind port 8080 and start the listener thread
6. **Status bar + screens** - build `StatusBar` + `Watchface` + `StatsScreen` + `SettingsScreen` + `LauncherScreen`
7. **Navigator** - construct `ScreenNavigator` with the screens; set initial active = launcher (index 0)
8. **First paint** - call `_nav.Current.OnResume()` to paint the boot screen (without this call the launcher's tiles never paint until the user navigates away and back - see commit `f63cfb6`)
9. **Event loop** - `_eventLoop.Run()` loops on `OnTick`, dispatching touch + button events and ticking the active screen

Total boot from PWR-click to launcher-painted: ~3-4 seconds with current configuration.

## BLE GATT layout

When BLE is active (currently disabled to fit under the deploy ceiling, see `Research/nf-interpreter-deploy-ceiling.md`), the watch advertises a single `GattServiceProvider` with one primary service. nanoFramework on ESP32 advertises one service at a time reliably.

Custom UUIDs use the base `a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb`. Note the `c1` (not `c0` as in NanoFrameTest1) so a phone with both PWAs installed doesn't get device contracts confused.

Apps don't talk to GATT directly; they talk to the BLE system service. Advertising is user-toggleable via Settings → BLE; not always-on the way the demo scaffold has it.

## Power model

- **Active**: panel on at full brightness, AMOLED black bg = ~0 mA per off pixel + partial flush ~25 KB/s
- **Dim**: same as Active but brightness drops to 0x40 (~1/4 of full)
- **Asleep**: CO5300 SLPIN + DISPOFF → panel ~µA, no flushes, CPU tickless-idle for the full 30 s

Idle countdown: Active → Dim after 15 s, Dim → Sleep after 30 s. Touch wakes the panel and triggers a full repaint. BOOT button on GPIO0 force-sleeps as a hardware shortcut. PWR button (AXP2101 EXIO6) is reserved for hard shutdown when held > 6 s.

Tick budget:
- Finger held = 16 ms (smooth 60 Hz)
- Active watchface = 1000 ms (only seconds digit changes)
- Dim watchface = 1000 ms (still ticking, just dimmer)
- Asleep = 30000 ms (housekeeping; touch INT wakes early)

## What "OS-shaped" means here

- **Apps are not fixed.** The launcher hosts a list of apps; built-in ones are compiled into the firmware initially, but the long-term aim is OTA-installable app payloads.
- **Services are background daemons,** consumed by apps via interfaces. Only one PMIC, one BLE stack, one display backlight - the system service owns it, apps ask politely.
- **Lifecycle is Android-flavored.** Apps have `OnCreate` / `OnResume` / `OnPause` / `OnDestroy` (partial today; full at Phase 8). The launcher decides what's foregrounded. Background services keep ticking through pause / resume.
- **Resource budgets are explicit.** PSRAM (8 MB), heap, flash slots, BLE MTU, WiFi airtime. Apps that hog get killed.
- **Power-aware by default.** AXP2101 + display-rail control + WiFi/BLE radio gating are first-class system concerns, not afterthoughts.
