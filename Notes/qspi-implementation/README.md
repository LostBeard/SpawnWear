# QSPI Display Driver Implementation - Reference Patches

The actual source files for the upstream contribution to nanoFramework, captured here as a self-contained reference. Two repos get touched:

1. **`nf-interpreter`** (the runtime — native C++) - one new file + one patched header
2. **`nanoFramework.Graphics`** (the managed driver library) - two new files + one patched class

Each file in this folder is either a unified diff to apply with `git apply` or a complete new source file to drop in. They're preserved here in the SpawnWear repo as a self-contained set in case the upstream forks change shape.

## Live forks

Working forks with the QSPI changes already applied to a feature branch:

- **<https://github.com/LostBeard/nanoFramework.Graphics/tree/feature/qspi-display-driver>** — `DisplayBusType.cs` enum, `GraphicDriver.cs` extension, full `Co5300` managed driver project.
- **<https://github.com/LostBeard/nf-interpreter/tree/feature/qspi-display-driver>** — `Qspi_To_Display.cpp` native runtime side, `DisplayInterface.h` header extension.

The patches in this folder mirror what's on those branches; either is a valid starting point. When the implementation is verified working end-to-end, we open draft PRs from the LostBeard forks against `nanoframework/main`.

## Apply order

To layer these onto a clean clone of `nf-interpreter` (commit `53be3026` in the IDF 5.4.x era, or rebased onto current main):

```bash
cd /path/to/nf-interpreter
git apply /path/to/SpawnWear/Notes/qspi-implementation/01-DisplayInterface.h.patch
cp /path/to/SpawnWear/Notes/qspi-implementation/02-Qspi_To_Display.cpp \
   src/nanoFramework.Graphics/Graphics/Displays/Qspi_To_Display.cpp
```

And onto a clean clone of `nanoFramework.Graphics`:

```bash
cd /path/to/nanoFramework.Graphics
git apply /path/to/SpawnWear/Notes/qspi-implementation/03-GraphicDriver.cs.patch
cp /path/to/SpawnWear/Notes/qspi-implementation/04-DisplayBusType.cs \
   nanoFramework.Graphics/Primitive/DisplayBusType.cs
mkdir -p ManagedDrivers/Co5300
cp /path/to/SpawnWear/Notes/qspi-implementation/05-Co5300.cs \
   ManagedDrivers/Co5300/Co5300.cs
```

(The `Co5300/nanoFramework.Graphics.Co5300.nfproj` + `packages.config` files alongside the `.cs` are mechanical copies of the existing `Gc9A01/` siblings with the chip name swapped — generate at PR time.)

## What each file does

| File | Where it lands | Purpose |
|---|---|---|
| `01-DisplayInterface.h.patch` | `nf-interpreter/src/nanoFramework.Graphics/Graphics/Displays/DisplayInterface.h` | Adds the `Qspi` config variant in the union (spiBus/cs/reset/backlight) and 4 new descriptor fields: `BusType`, `QspiRegisterWriteCommand`, `QspiMemoryWriteCommand`, `QspiMemoryWriteAddress`. |
| `02-Qspi_To_Display.cpp` | `nf-interpreter/src/nanoFramework.Graphics/Graphics/Displays/Qspi_To_Display.cpp` (NEW) | Parallel to `Spi_To_Display.cpp`. Implements `g_DisplayInterface` against ESP-IDF's half-duplex SPI with quad-mode data phase, ping-pong DMA buffers, CS-keep-active for memory-write streams. The runtime selects this file at build time via `CONFIG_NF_FEATURE_USE_QSPI_DISPLAY_DRIVER`. |
| `03-GraphicDriver.cs.patch` | `nanoFramework.Graphics/nanoFramework.Graphics/Primitive/GraphicDriver.cs` | Extends the descriptor with `BusType`, `QspiRegisterWriteCommand`, `QspiMemoryWriteCommand`, `QspiMemoryWriteAddress`. Backward-compatible: existing descriptors (Gc9A01, Ili9341, ...) get default values that mean "standard SPI, no DC pin needed", so they still work unchanged. |
| `04-DisplayBusType.cs` | `nanoFramework.Graphics/nanoFramework.Graphics/Primitive/DisplayBusType.cs` (NEW) | The two-value enum: `Spi` (default) and `Qspi`. |
| `05-Co5300.cs` | `nanoFramework.Graphics/ManagedDrivers/Co5300/Co5300.cs` (NEW) | The first consumer descriptor — full CO5300 init sequence, RGB565 pixel format, `BusType=Qspi`, the three QSPI translation bytes, default 410x502 panel size. |

## Outstanding integration work (not in these patches)

When we move to the actual fork + PR:

- Add `Qspi_To_Display.cpp` to the `nf-interpreter/src/nanoFramework.Graphics/CMakeLists.txt` conditional on `CONFIG_NF_FEATURE_USE_QSPI_DISPLAY_DRIVER`.
- Add a new Kconfig option `NF_FEATURE_USE_QSPI_DISPLAY_DRIVER` to `nf-interpreter/Kconfig.graphics` mirroring the existing `NF_FEATURE_USE_SPI_DISPLAY_DRIVER`.
- Add a target-local pin map helper `Qspi_GetDisplayPins(spiHost, &clk, &cs, &d0, &d1, &d2, &d3)` in `targets/ESP32/ESP32_S3/target_system_device_spi_config.cpp` (or move the data into `DisplayInterfaceConfig.Qspi`).
- Create `targets/ESP32/defconfig/ESP32_S3_BLE_QSPI_defconfig` inheriting from `ESP32_S3_BLE_defconfig` with `CONFIG_NF_FEATURE_USE_QSPI_DISPLAY_DRIVER=y` and `CONFIG_NF_FEATURE_USE_SPI_DISPLAY_DRIVER=n`.
- Create the `Co5300/nanoFramework.Graphics.Co5300.nfproj` + `packages.config` files (mechanical copy of `Gc9A01/`).
- Wire SpawnWear's `Program.cs` to call `DisplayControl.Initialize(...)` with the new `Co5300.GraphicDriver` descriptor + the watch's QSPI pin set.

## Design context

Full architectural design lives in `Notes/qspi-display-driver-design.md`. CO5300 chip-specific quirks (2-pixel minimum writes, even-aligned address windows, init sequence) are in `Notes/co5300-quirks.md`.
