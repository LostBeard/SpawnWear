# SpawnWear Working Notes

Living documentation - everything we learn while building this OS. The goal is for the repo to be self-contained: anyone landing here should be able to bring up the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch under nanoFramework without needing access to private knowledge or repeating dead-ends we already burned through.

When you discover something non-obvious - a chip quirk, a flashing gotcha, a register that is not in the datasheet, a USB re-enumeration trap - **document it here**, then commit and push. Notes are first-class artifacts, not scratch.

## Index

### Setup + tooling
- **[flashing.md](flashing.md)** - First-flash recipe with every gotcha (USB re-enumeration, COM port flips, the cosmetic E4000 errors, why `--masserase` is required on factory boards)
- **[build-environment.md](build-environment.md)** - Building custom nf-interpreter firmware on Windows: ESP-IDF v5.4.x setup, MSYSTEM / Python 3.11 vs 3.13 gotchas, cmake preset config files, build flow, flashing custom builds

### Reverse-engineered chip behavior
- **[co5300-quirks.md](co5300-quirks.md)** - AMOLED display driver: QSPI hybrid protocol, 2-pixel minimum writes, even-aligned address windows, init sequence, command table

### Design docs + reference patches (upstream contributions)
- **[qspi-display-driver-design.md](qspi-display-driver-design.md)** - End-to-end design for adding hybrid-QSPI display panel support to .NET nanoFramework. Covers managed descriptor extension, native bus binding, ESP-IDF quad-mode plumbing, custom firmware target. CO5300 lands as the first consumer.
- **[qspi-implementation/](qspi-implementation/)** - Reference patches + new files for the QSPI contribution (drop into nf-interpreter + nanoFramework.Graphics clones via `git apply` / direct copy). Self-contained so the work survives if vendor clones get blown away.

### Planned (will land as we hit them)

- `axp2101-driver-notes.md` - PMIC: charging, rails, IRQ multiplexing, PWR button via the IRQ output line on GPIO10
- `qmi8658-driver-notes.md` - 6-axis IMU: I²C protocol, INT pin on GPIO21, motion-wake config, step counter
- `pcf85063-driver-notes.md` - RTC: read/set time, alarms via INT pin on GPIO39, battery-backup behavior
- `ft3168-driver-notes.md` - Touch controller: register map, INT debouncing, multi-touch report format
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
