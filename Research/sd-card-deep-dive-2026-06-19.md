# SD Card Deep Dive — 2026-06-19/20 (Riker)

Full record of the SD-card investigation on the Waveshare ESP32-S3-Touch-AMOLED-2.06
watch under .NET nanoFramework. Read this top-to-bottom before resuming.

---

## 2026-06-20 PART 2: nf has a SEPARATE issue (contact was only the STANDALONE's blocker)

After the reseat fixed the standalone, nf STILL timed out (0x107) on the same well-seated
card. Systematically, with GOOD contact:
- standalone (bare ESP-IDF): mounts. standalone + full BT/WiFi/coex/SW_COEXIST linked
  (coex_pre_init runs pre-app_main): **also mounts** -> BLE/coex modem stack EXONERATED.
- nf: times out. NOT contact, NOT power (managed AXP sets DC1+ALDO1 voltage+enable,
  0x90=0x01 readback), NOT WiFi-init order (SD-first/before StartWifi still fails), NOT the
  managed path (MountNative just calls Storage_MountMMC == the standalone's direct call).
- **Re-added the native pre-CLR SD probe to nf -> `[NATIVE-PROBE] result=263 (0x107)`.** It
  fails BEFORE the CLR, the receiver/USB-OTG task, and WiFi. So the cause is in the nf
  BUILD's pre-app_main init, NOT the runtime environment.

PM was RULED OUT: nf with PM=n + tickless=n STILL fails the pre-CLR probe (263). Next, a
clean diff of nf(fails) vs standalone+coex(passes) - both PM-off, good contact - leaves
these real deltas: **CPU freq (nf 240 / standalone 160)**, PSRAM speed (nf 80 / standalone
40), flash size (4/32MB), console (UART / USJ), TASK_WDT. Every standalone build that mounted
ran at **160 MHz**; every failing nf at **240 MHz** - and the earlier 240->160 test was
contact-confounded. Testing nf at 160 MHz (PSRAM left at 80 to isolate CPU freq). If it
mounts -> CPU/PLL freq affects the SDMMC source clock; then decide ship-at-160 vs a clock fix
to keep 240. If not -> revert CPU, test PSRAM 40 next.

## 2026-06-20 BREAKTHROUGH: the immediate blocker was a MARGINAL PHYSICAL SD CONTACT

After exhaustively re-testing on the standalone ESP-IDF app (the proven-good oracle), the
root cause of the *current* failures was **a marginal microSD card contact / seating**,
NOT software or config:

- The standalone mounted the card once (`ESP_OK`, 521 s uptime), then **timed out (0x107)
  on every boot afterwards** with no real config change.
- A retry loop proved it was **deterministic within a session** (69/69 fail), and an
  **ALDO1 rail power-cycle did NOT recover it** (failed 69× with the cycle active).
- The moment TJ **physically reseated the microSD card**, it began mounting and ran
  **100+ consecutive `ESP_OK` mounts, zero new failures.**
- Therefore the SD path (card, slot wiring, power, pins, SDMMC code, sequence) is all good;
  the card was simply not making reliable contact.

**Every config lead chased on 2026-06-20 was a red herring** (coex/`SW_COEXIST`, `BT_ENABLED`,
partition table, settle time, `gpio_reset_pin`, `i2c_driver_delete`). They were confounded
two ways: (a) every "failing" build also *regenerated* sdkconfig from a minimal
`sdkconfig.defaults` after `Remove-Item sdkconfig` (the regenerated config is byte-identical
to the working one except harmless USB-descriptor strings), and (b) the card contact was
intermittently bad underneath the whole comparison. Lesson: when an on-hardware result is
non-deterministic, test repeatability (retry loop / multiple cold boots) BEFORE attributing
a single-boot result to a config change.

**Implication for the original nf-interpreter failures:** they were very likely the same
marginal contact (nf does a single SD mount at boot; a bad-contact boot times out). The
nf-side ALDO2/3 power fix (ROOT CAUSE #1 below) is still correct and should be kept, but the
dominant blocker appears to have been physical. **STILL TO VALIDATE:** flash the nf runtime
with the card now seated well and confirm it mounts; and add robustness so a marginal contact
self-recovers (candidate: `SDMMC_SLOT_FLAG_INTERNAL_PULLUP`, and/or a mount retry loop).

Tools from this session (kept in `sdtest-espidf/`): `idf-env.ps1` (build-env wrapper),
`read_watch.py` (USJ console reader - needs a USB unplug/replug to read; see the memory
`feedback-watch-usj-console-needs-unplug-replug-to-read`).

## TL;DR

- **The SD card and the watch's SD hardware are PROVEN GOOD.** A bare standalone
  ESP-IDF app mounts the card over SDMMC and reads a real folder (`SYSTEM~1`) off it
  on this exact watch.
- **We found and FIXED a real first-cause bug:** nanoFramework's
  `Axp2101Driver.EnableDisplayRails()` was powering **ALDO2 + ALDO3**, which are
  unused on this watch (the vendor AXP demo labels them camera/PIR) and **back-feed /
  interfere with the microSD bus**, making SDMMC card-init time out. The vendor powers
  **only DC1 + ALDO1**. Fix applied (managed, kept).
- **One blocker remains and it is NOT solved:** even after matching the working
  standalone on *every* dimension, the nanoFramework build still times out
  (`ESP_ERR_TIMEOUT` / `0x107`) on SDMMC card-init. A **native probe running before
  the CLR even starts** also fails — so the cause is **something compiled into the
  nf-interpreter build whose static startup init breaks the SDMMC peripheral**, not a
  config setting and not the CLR runtime. Next step = component/build bisection.
- **Updating nf-interpreter will NOT fix it by itself:** we are ~2 months behind
  upstream `main` (base 2026-04-22, upstream now 2026-06-19) but **zero** of the 49
  intervening upstream commits touch SDMMC / SD / FileSystem / FATFS / ESP32-S3 /
  PSRAM. Still worth updating tomorrow (rebase the QSPI fork) in case something
  indirect helps, but do not expect it to resolve this.

---

## The error and what it means

- `Storage_MountMMC` -> `esp_vfs_fat_sdmmc_mount` returns **`ESP_ERR_TIMEOUT` (0x107)**
  at `sdmmc_init_ocr: send_op_cond` = the card never answers ACMD41 during card-init.
- This is the **400 kHz probe phase** (pre-filesystem), so it is NOT a FATFS / format /
  buffer / speed-of-data problem. The card simply isn't responding to the
  clock/commands.

---

## ROOT CAUSE #1 (FOUND + FIXED): AXP2101 ALDO2/ALDO3 interfere with the SD bus

- At AXP2101 POR every rail is ON. The old `EnableDisplayRails()` bit-OR'd ALDO1+2+3 on
  (`REG 0x90 |= 0x07`). ALDO2/ALDO3 are unused on this board and interfere with the SD
  lines -> SDMMC card-init times out.
- The vendor firmware (`waveshareteam` `01_AXP2101/port_axp2101.cpp`) enables **only
  DC1 + ALDO1** and explicitly disables ALDO2/3/4, BLDO1/2, DLDO1/2.
- **PROOF:** the standalone ESP-IDF test mounted the SD ONLY after switching its AXP
  init from DC1+ALDO1/2/3 to **DC1+ALDO1 only** (`REG 0x90 = 0x01`). With ALDO2/3 on it
  timed out; with them off -> `SDMMC MOUNT OK`, read `SYSTEM~1`.
- **FIX (applied, KEEP):** `SpawnWear/Drivers/Power/Axp2101Driver.cs::EnableDisplayRails`
  now writes `REG_LDO_ONOFF0 (0x90) = 0x01` (ALDO1 only) and `REG_LDO_ONOFF1 (0x91) = 0x00`.
  Confirmed on-watch via readback: `[Power] P2b AXP LDO 0x90 = 0x01`. The AMOLED panel
  still works on DC1+ALDO1 (vendor-proven).

### AXP2101 register cheat-sheet (I2C addr 0x34, bus on SDA=15 / SCL=14)
- `0x80` DC1-5 on/off (bit0 = DC1). **Only OR bit0; never clear — DC2/3 power the core.**
- `0x82` DC1 voltage = `(mV-1500)/100` -> 3.3V = `18`.
- `0x90` LDO on/off: bit0=ALDO1, 1=ALDO2, 2=ALDO3, 3=ALDO4, 4=BLDO1, 5=BLDO2, 6=CPUSLDO, 7=DLDO1.
- `0x91` bit0 = DLDO2.
- `0x92/0x93/0x94` ALDO1/2/3 voltage = `(mV-500)/100` -> 3.3V = `28`.
- `0x00` status1 (bit5 = VBUS present). `0x03` chip id = `0x4A`.

---

## ROOT CAUSE #2 (NOT solved): nf-interpreter build breaks SDMMC before app_main

After the power fix, nanoFramework still timed out. We matched the working standalone on
**everything** and it still failed:

| Variable | nano (fail) -> matched to standalone (works) | Result |
|---|---|---|
| AXP power | DC1+ALDO1 only (0x90=0x01, readback confirmed) | still timeout |
| `slot_config.flags` | `0` (no internal pullup) | still timeout |
| SDMMC pins | hardcoded clk=2/cmd=1/d0=3 (map already returned 2/1/3) | still timeout |
| GPIO17 | not driven (removed the unfounded "level-shifter" drive) | still timeout |
| `Configuration.SetPinFunction` | removed (let esp_vfs route the matrix) | still timeout |
| `gpio_reset_pin(1/2/3)` before mount | added | still timeout |
| `CONFIG_PM_ENABLE` + tickless | disabled | still timeout |
| CPU freq | 240 -> 160 MHz | still timeout |
| PSRAM speed | 80 -> 40 MHz | still timeout |
| Mount retries | up to 30x | consistent fail (NOT intermittent) |
| Isolation boot | power + SD only (no WiFi/touch/RTC/display/BLE) | still timeout |

**The decisive test — native pre-CLR probe:** added `native_sd_probe()` in
`targets/ESP32/_IDF/esp32s3/app_main.c`, called in `app_main` BEFORE the CLR task, the
wire-protocol receiver task, and USB-OTG start. It sets AXP DC1+ALDO1 via native I2C,
`gpio_reset`s the SD pins, then `esp_vfs_fat_sdmmc_mount` (hardcoded 2/1/3, flags=0).
Result stored in global `g_native_sd_probe_result`, printed later by `Storage_MountMMC`
as `[NATIVE-PROBE]`.

> **Result: `[NATIVE-PROBE] pre-CLR SDMMC mount result=263 (0x107) aldo0x90=0x01`.**
> It fails even before the CLR/USB/tasks exist, with power correct and pins reset.
> The identical code in the standalone ESP-IDF app on the same board = MOUNT OK.

**Conclusion:** the cause is **in the nf-interpreter build** — a compiled-in
component's static startup init (runs before `app_main`) leaves the SDMMC peripheral
unable to clock the card. Not a setting, not the CLR runtime. A grep for
`__attribute__((constructor))` / `periph_module_*` in `targets/ESP32` found nothing
obvious, so it needs **bisection**.

### Eliminated (with hardware evidence)
card / format / wiring / exFAT / sector-size; power-distribution/brownout; the card
quality; GPIO17; `SetPinFunction`; pin-hold (`gpio_reset`); SPI-vs-SDMMC; display/QSPI
or other peripheral *runtime* bus conflict; every sdkconfig knob (PM, tickless, CPU,
PSRAM, flags, pins); the CLR runtime tasks + USB-OTG.

### Note on SPI mode (dead end on this board)
ESP-IDF's `sdspi` (SD-over-SPI) returns **deterministic garbage** on a 512-byte block
read on this watch (card inits, CSD/CID correct, but the boot-sector bytes are wrong and
identical across a clean raw bus init) — even standalone. The references that work use
either ESP-IDF **SDMMC** (vendor) or **esp-hal** SPI (the Rust port), never ESP-IDF
`sdspi`. So **SDMMC is the path**; do not chase SPI mode.

---

## DEVICE UPDATE / FLASH PROCESS (how we put firmware + apps on the watch)

There are TWO separate things you flash, with different mechanisms:

### A. The nanoFramework RUNTIME (nanoCLR.bin) — needs bootloader/download mode
Required whenever you change nf-interpreter (native C/C++ runtime).

1. **Build env is BROKEN by default** (see "Build environment" below) — you MUST use the
   Python-3.13 wrapper.
2. **Put the watch in bootloader/download mode:** hold **BOOT** and power-cycle via PWR
   (hold PWR >6 s to force off, single-click PWR to power on **while still holding
   BOOT**, then release BOOT). The screen stays dark — expected.
3. The watch enumerates as **COM6** ("USB JTAG/serial debug unit") in download mode.
   (Runtime mode it's **COM3**, "USB Serial Device" = USB-OTG CDC.)
4. Flash via `tools/nf-flash-full.bat COM6` (esptool: bootloader@0x0,
   partition-table@0x8000, nanoCLR@0x10000). Hash-verifies each.
5. **Return to runtime mode:** PMIC power-cycle (hold PWR >6 s, single-click).
   Runtime comes up on **COM3**.

**Handy shortcut found today:** when the watch is *already running a plain ESP-IDF app*
(USB-Serial-JTAG on COM6), `esptool` can **auto-reset into download mode** (`--before
default_reset`) — no manual BOOT dance. This works for the standalone SD test flashes.
It does NOT apply when the watch is in nano runtime mode (COM3 / USB-OTG), which can't be
esptool-reset — that still needs the manual BOOT dance.

### B. The SpawnWear APP (.pe assemblies) — runtime mode, no bootloader
1. Build: `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" SpawnWear\SpawnWear.nfproj -t:Build -v:m -p:Configuration=Debug -p:RestorePackages=false`
2. Deploy + capture `Debug.WriteLine` over the wire protocol:
   `dotnet run tools/nf-deploy.cs "SpawnWear\bin\Debug" COM3 30`
   - If it prints `AddDevice(COM3) returned null` or `Connect failed`, just retry once
     (transient after a fresh boot).
   - Filter the captured output with `Select-String` for `[SDCard]`, `[Power]`,
     `[NATIVE-PROBE]`, etc.

---

## BUILD ENVIRONMENT UPDATE 2026-06-20 (a SECOND breakage appeared)

Beyond the Python 3.13/3.14 issue below, the IDF 3.13 venv's `cryptography` was
upgraded to **46.0.7**, but ESP-IDF 5.5 pins `cryptography<45`. So `export.bat`/
`export.ps1` now abort on `check-python-dependencies` ("Requirement 'cryptography<45'
... Installed: 46.0.7"). Two ways past it:
- **Non-mutating (used today):** skip `export.bat` entirely. Set `IDF_PATH`,
  `IDF_PYTHON_ENV_PATH`, `IDF_PYTHON_CHECK_CONSTRAINTS=no`, then apply the toolchain
  PATH via `idf_tools.py export --format key-value` (that path does NOT run the dep
  check), then invoke `idf.py` via the venv python directly. Baked into
  `sdtest-espidf/idf-env.ps1` (`. .\idf-env.ps1 ; Invoke-IdfBuild ; Invoke-IdfFlash COM6`).
- **Permanent fix:** `pip install "cryptography<45"` into the 3.13 venv (TJ OK'd
  modifying the Espressif install). Do this if the bypass ever stops working.

Also: a standalone fatfs build needs the IDF `components/fatfs/src/ffconf.h` present;
nf-interpreter renames it to `ffconf.h.sav`. `idf-env.ps1` restores it automatically.

## BUILD ENVIRONMENT (it is broken without these fixes)

The system Python was upgraded **3.13 -> 3.14** since the last build, which breaks
ESP-IDF's `export.bat` (it looks for the nonexistent `idf5.5_py3.14_env` venv). Two fixes
are required and are baked into the wrapper scripts:

1. **Force Python 3.13 on PATH** so `export.bat` selects the existing
   `idf5.5_py3.13_env` venv: `set "PATH=C:\Python313;C:\Python313\Scripts;%PATH%"`.
2. **Pin cmake to the venv python** (cmake's `find_package(Python3)` otherwise grabs
   system 3.14 which lacks `kconfiglib`):
   `cmake --preset ESP32_S3_BLE_QSPI -DPython3_EXECUTABLE=C:\Espressif\python_env\idf5.5_py3.13_env\Scripts\python.exe -DPython3_FIND_REGISTRY=NEVER -DPython3_FIND_STRATEGY=LOCATION`

Other gotchas:
- **Run builds via PowerShell, NOT the Bash tool** — Git Bash MSYS mangles `cmd /c`
  (`/c` becomes a path) so the build silently no-ops (banner-only log, exit 0, stale
  `nanoCLR.bin`). Always verify `nanoCLR.bin` timestamp after a build.
- **`tools/nf-build.bat` should be hardened** to do the Python-3.13 + cmake-Python-pin
  automatically (TODO). For now use the wrappers below.
- Active build source: `D:\users\tj\Projects\nf-interpreter\nf-interpreter` (branch
  `feature/qspi-display-driver`). The `_vendor-nf-interpreter` copy is read-only ref.

---

## TOOLS created / used today

### Persisted in the repo
- **`sdtest-espidf/`** (`D:\users\tj\Projects\SpawnWear\sdtest-espidf`) — standalone
  ESP-IDF v5.5.4 SD test app. Vendor-exact SDMMC + SPI probe + AXP2101 DC1/ALDO1 init.
  THIS is the proof the hardware works. Build with the idf wrapper; flash
  `idf.py -p COM6 flash`; read console via the SerialPort reconnect-loop (below).
  - Build needs the IDF's `fatfs/src/ffconf.h` restored: nf-interpreter renames it to
    `ffconf.h.sav` (it substitutes its own per-target one). Copy `.sav` -> `ffconf.h`
    before a standalone build, and `EXCLUDE_COMPONENTS esp_tinyusb tinyusb` (broken in
    the IDF components dir) in the top CMakeLists.
- **`tools/` wrappers** (saved today): `nf-build-py313.bat`, `nf-flash-py313.bat`
  (see "Build environment").

### Reading the standalone app's console (USB Serial/JTAG, COM6)
`idf.py monitor` FAILS headless ("Monitor requires a TTY"). Use a PowerShell
`System.IO.Ports.SerialPort` **reconnect-loop** (survives the USB re-enumeration a reset
causes) with `DtrEnable=$true`, while doing a **PMIC power-cycle** of the watch:
```
$deadline=(Get-Date).AddSeconds(35); $sp=$null
while((Get-Date) -lt $deadline){
  if($null -eq $sp -or -not $sp.IsOpen){ try{ $sp=New-Object System.IO.Ports.SerialPort('COM6',115200); $sp.ReadTimeout=400; $sp.DtrEnable=$true; $sp.Open() }catch{ Start-Sleep -Milliseconds 300; continue } }
  try{ $l=$sp.ReadLine(); if($l){ Write-Output $l } }catch{} }
```

### Diagnostics wired into nf-interpreter (currently uncommitted on the fork)
- `Storage_DiagPrintf` (existing) routes SD errors to the wire-protocol channel
  (ESP_LOG is silenced by `dummyLog` in app_main).
- `[NATIVE-PROBE]` global + `native_sd_probe()` in `esp32s3/app_main.c`.
- `Storage_MountMMC` / `Storage_MountSpi` entry + errCode diag prints.

---

## CURRENT CODE STATE (what to KEEP vs REVERT)

### KEEP (correct fixes)
- `SpawnWear/Drivers/Power/Axp2101Driver.cs` — EnableDisplayRails -> DC1+ALDO1 only.
  **This is the real ALDO power fix. Keep it.**

### REVERT for a clean watch (all experimental, did not fix SD)
- nf-interpreter (uncommitted on `feature/qspi-display-driver`):
  - `targets/ESP32/_IDF/esp32s3/app_main.c` — native_sd_probe (diagnostic)
  - `targets/ESP32/_common/Target_System_IO_FileSystem.c` — flags=0, hardcoded pins,
    map/native-probe diag prints
  - `targets/ESP32/_IDF/sdkconfig.default_octal_ble_qspi.esp32s3` — PM=n, tickless=n,
    CPU 160, PSRAM 40 (revert to PM=y / 240 / 80M for a normal watch runtime)
  - `git checkout` these 3 files to restore the committed diag runtime, then rebuild +
    bootloader-flash, then PMIC power-cycle.
- `SpawnWear/Drivers/SdCard/SdCardService.cs` — SetPinFunction skip + 30x retry loop
  (experimental; revert to a clean SDMMC-1bit init).
- `SpawnWear/Program.cs` — `_sdIsolationTest=true` guard (set false to restore normal
  boot) + the `[Power] P2b` readback line (harmless, can keep or drop).

> NOTE: the watch is currently running the experimental probe runtime
> (PSRAM40 / PM-off / 160 MHz / native-probe / hardcoded pins). Restore before normal use.

---

## PLAN FOR TOMORROW

1. **Rebase the fork onto upstream `main`** (we're 2 months behind; no SD fix in the
   diff but do it anyway in case something indirect helps + to reduce drift). Re-apply
   the QSPI display work + the diag/probe on top.
2. **Component bisection** to find the static init that breaks SDMMC:
   - Rebuild dropping the custom **graphics/QSPI** component first (most likely — fork
     code that touches SPI hardware), re-run the native probe. If SD mounts -> culprit
     found; fix its init or order it after SD, then re-enable.
   - Then BLE, then WiFi, then narrow to the exact init.
   - The native probe is already wired and is the fast oracle for each build.
3. **Optionally file a nanoFramework GitHub issue** — gold-standard repro: "native
   pre-CLR `esp_vfs_fat_sdmmc_mount` times out under nf-interpreter on ESP32-S3 but
   identical code + sdkconfig mounts in a standalone ESP-IDF app on the same board."
4. Once SD mounts under nano: revert all diagnostics, finalize `SdCardService` (SDMMC
   1-bit), ship the runtime + the ALDO power fix, then resume Phase 8 SD-card apps.

## Reference: pins / config (this watch)
- SD slot: CLK=GPIO2, CMD=GPIO1, D0=GPIO3, CS=GPIO17 (CS used only in SPI mode).
- Working SDMMC config: `SDMMC_HOST_DEFAULT()`, slot width=1, `flags=0`, clk=2/cmd=1/d0=3.
- Working power: AXP2101 DC1 + ALDO1 @ 3.3V only.
- COM3 = runtime (USB-OTG CDC), COM6 = bootloader/JTAG (USB Serial/JTAG).
