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

## Installing a second ESP-IDF version side-by-side

When `main` of nf-interpreter advances ahead of your installed ESP-IDF (we hit this with v5.5.4 vs v5.4.1), you can install the newer ESP-IDF without removing the older one. The Espressif tools layout supports multiple frameworks under one tools tree.

```batch
@echo off
set MSYSTEM=
set MSYS=
set "IDF_TOOLS_PATH=C:\Espressif"

cd /d C:\Espressif\frameworks
git clone -b v5.5.4 --depth 1 --recurse-submodules --shallow-submodules https://github.com/espressif/esp-idf.git esp-idf-v5.5.4

cd /d C:\Espressif\frameworks\esp-idf-v5.5.4
call C:\Espressif\frameworks\esp-idf-v5.5.4\install.bat all
```

`install.bat all` downloads the matching toolchain (xtensa-esp-elf, riscv32-esp-elf, openocd, etc.), creates a Python venv (`C:\Espressif\python_env\idf5.5_py3.13_env\` because the installer picks up your system Python 3.13.x), and pins the Python deps to ESP-IDF's constraints file.

The clone is heavy (1+ GB) and the toolchain download adds another ~1 GB; expect 10-30 minutes total.

After install, point nf-interpreter's user config at the new framework path:

```json
// config/user-tools-repos.json
"ESP32_IDF_PATH": "C:/Espressif/frameworks/esp-idf-v5.5.4",
```

### Gotcha: install.bat may install Python deps at versions that don't match the constraint file

After we ran `install.bat all` the first time, `export.bat` failed with:

```
* Checking python dependencies ... FAILED
Requirement 'click<8.2,>=7.0' was not met. Installed version: 8.3.1
```

The first install pulled `click 8.3.1` (current latest), but ESP-IDF v5.5.4's `espidf.constraints.v5.5.txt` pins `click<8.2`. **Re-running `install.bat all` fixes this** - it sees the installed-but-out-of-spec packages and downgrades them to match.

If `install.bat` is reluctant, manually pin the offending package:

```batch
call C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat
python -m pip install "click<8.2"
```

### Gotcha: install.bat may fail to install all venv packages even when it exits 0

After our install + click-fix dance, `export.bat` was STILL failing — this time with `markdown_it_py`, `colorama`, `pyyaml`, `pyserial` missing from the venv. The venv was incomplete despite `install.bat all` reporting success.

Fix: force-install the full requirements file directly into the venv:

```batch
"C:\Espressif\python_env\idf5.5_py3.13_env\Scripts\python.exe" -m pip install ^
  -r "C:\Espressif\frameworks\esp-idf-v5.5.4\tools\requirements\requirements.core.txt" ^
  -c "C:\Espressif\espidf.constraints.v5.5.txt"
```

The constraint file (`-c`) keeps versions pinned to ESP-IDF's spec so we don't re-introduce the click 8.3.1 problem. After this, `export.bat` activates cleanly and `cmake --preset ESP32_S3_BLE` finds its toolchain.

### Gotcha: Python user-site-packages shadows the IDF venv

Even after `install.bat` correctly downgrades `click` to 8.1.8 inside the IDF venv, `export.bat` may STILL fail with the same `click 8.3.1` error. The reason: Python's `user-site-packages` directory at `C:\Users\<you>\AppData\Roaming\Python\Python313\site-packages` is searched BEFORE the venv's site-packages, and any global pip install (`pip install --user click`) puts a different version there that shadows the venv's pinned copy.

Fix: set `PYTHONNOUSERSITE=1` before invoking `export.bat` in any wrapper script:

```batch
@echo off
set PYTHONNOUSERSITE=1
call C:\Espressif\frameworks\esp-idf-v5.5.4\export.bat
```

This tells Python to ignore user-site-packages entirely, so the venv's exact pinned versions win. Verify with:

```batch
"C:\Espressif\python_env\idf5.5_py3.13_env\Scripts\python.exe" -m pip show click
```

The version reported there is what the venv has; if `export.bat` is reading something different, user-site-packages is the culprit.

## ESP-IDF version pinning

nf-interpreter ties itself to a specific ESP-IDF version via `set(ESP32_IDF_TAG "X.Y.Z" ...)` near the top of `targets/ESP32/CMakeLists.txt`. Each commit on `main` targets exactly one version.

| nf-interpreter commit / range | ESP-IDF version |
|---|---|
| `main` (current at 2026-04-28) - commit `b9a29ca` | **5.5.4** |
| commit `463e6ee9` ("Update IDF v5.5.4") and ahead | 5.5.4 |
| commit `f0c7f761` ("Migrate to v5.5.3") to before `463e6ee9` | 5.5.3 |
| commit `53be3026` ("Update to IDF 5.4.2") to before `f0c7f761` | **5.4.2** |
| commit `4e446673` to before `53be3026` | 5.2.3 |
| earlier | 5.1.x and below |

If your installed ESP-IDF doesn't match what `main` wants, you have two options:

1. **Install the matching ESP-IDF**: re-run the Espressif Tools Installer with the right version, or `git fetch && git checkout v5.5.4` inside the existing `C:\Espressif\frameworks\esp-idf-v5.4.1\` clone (and re-run `install.bat` to refresh the bundled tools + Python venv).
2. **Check out an older nf-interpreter commit that matches your ESP-IDF**: `cd _vendor-nf-interpreter && git checkout 53be3026` (the IDF 5.4.2 era). Cleanly recovers without installing more software. Apply your own changes on top, rebase to `main` later when you upgrade IDF.

### Symptom of a mismatch

cmake configures successfully but `cmake --build` enters a perpetual reconfig loop:

```
[0/1] Re-running CMake...
... configuring (12-15s) ...
[0/2] Re-running CMake...
... configuring (12-15s) ...
[0/3] Re-running CMake...
...
```

Each pass takes ~13 s; we observed 40+ passes in 9 minutes with no actual compilation step ever starting. The signature in the configure output is a line like `ESP32 IDF v5.5.4 source from: C:/Espressif/frameworks/esp-idf-v5.4.1` — nf detects a mismatch (it wants v5.5.4 but the path is v5.4.1) and keeps trying to reconcile.

Fix is one of the two options above. SpawnWear takes option 2 currently (build at commit `53be3026`, IDF 5.4.x era). Final upstream contribution will be rebased onto whatever `main` wants at PR time.

## Known warnings

- **CMAKE_OBJECT_PATH_MAX warning** on Windows: cmake warns that some intermediate object paths exceed 250 characters and the build "may not work correctly". In practice the build completes, but if it fails on a long-path link error, move the entire `_vendor-nf-interpreter` clone to a shorter root (e.g. `C:\nf-interpreter\`).

## Files this build environment touches outside the repo

- `_vendor-nf-interpreter/config/user-tools-repos.json` (created here, machine-specific)
- `_vendor-nf-interpreter/config/user-prefs.json` (created here, machine-specific)
- `_vendor-nf-interpreter/build/` (cmake output, gitignored upstream)

When we eventually fork nf-interpreter for the SpawnWear QSPI contribution, those config files stay machine-specific and never get committed.
