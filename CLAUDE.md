# SpawnWear — Project Instructions

## Mission

A small **wearable OS** in C# (Android-shaped, ESP32-sized) for the **Waveshare ESP32-S3-Touch-AMOLED-2.06 watch**. SpawnWear is not a single firmware app — it's a launcher + system services + UI framework + apps that the user installs/launches/closes.

Flagship app: **AI Assistant** — voice + text chat to TJ's home PC over WebRTC (SpawnDev.RTC), watch is the I/O device. Other built-in apps: Settings, Clock, Media Player, Voice Recorder, Activity.

Companion **Blazor WebAssembly PWA** mirrors the watch UI over BLE + WiFi — useful for setup before the on-screen keyboard is comfortable, and for live debugging from a laptop. **Optional**, never required.

**Primary target hardware is fixed.** All pin numbers, drivers, and capabilities are documented in `README.md`. Do not generalize — this is not a generic "ESP32-S3 wearable" project, it is the 2.06 watch project.

## Read Before Coding

1. `README.md` — full IC list, pin map, I²C addresses, roadmap. **The pins live there, not in your head.**
2. `D:\users\tj\Projects\SpawnWear\_vendor-waveshare-demo\` — cloned upstream Arduino + ESP-IDF demos (sits in the project parent folder, **outside git**). Read these before guessing how a chip works.
3. `D:\users\tj\Projects\SpawnWear\_wiki-decoded.txt` — decoded Waveshare wiki page for prose context (also outside git).
4. `D:\users\tj\Projects\NanoFrameTest1\NanoFrameTest1\` — the **reference architecture** for this project (different board, same shape: nanoFramework GATT + Blazor PWA + Playwright tests). When in doubt, mirror it.

## Architecture

Layered, Android-flavored. From bottom to top:

1. **HAL / Drivers** — hand-rolled C# (CO5300 QSPI, FT3168 I²C touch, AXP2101, PCF85063, QMI8658, ES8311, ES7210). Lowest layer, no framework dependencies above the runtime.
2. **System Services** — singletons started at boot: Power, WiFi, BLE, RTC, Audio, Storage, Sensors, Update, Logger. Apps consume services through interfaces; only one of each.
3. **UI Framework** — drawing primitives, framebuffer, touch + button dispatch, navigation stack, app lifecycle (`OnCreate` / `OnResume` / `OnPause` / `OnDestroy`), system widgets (status bar, keyboard, dialogs, list, slider, switch).
4. **Apps** — managed C# classes implementing the app lifecycle, run in-process under the SpawnWear app host. Built-ins: Launcher, Settings, Clock, AI Assistant (flagship), Media Player, Voice Recorder, Activity. Aim for OTA-installable apps once the foundation is solid.
5. **Companion Blazor WASM PWA** — talks BLE + WiFi to the watch, mirrors every built-in app as a remote-control surface. Uses `SpawnDev.BlazorJS` (no raw JS, no `IJSRuntime`).

### BLE GATT layout

- **Single `GattServiceProvider`** with one primary service. nanoFramework on ESP32 advertises one service at a time reliably.
- **Custom UUIDs** under base `a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb` (note `c1` not `c0` — `c0` is NanoFrameTest1's namespace, this project uses `c1` so a phone with both PWAs installed doesn't get the device contracts confused).
- BLE GATT is just one of many transports the **BLE system service** owns. Apps don't talk to GATT directly — they talk to the service.
- BLE characteristics are user-toggleable via Settings → BLE; advertising is not "always on" the way it is in the demo scaffold. The demo scaffold (`WifiConfigService` etc) graduates from "always-on" to "user-controlled" once the Settings app exists.

## Rules

- **OS-shaped, not app-shaped.** Don't add features to `Program.cs` or `WifiConfigService.cs` directly. They're scaffolding. Real features go through a **system service** (a service singleton) and an **app** (a UI surface). If you can't decide which one a piece of functionality belongs in, write the question down and ask.
- **One owner per resource.** Display backlight, BLE radio, audio output, AXP2101 — each has exactly one system service that owns it. Apps ask through the service. Two apps grabbing the speaker simultaneously is a service-design failure, not an "apps fight it out" feature.
- **Lifecycle discipline.** Apps must implement `OnPause` / `OnResume` correctly. Anything an app starts (timer, BLE notify subscription, sensor stream, audio session) it must stop in `OnPause` and restart in `OnResume`. Background services keep ticking through pause / resume.
- **Power-aware by default.** Every service exposes a "low-power mode". The Power service (AXP2101 driver) coordinates: when the screen is off + idle, it tells everyone to drop to low-power, gates radios, dims rails. Don't write code that polls forever at full clock — use events, sleeps, and AXP2101 wake interrupts.
- **SpawnDev.BlazorJS for ALL JS interop.** No raw JavaScript. No `IJSRuntime`. (Companion PWA only.)
- **SpawnDev.RTC for WebRTC.** That's the AI Assistant transport. Don't write a parallel WebRTC stack.
- **Fix libraries, don't work around.** If `nanoFramework.Device.Bluetooth` / `nanoFramework.Hardware.Esp32` / SpawnDev.BlazorJS / SpawnDev.RTC is missing something, fix it upstream — see Rule 2 in `D:\users\tj\Projects\CLAUDE.md`.
- **DI first** in the Blazor companion. `BlazorJSRuntime` injected via constructor, never the static accessor in DI-available contexts.
- **Event properties** in SpawnDev.BlazorJS — `OnGATTServerDisconnected += handler`, never `AddEventListener`.
- **Performance** — no unnecessary .NET ↔ JS roundtrips. Keep data on the side that needs it.
- **Pin numbers come from `pin_config.h`** (vendor source) or the schematic PDF — never from memory, never from another Waveshare watch's wiki.
- **The watch is the primary device.** Don't design features that require the PWA to be running. The PWA is a remote, not a tether.

## Watch-Specific Gotchas

These are the things that bit somebody else first. Don't be the second.

- **PWR button is on the AXP2101 (EXIO6 over I²C), not a GPIO.** The button reading is an I²C register read on the PMIC, not a `GpioPin.Read()`. Don't waste an hour wiring it as if it were direct GPIO.
- **Display is QSPI, not SPI.** nanoFramework's existing display drivers don't drive CO5300. Phase 5 has an open research question — don't promise display in earlier phases.
- **Backlight control is over QSPI register 0x51 on the CO5300**, not a separate PWM-able GPIO. There is no backlight pin to PWM.
- **Hold PWR > 6 s = power off.** During testing, don't hold it down. Single-click to wake from soft-off.
- **BOOT (GPIO0) doubles as a user button** during normal operation. Hold during power-on for download mode; releasable click during runtime is fine.
- **One I²C bus, six devices.** SDA=GPIO15, SCL=GPIO14, addresses 0x18 / 0x34 / 0x38 / 0x40 / 0x51 / 0x6B. Bus contention matters; lock around chip-specific transactions.
- **AXP2101 manages the screen rail.** Killing the screen rail to save power is done by writing AXP2101 registers, not by sleeping the CO5300 (though that's complementary).
- **PCF85063 RTC is battery-backed via the AXP2101 coin cell pin.** The AXP2101 keeps the RTC alive across main-battery removal IF the coin cell is installed — verify TJ's unit has one before promising RTC persistence across full power loss.
- **PSRAM is octal, 8 MB.** Lots of memory by ESP32 standards but allocations still need `MALLOC_CAP_SPIRAM` / equivalent in nanoFramework's surface. Watch out for fragmentation when buffering audio or large notify payloads.

## Testing

- **PlaywrightMultiTest pattern** (mirroring NanoFrameTest1.Tests) for the Blazor WASM PWA.
- **Hardware-in-the-loop tests** require the watch on a known COM port. Tag those `[Category("HardwareWatch")]` so they're optional.
- **No mock tests.** The PWA tests must drive a real Chromium with a real Web Bluetooth stack. The firmware tests must talk to a real watch over a real BLE link.
- **TJ confirms, doesn't test.** Run the test suite yourself before declaring something ready. See `D:\users\tj\Projects\CLAUDE.md` Rule 5.

## Lane Ownership

This project is **Riker's lane** for now (consuming-project work, BLE plumbing, PWA UI). If a SpawnDev library needs a fix to support the watch:
- BLE / WiFi / Web Bluetooth → SpawnDev.BlazorJS or upstream nanoFramework — Riker
- WebRTC integration (Phase 7) → SpawnDev.RTC — Riker
- Display QSPI / CO5300 driver path (Phase 5 research) → coordinate with Captain before any cross-lane fix

## Vendor References

- Schematic PDF: <https://files.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06/ESP32-S3-Touch-AMOLED-2.06.pdf>
- Vendor demos: <https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-2.06> (cloned to `D:\users\tj\Projects\SpawnWear\_vendor-waveshare-demo\`)
- Datasheets (in `README.md` table): QMI8658, PCF85063, AXP2101, ES8311, ES7210, FT3168, ESP32-S3 series
- nanoFramework ESP32-S3: <https://docs.nanoframework.net/content/reference-targets/esp32-s3.html>
- nanoFramework Bluetooth samples: <https://github.com/nanoframework/Samples/tree/main/samples/Bluetooth>
- NanoFrameTest1 reference repo: `D:\users\tj\Projects\NanoFrameTest1\NanoFrameTest1\`
