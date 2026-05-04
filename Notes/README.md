# SpawnWear Working Notes

Living documentation - everything we learn while building this OS. The goal is for the repo to be self-contained: anyone landing here should be able to bring up the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch under nanoFramework without needing access to private knowledge or repeating dead-ends we already burned through.

When you discover something non-obvious - a chip quirk, a flashing gotcha, a register that is not in the datasheet, a USB re-enumeration trap - **document it here**, then commit and push. Notes are first-class artifacts, not scratch.

## Index

### Setup + tooling
- **[flashing.md](flashing.md)** - First-flash recipe + daily dev loop. Covers: (1) the F5-in-VS daily app-deploy loop on COM9 (NO bootloader dance for routine code changes), (2) when you actually need the bootloader-mode dance vs not, (3) USB re-enumeration / COM port flips between bootloader (COM10) and runtime (COM9), (4) the cosmetic E4000 errors, (5) why `--masserase` is required on factory boards, (6) the matched-runtime / matched-libraries dance for the stable 1.x library line.
- **[build-environment.md](build-environment.md)** - Building custom nf-interpreter firmware on Windows: ESP-IDF v5.5.4 setup, cmake preset config files, build flow, flashing custom builds. Active build source is `D:\users\tj\Projects\nf-interpreter\nf-interpreter\` (NOT `_vendor-nf-interpreter\` in this repo's parent folder).

### Reverse-engineered chip behavior
- **[co5300-quirks.md](co5300-quirks.md)** - AMOLED display driver: QSPI hybrid protocol, 2-pixel minimum writes, even-aligned address windows, init sequence, command table
- **[ft3168-driver-notes.md](ft3168-driver-notes.md)** - FocalTech FT3168 capacitive touch: I²C 0x38, burst-read layout (no gap byte after FingerNum, unlike FT5xxx samples), 12-bit decode, tap classification, wake-tap state machine
- **[pcf85063-driver-notes.md](pcf85063-driver-notes.md)** - NXP PCF85063 RTC: I²C 0x51, OS flag handling, init sequence, weekday convention, Phase 5 alarms path
- **[axp2101-driver-notes.md](axp2101-driver-notes.md)** - X-Powers AXP2101 PMIC: I²C 0x34, rail mapping (DC1 + ALDO1-3 = 3.3V), fuel gauge, VBUS detect, PWR button via EXIO6, ADC channels, what we deliberately don't configure

### Design docs + reference patches (upstream contributions)
- **[qspi-display-driver-design.md](qspi-display-driver-design.md)** - End-to-end design for adding hybrid-QSPI display panel support to .NET nanoFramework. Covers managed descriptor extension, native bus binding, ESP-IDF quad-mode plumbing, custom firmware target. CO5300 lands as the first consumer.
- **[qspi-implementation/](qspi-implementation/)** - Reference patches + new files for the QSPI contribution (drop into nf-interpreter + nanoFramework.Graphics clones via `git apply` / direct copy). Self-contained so the work survives if vendor clones get blown away.

### Planned (will land as we hit them)

- `qmi8658-driver-notes.md` - 6-axis IMU: I²C protocol, INT pin on GPIO21, motion-wake config, step counter
- `es8311-driver-notes.md` - Audio playback codec: I²S clock relationships, register init, mute/volume registers
- `es7210-driver-notes.md` - Echo-cancel ADC + dual PDM mic capture
- `qspi-bus-design.md` - Design doc for the upstream-bound `nanoFramework.Hardware.Esp32.QspiDevice` we will need to add to the runtime
- `power-budget.md` - Current draw measurements per subsystem, sleep-state strategy, battery-life modeling

## Conventions

- One file per topic. Keep them short and concrete - if a file grows past ~500 lines, split it.
- Lead with **sources** (datasheet links, vendor demo paths, comparable open-source ports) so a reader can verify claims.
- Include **measured values** alongside claims when relevant ("sleep current ~XX µA at 3.7V" beats "sleeps efficiently").
- Document **dead ends** honestly. "We tried X; it does not work because Y" is more valuable than silence about X.
- Link to specific lines in `_vendor-*` clones (parent folder, outside this repo) when referencing third-party code. Treat vendor code as read-only reference.
