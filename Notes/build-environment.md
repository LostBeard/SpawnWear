# Building nf-interpreter (Custom nanoFramework Firmware)

To ship the QSPI display driver upstream, we build our own nf-interpreter image. This document captures the working build recipe on Windows so anyone can reproduce it.

## Prerequisites

| Component | Version | Where it comes from |
|---|---|---|
| ESP-IDF | **v5.4.x** (we use 5.4.1) | Espressif Tools Installer for Windows: <https://dl.espressif.com/dl/esp-idf/> |
| Bundled Python | 3.11 (matches the venv ESP-IDF creates) | Espressif installer drops this at `C:\Espressif\tools\idf-python\3.11.2\` |
| cmake | 3.30+ | Bundled with ESP-IDF at `C:\Espressif\tools\cmake\3.30.2\` |
| Ninja | latest | Bundled with ESP-IDF |
| Xtensa toolchain | esp32s3-elf gcc | Bundled with ESP-IDF |
| nf-interpreter source | <https://github.com/nanoframework/nf-interpreter> | Cloned with submodules - see below |

System Python (Python 3.13 from the official installer) is NOT used for the build - the bundled 3.11 wins because the ESP-IDF Python venv only matches that version.

## One-time setup

### 1. Install ESP-IDF (if not already there)

Run the Espressif Tools Installer with ESP-IDF v5.4.x selected. Default install location is `C:\Espressif\`. Confirms when done:

```
C:\Espressif\frameworks\esp-idf-v5.4.1\          <- ESP-IDF source
C:\Espressif\tools\cmake\3.30.2\bin\cmake.exe    <- cmake
C:\Espressif\tools\idf-python\3.11.2\python.exe  <- bundled Python
C:\Espressif\python_env\idf5.4_py3.11_env\       <- IDF Python venv
C:\Espressif\frameworks\esp-idf-v5.4.1\export.bat <- env activation script
```

### 2. Clone nf-interpreter with submodules

The `targets-community` directory is a git submodule and must be initialized for cmake's preset loader to succeed (otherwise it errors with `File not found: targets-community/CMakePresets.json`).

```bash
git clone https://github.com/nanoframework/nf-interpreter.git _vendor-nf-interpreter
cd _vendor-nf-interpreter
git submodule update --init --depth 1 targets-community
```

A `--depth 1` shallow clone is fine for the working tree (~150 MB cloned, ~600 MB after submodule init).

### 3. Create the user config files

nf-interpreter's CMake presets `inherit` user-supplied configs that aren't in the repo. Copy templates and fill them in.

Create `config/user-tools-repos.json`:

```json
{
    "version": 4,
    "configurePresets": [
        {
            "name": "user-tools-repos",
            "description": "ESP32-only build paths.",
            "hidden": true,
            "cacheVariables": {
                "ESP32_IDF_PATH": "C:/Espressif/frameworks/esp-idf-v5.4.1",
                "TOOL_HEX2DFU_PREFIX": null,
                "TOOL_SRECORD_PREFIX": null,
                "CHIBIOS_SOURCE_FOLDER": null,
                "FREERTOS_SOURCE_FOLDER": null,
                "CHIBIOS_CONTRIB_SOURCE": null,
                "CHIBIOS_HAL_SOURCE": null,
                "STM32_HAL_DRIVER_SOURCE": null,
                "STM32_CMSIS_DEVICE_SOURCE": null,
                "STM32_CMSIS_CORE_SOURCE": null,
                "LWIP_SOURCE": null,
                "MBEDTLS_SOURCE": null,
                "FATFS_SOURCE": null,
                "LITTLEFS_SOURCE": null,
                "TI_SL_CC32xx_SDK_SOURCE": null,
                "TI_SL_CC13xx_26xx_SDK_SOURCE": null,
                "TI_XDCTOOLS_SOURCE": null,
                "TI_SYSCONFIG_SOURCE": null,
                "THREADX_SOURCE_FOLDER": null,
                "NETXDUO_SOURCE_FOLDER": null
            }
        }
    ]
}
```

Note: the template ships as `user-tools-repos.TEMPLATE.json` and names the preset `user-tools-repos-local`. The ESP32 target presets inherit from `user-tools-repos` (no `-local` suffix), so we name ours that way.

Create `config/user-prefs.json`:

```json
{
    "version": 4,
    "configurePresets": [
        {
            "name": "user-prefs",
            "description": "SpawnWear preferences.",
            "hidden": true,
            "cacheVariables": {
                "CMAKE_BUILD_TYPE": "Release",
                "BUILD_VERSION": "1.16.0.5631",
                "BUILD_VERBOSE": "OFF"
            }
        }
    ],
    "buildPresets": [
        {
            "cleanFirst": false,
            "configuration": "Release",
            "hidden": true,
            "name": "base-user",
            "verbose": false
        }
    ]
}
```

Note: `BUILD_VERSION` must be a pure numeric `MAJOR.MINOR.PATCH.BUILD` (cmake `project(... VERSION ...)` rejects suffixes like `-spawnwear`). To distinguish a SpawnWear build from upstream, encode the suffix in the build number itself - e.g. `1.16.0.5631` (upstream is `1.16.0.563`, SpawnWear's first build is `5631`).

## Activating the build environment from bash

Two gotchas conspire on Git Bash:

1. **`export.bat` refuses to run if `$MSYSTEM` is set.** The first lines of the script bail out with `This .bat file is for Windows CMD.EXE shell only.` if it detects the MSYS env var. Workaround: clear `MSYSTEM` (and `MSYS`) at the top of any wrapper.
2. **System Python 3.13 shadows the bundled 3.11.** The Espressif venv is `idf5.4_py3.11_env`, the export script auto-detects via `python.exe --version` and goes looking for `idf5.4_py3.13_env` if it sees system 3.13. Workaround: prepend `C:\Espressif\tools\idf-python\3.11.2` to PATH before calling `export.bat`.

The wrapper script that handles both:

```batch
@echo off
set MSYSTEM=
set MSYS=
set "PATH=C:\Espressif\tools\idf-python\3.11.2;%PATH%"
call C:\Espressif\frameworks\esp-idf-v5.4.1\export.bat > %TEMP%\nf-export.log 2>&1
cd /d D:\users\tj\Projects\SpawnWear\_vendor-nf-interpreter
:: now do whatever cmake/idf.py command you need
```

Save as `_vendor-nf-interpreter/local-build.bat` (gitignored - machine-specific) or wherever convenient, and call it directly from bash via `"./local-build.bat"`. Bash invokes .bat files transparently on Windows.

## Build flow

### Configure (fast - ~25 seconds first time, instant after)

```bash
cmake --preset ESP32_S3_BLE
```

Output ends with:

```
-- Configuring done
-- Generating done
-- Build files have been written to: .../build
```

### Build (slow first time - tens of minutes; minutes for incremental)

```bash
cmake --build --preset ESP32_S3_BLE
```

Output appears in `build/`. Final firmware artifacts:

- `build/nanoCLR.bin` - the firmware blob to flash
- `build/<target>-flash.zip` - the package format `nanoff` understands

### Available cmake presets (current ESP32-S3 set)

```
AtomS3
ESP32_S3
ESP32_S3_ALL
ESP32_S3_ALL_UART
ESP32_S3_BLE
ESP32_S3_BLE_UART
```

Switch between them by changing `--preset <name>` on both `cmake --preset` and `cmake --build --preset`.

## Flashing a custom build

Once `build/nanoCLR.bin` exists, flash exactly like the official one. nanoff supports a local-file path:

```bash
nanoff --target ESP32_S3_BLE --serialport COMxx --update --masserase \
       --fwversion 1.16.0.5631 \
       --archivepath D:\users\tj\Projects\SpawnWear\_vendor-nf-interpreter\build\
```

(Recipe to be confirmed during first custom-build flash; alternative is to call esptool directly with the same partition layout.)

## Known warnings

- **CMAKE_OBJECT_PATH_MAX warning** on Windows: cmake warns that some intermediate object paths exceed 250 characters and the build "may not work correctly". In practice the build completes, but if it fails on a long-path link error, move the entire `_vendor-nf-interpreter` clone to a shorter root (e.g. `C:\nf-interpreter\`).

## Files this build environment touches outside the repo

- `_vendor-nf-interpreter/config/user-tools-repos.json` (created here, machine-specific)
- `_vendor-nf-interpreter/config/user-prefs.json` (created here, machine-specific)
- `_vendor-nf-interpreter/build/` (cmake output, gitignored upstream)

When we eventually fork nf-interpreter for the SpawnWear QSPI contribution, those config files stay machine-specific and never get committed.
