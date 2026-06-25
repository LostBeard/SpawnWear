# SpawnWear Roadmap

The display is the user interface, so it leads. Plumbing (radios, sensors, audio, OTA) is still needed - a watch with no radios is a fancy clock - but each piece gets exposed THROUGH an app or Settings page, not as a substitute for one.

This is the canonical roadmap. The README's Status / Milestones table tracks what already shipped; this file describes what's coming and the ordering rationale.

---

## Phase 1 - Display + touch + input (the UI substrate) - **complete 2026-05-03**

- [x] CO5300 QSPI driver in C# - landed on `LostBeard/nf-interpreter@feature/qspi-display-driver` and `LostBeard/nanoFramework.Graphics@feature/qspi-display-driver`
- [x] FT3168 touch I²C driver - `SpawnWear/Drivers/Touch/Ft3168Driver.cs` (fixed burst-read layout 2026-05-04, see `Research/ft3168-burst-read-layout.md`)
- [x] Frame-buffer + drawing primitives - sits on top of `nanoFramework.Graphics` with new `DisplayControl.Sleep / Wake / SetBrightness` upstream extensions
- [x] Touch + button input dispatcher - `Services/EventLoop.cs` is the host loop; tap classification + cycle-on-tap is in `Program.cs`
- [x] BOOT button polling on GPIO0 - single-press = force panel Sleep
- [x] CO5300 alignment quirk baked into firmware (every `Bitmap.Flush(x, y, w, h)` aligns automatically)

## Phase 2 - UI Framework + Launcher - **largely complete 2026-05-04**

- [x] Drawing primitives: text (SmallFont 5x7, SegmentFont 7-segment), rounded rects (corner-mask staircase), gradients (16-band horizontal slices), icons (rectangle-only)
- [x] Navigation stack + screen lifecycle - `IScreen` + `ScreenNavigator` (1-screen rotation; full Android `OnCreate` / `OnResume` / `OnPause` / `OnDestroy` is Phase 8 territory)
- [x] System widgets: status bar (time + WiFi + USB + BLE + battery), page dots (Android-style pill for active screen), list view, gradient tile
- [x] **Launcher app**: 3x3 grid of tiles with rounded-corner gradient backgrounds, notification badges, status bar, page indicator
- [ ] Toast / dialog primitives
- [ ] On-screen keyboard (deferred until WiFi config UI needs it)
- [ ] Slider / switch widgets (deferred until first Settings page that needs them)

## Phase 3 - System Services + power/sensors plumbing - **in progress**

- [x] Project scaffolding (nanoFramework solution, BLE GATT layout, gitignore, repo at github.com/LostBeard/SpawnWear)
- [x] AXP2101 driver basics: battery V / I / SOC, charge state, USB-VBUS detect (PWR button via EXIO6 still TODO)
- [x] PCF85063 RTC driver: read / set time, weekday, month, day (alarms TODO)
- [x] WiFi service: `WifiNetworkHelper.ConnectDhcp` against stored credentials, status reported to status bar
- [x] HTTP server: port 8080 raw socket, `/screenshot.bin` (RGB565 BE) + index page for live framebuffer capture
- [ ] Service host: singletons, lifecycle, inter-service events (currently each service is a top-level static in Program.cs)
- [ ] QMI8658 IMU driver: accel + gyro + step-count
- [x] Storage service: TF/microSD mount (SDSPI on SPI3_HOST; clock raised 400 kHz->4 MHz 2026-06-20) + simple key-value store in internal flash for settings persistence — SD read/write hardware-verified 2026-06-24 (`sys.files` over WebRTC, chunked)
- [ ] Logger service: ring buffer + USB-CDC sink + BLE notify sink

## Phase 4 - Settings app

- [x] Page: **Settings** scaffold - 3-row ListView (BRIGHTNESS / SLEEP / BUILD)
- [ ] Page: **Battery** - level, charging state, USB-VBUS, charge target slider
- [ ] Page: **Display** - brightness slider (CO5300 reg 0x51), sleep timeout, rotation
- [ ] Page: **Time / RTC** - read PCF85063, set fields, sync-from-NTP toggle
- [ ] Page: **About** - firmware version, MAC, IP, free heap, uptime
- [ ] Page: **WiFi** - toggle, SSID list, on-screen keyboard for password, current connection details
- [ ] Page: **Bluetooth** - radio toggle, paired devices, scan
- [ ] Page: **BLE** - GATT-server visibility toggle, advertised name editor

## Phase 5 - Clock app

- [x] V1 watchface: HH:MM:SS in 7-segment digits, battery bar, date label (WEEKDAY MONTH DAY)
- [ ] Multiple watch faces (analog, digital, complications)
- [ ] Alarms (RTC alarm interrupt → wake from low-power)
- [ ] Timer + stopwatch

## Phase 6 - Audio service + Voice Recorder + Media Player

- [ ] ES8311 playback driver (I²S) - depends on `nanoFramework.Hardware.Esp32` I²S surface
- [ ] ES7210 capture driver (PDM dual mic + echo cancel ADC)
- [ ] Audio service: shared pipeline, volume, mute, mic gain, format negotiation
- [ ] Page: Settings → **Sound** (volume / mic gain / test-tone / mic-level meter)
- [ ] **Voice Recorder app**: capture to TF, listen back, delete, share over WiFi
- [ ] **Media Player app**: play files from TF, basic transport controls; HTTP streaming if airtime allows

## Phase 7 - WebRTC service + AI Assistant app (flagship)

- [x] WebRTC peer service: **LIVE 2026-06-24.** Watch-side WebRTC over the libpeer LostBeard fork (`spawnwear` branch); authenticated (mutual Ed25519) multiplexed channel bus; DTLS answerer-role blocker resolved 2026-06-24 (SipSorcery 10.0.7 / RTC 1.1.11), hardware-verified. As-built reference: `Docs/transport.md`. (Original line: SpawnDev.RTC integration; signaling via the companion PWA or a small HTTP signaling relay; ICE / SDP plumbing.)
- [ ] **AI Assistant app**: push-to-talk button, on-screen keyboard for text, live transcript display, TTS playback through speaker, conversation history persisted to TF
- [ ] PC-side counterpart: a small Blazor / .NET host on TJ's PC that the watch dials, runs the assistant model, returns audio + text

## Phase 8 - OTA + app install

- [ ] OTA firmware update path (nanoFramework standard)
- [ ] Page: **About → Update** - pull URL field, "Check for update" button, download + reboot flow
- [x] **SD-card-loadable apps** - SHIPPED + hardware-verified 2026-06-25. Apps load from `D:\apps` loose-file dirs (dir-per-app model); launcher shows a tile per app (DiceApp, CounterApp, HelloWorldApp, PaintApp). See `sd-card-apps.md`. (Original line: manifest + payload format, launcher reads SD root for installed-app metadata, registers tile + icon at boot.)
- [ ] App lifecycle: full Android-style `OnCreate` / `OnResume` / `OnPause` / `OnDestroy` (Phase 2 only does enter/exit on screen switch)

## Phase 9 - Activity app + later

- [ ] **Activity app**: step count, daily totals, motion log
- [ ] User-contributed apps via the install path
- [ ] Polish, theming, watchface marketplace ideas

## Companion Blazor WASM PWA (parallel track, starts in Phase 4)

See `companion-pwa.md`.

- [ ] Scaffolded with SpawnDev.BlazorJS
- [ ] Mirrors every Settings page over BLE (provisioning + diagnostics work even before the on-device keyboard is comfortable)
- [ ] Mirrors every built-in app (remote launcher)
- [ ] Live system log viewer over BLE notify
- [ ] PWA installable so it lives on a phone home screen

---

## Open blockers

- ~~**nf-interpreter deploy ceiling** (~290 KB wire-protocol).~~ **RESOLVED 2026-06-25** — no ceiling; firmware rebuild cleared it; full 2.94 MB partition usable (387 KB deployed clean). See `Research/nf-interpreter-deploy-ceiling.md`.
- **`nanoFramework.Hardware.Esp32` I²S surface incomplete.** Audio (Phase 6) needs it; tracked as an upstream contribution alongside the QSPI work.
- ~~**WebRTC on ESP32-S3.**~~ **RESOLVED 2026-06-24** — watch-side WebRTC is live on the libpeer LostBeard fork (`spawnwear` branch), mutual-Ed25519 authenticated, hardware-verified. DTLS answerer-role blocker fixed (SipSorcery 10.0.7 / RTC 1.1.11). As-built reference: `Docs/transport.md`.
