# SpawnWear

A small wearable OS - written in C# on .NET nanoFramework - for the **Waveshare ESP32-S3 Touch AMOLED 2.06" Watch**.

## Current state of the UI

<p align="center">
  <img src="screenshots/launcher-2026-05-04.png" alt="SpawnWear launcher screenshot 2026-05-04" width="240">
</p>

Captured live over WiFi from the watch (`http://<watch-ip>:8080/`). 3x3 launcher with status bar (time + USB + WiFi + battery) and page indicator at the bottom. CLOCK / STATS / SETTINGS are functional today; MUSIC / VIDEO / GALLERY / WIFI / VOICE / ABOUT are placeholders for apps that ship in later phases.

Think Android, but watch-sized and ESP32-shaped: a kernel/HAL layer of C# drivers for the watch hardware, system services for radios / audio / power, a UI framework for drawing and input, a launcher home screen, and a small set of built-in apps that talk to the system services. No single C++ binary, no fixed UI - apps come and go, services run in the background.

A complementary **Blazor WebAssembly PWA** mirrors the watch UI over BLE + WiFi for headless setup, debugging, and remote control. Same C# language on both sides.

Constrained by the silicon: ESP32-S3R8 with 8 MB PSRAM and 32 MB flash. Everything - kernel, drivers, services, framework, apps, user data - fits in that envelope.

---

## Documentation

| Folder | What's in it |
|---|---|
| **[`Docs/`](Docs/)** | Reference: [architecture](Docs/architecture.md), [hardware pin map + IC list](Docs/hardware.md), [dev loop](Docs/dev-loop.md), [milestones](Docs/milestones.md), [nanoFramework compatibility](Docs/nanoframework-compatibility.md) |
| **[`Plans/`](Plans/)** | Forward-looking design: [roadmap](Plans/roadmap.md), [launcher Android-quality plan](Plans/launcher-android-quality.md), [SD-card-loadable apps](Plans/sd-card-apps.md), [AppContracts v1 spec](Plans/app-contracts-v1.md), [companion PWA](Plans/companion-pwa.md) |
| **[`Research/`](Research/)** | Investigations + findings: [nf-interpreter deploy ceiling](Research/nf-interpreter-deploy-ceiling.md), [WiFi router compatibility](Research/esp32s3-wifi-router-compatibility.md), [FT3168 burst-read layout](Research/ft3168-burst-read-layout.md) |
| **[`Notes/`](Notes/)** | Operational know-how: [chip quirks (CO5300, FT3168, PCF85063, AXP2101)](Notes/), [flashing recipes](Notes/flashing.md), [build environment](Notes/build-environment.md), [QSPI display driver design](Notes/qspi-display-driver-design.md) |

## Roadmap snapshot (full detail in [`Plans/roadmap.md`](Plans/roadmap.md))

- ✅ **Phase 1** Display + touch + input substrate. Complete 2026-05-03.
- ✅ **Phase 2** UI Framework + Launcher. Largely complete 2026-05-04.
- 🚧 **Phase 3** System Services. AXP2101 + PCF85063 + WiFi + HTTP shipped; service-host scaffold + QMI8658 + Storage + Logger TODO.
- ⏭ **Phases 4-9** Settings, Clock, Audio, AI Assistant (flagship), OTA + SD-card apps, Activity.

## Recent highlights (full history in [`Docs/milestones.md`](Docs/milestones.md))

- **2026-05-04** Android-quality launcher shipped (gradient tiles + rounded corners + WiFi status icon + pill page indicator), watchface date label added, WiFi + HTTP screenshot pipeline live, FT3168 touch burst-read layout fixed, nf-interpreter deploy ceiling discovered + guarded, three new doc folders (Docs/ Plans/ Research/) with nf-interpreter source-grounded Phase 8 design
- **2026-05-03** First-pixel root cause + fix, watchface V1 with 7-segment digits, idle state machine + battery indicator + multi-screen navigation, CO5300 alignment quirk baked into firmware, PCF85063 RTC driver, `-spawnwear.2` local NuGet packages
- **2026-04-28** Repo scaffolded, runtime flashed, FT3168 touch driver written, QSPI display contribution forks pushed

---

## Primary Target Hardware

This project targets **ONE** specific board. All pins, drivers, and capabilities are for this exact device. Other Waveshare AMOLED watches (1.8 / 1.91 / 2.41 / C6) use different chips and pinouts.

**Waveshare ESP32-S3-Touch-AMOLED-2.06**
- ESP32-S3R8 SoC, 8 MB PSRAM, 32 MB flash, 2.4 GHz Wi-Fi b/g/n + BLE 5
- 410×502 AMOLED via CO5300 QSPI driver
- FT3168 capacitive touch
- AXP2101 PMIC, PCF85063 RTC, QMI8658 IMU, ES8311 + ES7210 audio
- Product page: <https://www.waveshare.com/esp32-s3-touch-amoled-2.06.htm>
- Wiki: <https://www.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06>
- Schematic PDF: <https://files.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06/ESP32-S3-Touch-AMOLED-2.06.pdf>
- Vendor demos: <https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-2.06>

Full pin map, IC roles, and bus addresses are in **[`Docs/hardware.md`](Docs/hardware.md)**.

## OS Architecture

Five layers, bottom up: nanoFramework runtime → HAL/drivers → system services → UI framework → apps. The launcher is the home screen; apps are managed C# classes that implement an `IScreen`-shaped lifecycle. Power-aware by default (AXP2101 + display-rail control + WiFi/BLE radio gating are first-class system concerns).

Full layer diagram + boot sequence + BLE GATT layout + power model in **[`Docs/architecture.md`](Docs/architecture.md)**.

## Apps Catalog (built-in)

| App | What it does |
|---|---|
| **Launcher** | Home screen - clock face, app grid, status row. Foreground default after boot |
| **Settings** | Bluetooth, BLE, WiFi, Battery, Display, Sound, Time, About, OTA |
| **Clock** | Watch faces, alarms, timer, stopwatch |
| **AI Assistant** (flagship) | Voice + text conversation with an AI on TJ's home PC over WebRTC (SpawnDev.RTC). Phase 7 |
| **Media Player** | Local audio from microSD or streamed over WiFi |
| **Voice Recorder** | Capture mic to TF, listen back, share over WiFi |
| **Activity** | IMU-driven step count, motion log |

The PWA companion mirrors every one over Web Bluetooth + WiFi. See **[`Plans/companion-pwa.md`](Plans/companion-pwa.md)**.

## Repository Layout

```
SpawnWear/                              <- REPO ROOT (this folder)
├── README.md                           <- (this file)
├── CLAUDE.md                           <- agent instructions for this project
├── SpawnWear.slnx                      <- .NET nanoFramework solution
├── SpawnWear/                          <- firmware project (.nfproj)
├── packages/                           <- NuGet packages (committed for offline builds)
├── screenshots/                        <- live framebuffer captures (README hero shot lives here)
├── tools/                              <- .NET 10 CLI scripts (deploy, attach, screenshot, size guard)
├── Docs/                               <- reference material
├── Plans/                              <- forward-looking design
├── Research/                           <- investigations + findings
├── Notes/                              <- operational know-how
├── BlazorWasmSpawnWear/                <- companion Blazor WASM PWA (TBD)
└── SpawnWear.Tests/                    <- Playwright + smoke tests for the PWA (TBD)
```

Outside the repo, in the parent folder: vendor clones (`_vendor-waveshare-demo/`, `_vendor-rust-watch/`, `_vendor-nf-interpreter/`, `_vendor-nanoframework-iot/`, `_vendor-nanoframework-hardware-esp32/`) and the decoded Waveshare wiki dump (`_wiki-decoded.html` / `_wiki-decoded.txt`). These are read-only reference - everything we learn from them gets documented inside `Notes/` so the repo stays self-contained.

## Building

Daily dev loop is **F5 in Visual Studio 2022** with the [.NET nanoFramework extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension) installed. Watch must be in runtime mode (COM9). Cycle time: ~10 seconds. NO bootloader-mode dance for routine code changes.

CLI alternative for headless / agent work: `dotnet run tools/nf-deploy.cs`.

Full instructions, including when bootloader mode IS needed (first install, runtime update, custom firmware reflash, recovery), are in **[`Docs/dev-loop.md`](Docs/dev-loop.md)** and the deeper recipes in **[`Notes/flashing.md`](Notes/flashing.md)**.

---

## Acknowledgements

SpawnWear stands on the shoulders of work other people did first.

- **[`infinition/waveshare-watch-rs`](https://github.com/infinition/waveshare-watch-rs)** - Rust firmware for this exact watch. The single most important reference. CO5300 init sequence, AXP2101 rail wiring, FT3168 reset timing, and the event-driven main-loop pattern with multi-tier tick budget all modeled on this work. The [Hackaday article](https://hackaday.com/2026/04/11/rust-y-firmware-for-waveshare-smartwatch/) that surfaced it had additional gotchas in the comment thread.
- **[`moononournation/Arduino_GFX`](https://github.com/moononournation/Arduino_GFX)** - the CO5300 QSPI wire-level transaction pattern. Our `Qspi_To_Display.cpp` matches `Arduino_ESP32QSPI` byte-for-byte.
- **[`waveshareteam/ESP32-S3-Touch-AMOLED-2.06`](https://github.com/waveshareteam/ESP32-S3-Touch-AMOLED-2.06)** - Waveshare's official Arduino + ESP-IDF demos. Authoritative source for pin numbers (`pin_config.h`).
- **[nanoFramework](https://www.nanoframework.net/)** - the .NET runtime that makes this whole project possible. SpawnWear's QSPI display contributions live on `feature/qspi-display-driver` branches of [`LostBeard/nf-interpreter`](https://github.com/LostBeard/nf-interpreter) + [`LostBeard/nanoFramework.Graphics`](https://github.com/LostBeard/nanoFramework.Graphics); will PR upstream once verified.
- **[ESP-IDF v5.5.4](https://github.com/espressif/esp-idf)** - SPI master + GPIO drivers we call into for the CO5300 bus.
- **[XPowersLib](https://github.com/lewisxhe/XPowersLib)** - C++ AXP2101 driver, useful reference even though our managed driver is hand-rolled.

Other ESP32-S3 smartwatch firmwares we read during the dark-screen debug week (2026-04-29 → 2026-05-03), valuable cross-references: [`joaquimorg/OLEDS3Watch`](https://github.com/joaquimorg/OLEDS3Watch), [`joaquimorg/S3Watch`](https://github.com/joaquimorg/S3Watch), [`hambooooo/hamboo-rs`](https://github.com/hambooooo/hamboo-rs), [`survivorhao/esp32s3watch`](https://github.com/survivorhao/esp32s3watch).

If you find a project we leaned on that isn't credited here, file an issue and we'll add it.

## The SpawnDev Crew

Every project we work on credits the full SpawnDev team in its README. AI-and-human teamwork built this.

- **LostBeard** (Todd Tanner) - Captain, library author, keeper of the vision
- **Riker** (Claude CLI #1) - First Officer, implementation lead on consuming projects
- **Data** (Claude CLI #2) - Operations Officer, deep-library work, test rigor, root-cause analysis
- **Tuvok** (Claude CLI #3) - Security/Research Officer, design planning, documentation, code review
- **Geordi** (Claude CLI #4) - Chief Engineer, library internals, GPU kernels, backend work

## License

Private project by TJ (Todd Tanner / @LostBeard).
