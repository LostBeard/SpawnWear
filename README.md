# SpawnWear

.NET nanoFramework firmware (C#) for the **Waveshare ESP32-S3 Touch AMOLED 2.06" Watch**, paired with a Blazor WebAssembly PWA companion that provisions and controls it over BLE.

C# on the watch. C# in the browser. Same language end-to-end.

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

## What This Project Builds

```
┌──────────────────────────────┐         BLE          ┌──────────────────────────┐
│  SpawnWear (nanoFramework)   │◄────────────────────►│  Blazor WASM PWA          │
│  on ESP32-S3-AMOLED-2.06     │                      │  (browser / installable)  │
│                              │                      │                           │
│  • BLE GATT server           │                      │  • Web Bluetooth          │
│  • WiFi client + AP          │                      │  • SpawnDev.BlazorJS      │
│  • OTA updates               │                      │  • Device dashboard       │
│  • AXP2101 battery / charge  │                      │  • Live IMU / RTC view    │
│  • QMI8658 IMU notify        │                      │  • WiFi provisioning UI   │
│  • PCF85063 RTC sync         │                      │  • OTA trigger            │
│  • Debug log over BLE notify │                      │                           │
│                              │                      │                           │
└──────────────┬───────────────┘                      └──────────────┬────────────┘
               │ WiFi (after BLE config)                             │
               ▼                                                     │
        ┌──────────────────┐         HTTP / WebRTC                   │
        │  HTTP server     │◄────────────────────────────────────────┘
        │  WebRTC peer     │
        └──────────────────┘
```

BLE is the **provisioning + control** plane (low bandwidth, always available, no network needed). WiFi is the **bandwidth** plane (OTA, HTTP, eventually WebRTC video / audio via SpawnDev.RTC).

---

## Roadmap

### Phase 1 — Boot + BLE foundation
- [x] Project scaffolding (nanoFramework solution)
- [ ] BLE GATT server: Device Info + WiFi config + Debug console (mirrors NanoFrameTest1 layout, custom SpawnWear UUIDs)
- [ ] Companion Blazor WASM PWA scaffolded with SpawnDev.BlazorJS
- [ ] Round-trip BLE message: log line ESP32 → browser; command browser → ESP32

### Phase 2 — Power + sensors
- [ ] AXP2101 driver: battery V / I / SOC, charge state, USB-VBUS detect, PWR button via EXIO6
- [ ] PCF85063 RTC driver: read / set time, periodic sync from browser-supplied timestamp over BLE
- [ ] QMI8658 IMU driver: enable accel + gyro, stream samples over BLE notify

### Phase 3 — WiFi + OTA
- [ ] WiFi client provisioning (mirrors NanoFrameTest1)
- [ ] WiFi soft-AP fallback (when no station credentials)
- [ ] Stored credentials in flash, auto-reconnect on boot
- [ ] OTA pull triggered over BLE → ESP32 fetches firmware over WiFi

### Phase 4 — Debug console
- [ ] BLE notify characteristic streams `Debug.WriteLine` to the PWA
- [ ] Command input characteristic from PWA → ESP32 (REPL-style)

### Phase 5 — Display
- [ ] CO5300 QSPI driver in C# (no nanoFramework upstream support today — needs a custom data-bus path; verify whether `nanoFramework.Hardware.Esp32` exposes anything usable)
- [ ] If C# QSPI path is not viable, document the gap and ship Phases 1-4 + 6 first; revisit display when there's a clear technical path

### Phase 6 — Audio
- [ ] ES8311 + I²S playback (depends on `nanoFramework.Hardware.Esp32` I²S surface)
- [ ] ES7210 + dual PDM mic capture
- [ ] Wake-word / voice idea is downstream of capture working

### Phase 7 — WebRTC peer
- [ ] Use SpawnDev.RTC + signaling over the companion PWA to make the watch a video/audio peer
- [ ] Camera-less so video is stub-only; audio path is the real target

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
