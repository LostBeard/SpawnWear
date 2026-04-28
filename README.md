# SpawnWear

A small wearable OS — written in C# on .NET nanoFramework — for the **Waveshare ESP32-S3 Touch AMOLED 2.06" Watch**.

Think Android, but watch-sized and ESP32-shaped: a kernel/HAL layer of C# drivers for the watch hardware, system services for radios / audio / power, a UI framework for drawing and input, a launcher home screen, and a small set of built-in apps (Settings, Clock, etc.) that talk to the system services. No single C++ binary, no single fixed UI — apps come and go, services run in the background.

It comes with a complementary **Blazor WebAssembly PWA** that mirrors the watch UI over BLE + WiFi for headless setup, debugging, and remote control. Same C# language on both sides.

Constrained by the silicon: ESP32-S3R8 with 8MB PSRAM and 32MB flash. Everything — kernel, drivers, services, framework, apps, user data — fits in that envelope.

---

## Primary Target Hardware

This project targets **ONE** specific board. All pins, drivers, and capabilities documented below are for this exact device. Do not generalize — other Waveshare AMOLED watches (1.8 / 1.91 / 2.41 / C6 variant) use different chips and pinouts.

**Waveshare ESP32-S3-Touch-AMOLED-2.06**
- Product page: <https://www.waveshare.com/esp32-s3-touch-amoled-2.06.htm>
- Wiki: <https://www.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06>
- Schematic PDF: <https://files.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06/ESP32-S3-Touch-AMOLED-2.06.pdf>
- Vendor demos (Arduino + ESP-IDF): <https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-2.06>
- Amazon (US, with battery): <https://www.amazon.com/gp/product/B0FJQZ7SBG>
- Amazon (US, no battery): <https://www.amazon.com/Waveshare-ESP32-S3-Development-Dual-core-Microphones/dp/B0FJFNXGNX>

### SoC

| Field | Value |
|---|---|
| Part | **ESP32-S3R8** (Espressif, embedded PSRAM variant) |
| CPU | Xtensa **LX7** dual-core, up to **240 MHz** |
| SRAM | 512 KB internal |
| ROM | 384 KB internal |
| PSRAM | **8 MB** octal, in-package |
| Flash | **32 MB** external (W25Q256-class) |
| Radio | 2.4 GHz Wi-Fi 802.11 b/g/n + **Bluetooth 5 LE** |
| USB | Native **USB-OTG** off the ESP32-S3 (USB-C connector) |
| Antenna | On-board SMD antenna |

### Display

| Field | Value |
|---|---|
| Panel | 2.06" AMOLED, capacitive touch |
| Resolution | **410 × 502** |
| Color depth | 16.7M (24-bit) |
| Driver IC | **CO5300** (QSPI, 80 MHz max) |
| Backlight | Software-controlled via CO5300 register `0x51` (0x00 dark → 0xFF bright) — no separate backlight pin |

### Touch

| Field | Value |
|---|---|
| Controller | **FT3168** self-capacitance (FocalTech) |
| Bus | I²C, address **0x38** |
| Speed | 10 kHz – 400 kHz |

### Sensors / On-board ICs

| IC | Role | Bus / Addr |
|---|---|---|
| **QMI8658** | 6-axis IMU (3-axis accel + 3-axis gyro), step-count, motion / gesture | I²C, addr **0x6B** (alt 0x6A) |
| **PCF85063** | Real-Time Clock, battery-backed via AXP2101 | I²C, addr **0x51** |
| **AXP2101** | Power Management IC — charging, multi-rail outputs, ADC for battery V/I/temp, **EXIO6** = PWR side button | I²C, addr **0x34** |
| **ES8311** | Audio codec (DAC + line-in ADC), drives speaker | I²C, addr **0x18** |
| **ES7210** | Echo-cancel ADC, drives dual PDM microphone array | I²C, addr **0x40** |
| Speaker | Onboard, driven through ES8311 + class-D amp (PA_EN on **GPIO46**) | — |
| Microphones | **Dual PDM array**, fed into ES7210 | — |
| TF / microSD | Slot, 4-bit SDMMC | dedicated GPIO (see pin map) |
| Buttons | **BOOT** (direct GPIO) + **PWR** (via AXP2101) | see pin map |
| Vibration | Not present on this SKU | — |

### Pin Map

Authoritative source: vendor `pin_config.h` (cloned to `_vendor-waveshare-demo/`) and the schematic PDF above.

#### AMOLED Display — QSPI (CO5300)
| Signal | GPIO |
|---|---|
| SDIO0 | **GPIO4** |
| SDIO1 | **GPIO5** |
| SDIO2 | **GPIO6** |
| SDIO3 | **GPIO7** |
| SCLK  | **GPIO11** |
| CS    | **GPIO12** |
| RESET | **GPIO8** |

#### I²C bus (shared by FT3168, QMI8658, PCF85063, AXP2101, ES8311, ES7210)
| Signal | GPIO |
|---|---|
| SDA   | **GPIO15** |
| SCL   | **GPIO14** |

#### Touch (FT3168) extra pins
| Signal | GPIO |
|---|---|
| INT   | **GPIO38** |
| RESET | **GPIO9**  |

#### TF / microSD card (SDMMC)
| Signal | GPIO |
|---|---|
| CLK   | **GPIO2**  |
| CMD   | **GPIO1**  |
| DATA  | **GPIO3**  |
| CS    | **GPIO17** |

#### Audio I²S (ES8311 playback / ES7210 record)
| Signal | GPIO |
|---|---|
| MCLK  | **GPIO16** |
| BCLK  | **GPIO41** |
| LRCLK / WS | **GPIO45** |
| DOUT (codec → speaker) | **GPIO40** |
| DIN  (mic → codec)     | **GPIO42** |
| PA enable (speaker amp) | **GPIO46** |

#### Buttons
| Button | Path | Notes |
|---|---|---|
| **BOOT** | **GPIO0** (direct, active LOW) | Hold during power-on → ROM download mode. During normal boot, used as user button — single / double / multi / long press |
| **PWR**  | **AXP2101 EXIO6** (over I²C, active HIGH) | Hold 6 s → power off. From off + on charger → click to power on. Don't hold > 6 s during normal use or device powers off |

#### USB
- USB-C, **native USB-OTG** off the ESP32-S3 (CDC + JTAG via the same port).
- Auto-download circuit on board — no manual reset/boot dance needed for normal flashing.

---

## OS Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Apps (managed C#, run in-process under the SpawnWear app host)              │
│  • Launcher (home / clock / app grid)    • Settings        • Clock           │
│  • AI Assistant (voice + text → home PC over WebRTC)                         │
│  • Media Player                          • Voice Recorder  • Activity (IMU)  │
│  • [user-installable later via OTA-style app payloads]                       │
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
│  .NET nanoFramework runtime                                                  │
│  • Wi-Fi stack (System.Device.Wifi)    • BLE stack (Device.Bluetooth)        │
│  • System.Net / IO / Threading         • Hardware.Esp32                      │
├──────────────────────────────────────────────────────────────────────────────┤
│  Espressif ESP32-S3 firmware (managed by nanoFramework, not SpawnWear)       │
└──────────────────────────────────────────────────────────────────────────────┘

                BLE (always-available)            WiFi (when configured)
                       │                                  │
                       ▼                                  ▼
        ┌────────────────────────────────────────────────────────┐
        │  Blazor WebAssembly PWA — companion / remote UI        │
        │  • Mirrors every built-in app over BLE + WiFi          │
        │  • Adds a comfortable laptop-grade keyboard for setup  │
        │  • Not required to use the watch                       │
        └────────────────────────────────────────────────────────┘
```

**The watch is the primary device.** Everything ships with a touchscreen UI on the AMOLED. The PWA is a complementary remote — convenient for first-time WiFi provisioning before the on-device keyboard exists, for live debugging from a laptop, and for showing off the watch's API surface from a browser. Nothing on the PWA is required to use the watch.

### What "OS-shaped" means here

- **Apps are not fixed.** The launcher hosts a list of apps; built-in ones are compiled into the firmware initially, but the long-term aim is OTA-installable app payloads (limited by what nanoFramework's assembly loader can do at runtime — likely an in-place re-flash of a partition slice, not true dynamic loading).
- **Services are background daemons,** consumed by apps via interfaces. Only one PMIC, one BLE stack, one display backlight — the system service owns it, apps ask politely.
- **Lifecycle is Android-flavored.** Apps have `OnCreate` / `OnResume` / `OnPause` / `OnDestroy`. The launcher decides what's foregrounded. Background services keep ticking through pause/resume.
- **Resource budgets are explicit.** PSRAM (8 MB), heap, flash slots, BLE MTU, WiFi airtime. Apps that hog get killed.
- **Power-aware by default.** AXP2101 + display-rail control + WiFi/BLE radio gating are first-class system concerns, not afterthoughts.

---

## Apps Catalog (built-in)

The watch ships with a small core set of first-party apps. They're listed here so the launcher's job is concrete and so the system services know who their consumers are.

| App | What it does | Primary services it uses |
|---|---|---|
| **Launcher** | Home screen — clock face, app grid, status row (battery / WiFi / BLE / time). Foreground default after boot. | UI Framework, Power, RTC, Storage |
| **Settings** | Bluetooth, BLE, WiFi, Battery, Display, Sound, Time, About, OTA. Each subsystem is one page. | All system services |
| **Clock** | Watch faces, alarms, timer, stopwatch. Several faces selectable. | RTC, UI Framework, Power |
| **AI Assistant** | **The flagship app.** Voice + text conversation with an AI running on TJ's home PC over WebRTC (SpawnDev.RTC). Push-to-talk button, live transcript on screen, TTS replies through the speaker, on-screen keyboard for typed messages, history scrollback. Works whenever the watch can reach the PC over WiFi (LAN or via signaling relay). | Audio (ES8311 playback + ES7210 PDM mic), WiFi, UI Framework, RTC (timestamps), Storage (history) |
| **Media Player** | Local audio playback from microSD or streamed over WiFi. Play / pause / next / volume on screen. | Audio (ES8311), Storage (TF), WiFi |
| **Voice Recorder** | Capture mic to a file on the microSD. Listen back, delete, share to PC over WiFi. | Audio (ES7210 PDM), Storage |
| **Activity** | IMU-driven step count, motion log, simple charts. | Sensors (QMI8658), RTC, Storage, UI Framework |

The PWA companion mirrors every one of these as a remote-UI page reachable via Web Bluetooth + WiFi, so a developer or anyone with the watch's IP can drive any app from a browser.

---

## Roadmap

The display is the user interface, so it leads. Plumbing (radios, sensors, audio, OTA) is still needed - a watch with no radios is a fancy clock - but each piece gets exposed THROUGH an app or Settings page, not as a substitute for one.

### Phase 1 — Display + touch + input (the UI substrate)
- [ ] CO5300 QSPI driver in C# — research nanoFramework's QSPI surface; if absent, contribute a managed QSPI bus + CO5300 panel driver upstream
- [ ] FT3168 touch I²C driver
- [ ] Frame-buffer + drawing primitives (probably sit on top of `nanoFramework.Graphics` if its surface fits the 410×502 panel; otherwise hand-roll)
- [ ] Touch + button input dispatcher feeding a UI message loop
- [ ] BOOT button polling on GPIO0 (single / double / long press dispatch)

### Phase 2 — UI Framework + Launcher
- [ ] Drawing primitives: text, rounded rects, gradients, icons, scrollable lists, keyboard
- [ ] Navigation stack + app lifecycle (`OnCreate` / `OnResume` / `OnPause` / `OnDestroy`)
- [ ] Theme + system widgets (status bar, dialog, toast, list view, slider, switch)
- [ ] **Launcher app**: clock face + app grid + status row (battery / WiFi / BLE / time)

### Phase 3 — System Services + power/sensors plumbing
- [x] Project scaffolding (nanoFramework solution, BLE GATT layout, gitignore, repo at github.com/LostBeard/SpawnWear)
- [ ] Service host: singletons, lifecycle, inter-service events
- [ ] AXP2101 driver: battery V / I / SOC, charge state, USB-VBUS detect, PWR button via EXIO6
- [ ] PCF85063 RTC driver: read / set time, weekday, alarms
- [ ] QMI8658 IMU driver: accel + gyro + step-count
- [ ] Storage service: TF/microSD mount + simple key-value store in internal flash for settings persistence
- [ ] Logger service: ring buffer + USB-CDC sink + BLE notify sink

### Phase 4 — Settings app
- [ ] Page: **Battery** — level, charging state, USB-VBUS, charge target slider
- [ ] Page: **Display** — brightness slider (CO5300 reg 0x51), sleep timeout, rotation
- [ ] Page: **Time / RTC** — read PCF85063, set fields, sync-from-NTP toggle
- [ ] Page: **About** — firmware version, MAC, IP, free heap, uptime
- [ ] Page: **WiFi** — toggle, SSID list, on-screen keyboard for password, current connection details
- [ ] Page: **Bluetooth** — radio toggle, paired devices, scan
- [ ] Page: **BLE** — GATT-server visibility toggle, advertised name editor

### Phase 5 — Clock app
- [ ] Multiple watch faces (analog, digital, complications)
- [ ] Alarms (RTC alarm interrupt → wake from low-power)
- [ ] Timer + stopwatch

### Phase 6 — Audio service + Voice Recorder + Media Player
- [ ] ES8311 playback driver (I²S) — depends on `nanoFramework.Hardware.Esp32` I²S surface
- [ ] ES7210 capture driver (PDM dual mic + echo cancel ADC)
- [ ] Audio service: shared pipeline, volume, mute, mic gain, format negotiation
- [ ] Page: Settings → **Sound** (volume / mic gain / test-tone / mic-level meter)
- [ ] **Voice Recorder app**: capture to TF, listen back, delete, share over WiFi
- [ ] **Media Player app**: play files from TF, basic transport controls; HTTP streaming if airtime allows

### Phase 7 — WebRTC service + AI Assistant app (flagship)
- [ ] WebRTC peer service: SpawnDev.RTC integration; signaling via the companion PWA or a small HTTP signaling relay; ICE / SDP plumbing
- [ ] **AI Assistant app**: push-to-talk button, on-screen keyboard for text, live transcript display, TTS playback through speaker, conversation history persisted to TF
- [ ] PC-side counterpart: a small Blazor / .NET host on TJ's PC that the watch dials, runs the assistant model, returns audio + text

### Phase 8 — OTA + app install
- [ ] OTA firmware update path (nanoFramework standard)
- [ ] Page: **About → Update** — pull URL field, "Check for update" button, download + reboot flow
- [ ] App install: ship apps as separate managed payloads where the runtime allows; otherwise treat "install an app" as "OTA the firmware with an updated app set"

### Phase 9 — Activity app + later
- [ ] **Activity app**: step count, daily totals, motion log
- [ ] User-contributed apps via the install path
- [ ] Polish, theming, watchface marketplace ideas

### Companion Blazor WASM PWA (parallel track, starts in Phase 4)
- [ ] Scaffolded with SpawnDev.BlazorJS
- [ ] Mirrors every Settings page over BLE (provisioning + diagnostics work even before the on-device keyboard is comfortable)
- [ ] Mirrors every built-in app (remote launcher)
- [ ] Live system log viewer over BLE notify
- [ ] PWA installable so it lives on a phone home screen

---

## Repository Layout

This README sits at the **git repo root** (`D:\users\tj\Projects\SpawnWear\SpawnWear\`). The parent folder (`D:\users\tj\Projects\SpawnWear\`) is project scratch — vendor clones, wiki dumps, and other agent reference material live there and are intentionally outside git.

```
SpawnWear/                              ← REPO ROOT (this folder)
├── README.md                           ← (this file)
├── CLAUDE.md                           ← agent instructions for this project
├── spawn-wear.md                       ← original feature wishlist
├── SpawnWear.slnx                      ← .NET nanoFramework solution
├── SpawnWear/                          ← firmware project (.nfproj)
├── packages/                           ← NuGet packages (committed for offline builds)
├── BlazorWasmSpawnWear/                ← companion Blazor WASM PWA (TBD)
└── SpawnWear.Tests/                    ← Playwright + smoke tests for the PWA (TBD)
```

Outside the repo, in the parent folder (`D:\users\tj\Projects\SpawnWear\`):

```
_vendor-waveshare-demo/                 ← upstream Arduino + ESP-IDF demos (cloned)
_wiki-decoded.{html,txt}                ← decoded copy of the Waveshare wiki page
ESP32-S3-Touch-AMOLED-2.06 - Waveshare Wiki.mhtml  ← raw archived wiki page
_extract-wiki.cs                        ← script that decoded the .mhtml
```

These reference files exist so pin numbers and IC behavior can be verified against the vendor's own working code without bloating the repo or violating their license.

---

## nanoFramework Compatibility Notes

These are the realities of running C# on this board today. None of them are blockers for Phases 1-4.

| Capability | Status | Notes |
|---|---|---|
| WiFi station + AP | **Supported** | `nanoFramework.System.Device.Wifi` |
| BLE GATT server | **Supported** | `nanoFramework.Device.Bluetooth` |
| OTA firmware update | **Supported** | nanoFramework has standard OTA (verify package API surface during Phase 3) |
| I²C device control (AXP2101 / QMI8658 / PCF85063 / FT3168) | **Supported** | `nanoFramework.Hardware.Esp32` + `System.Device.I2c` — drivers written by hand against the chip datasheets |
| PCF85063 RTC | **Supported (community driver)** | `nanoFramework.IoT.Device.Pcf85063` exists |
| QMI8658 IMU | **Hand-roll driver** | No upstream nanoFramework driver; protocol is plain I²C register reads |
| FT3168 touch | **Hand-roll driver** | Same — datasheet linked above |
| AXP2101 PMIC | **Hand-roll driver** | Datasheet linked; XPowersLib (C++) is a useful reference |
| AMOLED display via CO5300 QSPI | **Gap** | nanoFramework's display drivers are SPI, not QSPI. Either contribute a QSPI bus + CO5300 driver to nanoFramework, or document and defer |
| I²S audio (ES8311 / ES7210) | **Gap / partial** | nanoFramework I²S surface is limited. PDM mic capture is even more constrained. Phase 6 is a research item before promising delivery |
| USB-CDC for `Debug.WriteLine` | **Supported** | Native USB-OTG → CDC. Standard nanoFramework path |

---

## Building (Phase 0 status)

```bash
# Flash the nanoFramework runtime to the watch (one-time)
dotnet tool install -g nanoff
nanoff --target ESP32_S3_BLE --serialport COMx --update

# Build firmware (Visual Studio 2022 with the nanoFramework extension)
# (msbuild via CLI also works once the VS extension is installed)
```

Phase 1 deploy + first BLE round-trip is the next milestone — see roadmap.

---

## The SpawnDev Crew

Every project we work on credits the full SpawnDev team in its README. AI-and-human teamwork built this.

- **LostBeard** (Todd Tanner) — Captain, library author, keeper of the vision
- **Riker** (Claude CLI #1) — First Officer, implementation lead on consuming projects
- **Data** (Claude CLI #2) — Operations Officer, deep-library work, test rigor, root-cause analysis
- **Tuvok** (Claude CLI #3) — Security/Research Officer, design planning, documentation, code review
- **Geordi** (Claude CLI #4) — Chief Engineer, library internals, GPU kernels, backend work

## License

Private project by TJ (Todd Tanner / @LostBeard).
