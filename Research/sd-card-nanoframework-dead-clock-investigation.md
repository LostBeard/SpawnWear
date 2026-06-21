# SD Card Will Not Mount Under nanoFramework on ESP32-S3 - The SDMMC Command Is Never Clocked Out

**Board:** Waveshare ESP32-S3-Touch-AMOLED-2.06 (watch form factor)
**Runtime that fails:** .NET nanoFramework (`nf-interpreter`), ESP-IDF target preset `ESP32_S3_BLE_QSPI`
**Runtime that works:** a bare ESP-IDF 5.5.4 application (same IDF install, same board, same card)
**Last updated:** 2026-06-20 (rev 2 - corrects the rev-1 "dead clock" evidence; adds the SD+PSRAM research thread and its disproof)
**Status:** ✅ **SOLVED + SHIPPED-READY via SDSPI + exFAT.** The SpawnWear app now mounts the microSD over **SDSPI** (SD in SPI mode on the SPI peripheral, bypassing the dead SDMMC controller) and the **full filesystem is browsable** alongside the display, WiFi, BLE and HTTP - all running together. Verified on-hardware 2026-06-20: `[SdCard] mounted at D:\`, root has 12 dirs + 8 files.

Three findings made it work, in order:
1. **SDSPI bypasses the dead SDMMC controller.** Managed `SDCardSpiParameters{spiBus=2, chipSelectPin=17}` + `Configuration.SetPinFunction(SPI2_CLOCK/MOSI/MISO on GPIO2/1/3)` -> busIndex 1 -> native **SPI3_HOST** (`host.slot=2`). The CO5300 display owns SPI2_HOST (QSPI), so the two never collide. Board rule for this watch: **when a dedicated controller fights, use the SPI peripheral** (display went QSPI, SD went SPI).
2. **Not every SD card supports SPI-mode bulk reads.** The 960 MB FAT32 card returned *stable-but-wrong* data on the 512-byte block read (`sig=88e6`, not `55aa`) while `card_init`/CSD read fine - its optional SPI-mode bulk read is flaky (common on old/small cards). A 128 GB card read a clean `55aa` boot sector -> the SDSPI read path is good; the small card was the fault. **Test with a second card before blaming the board.**
3. **exFAT had to be enabled.** The working card is exFAT; nf's per-target FatFs (`targets/ESP32/ESP32_S3/ffconf.h`) shipped `FF_FS_EXFAT 0`. Set to `1` (the IDF ffconf.h is `.sav`'d by nf's CMake; `FF_USE_LFN=2` already satisfies the LFN requirement). Now exFAT + FAT32 both mount.

Plus a display regression fix: a 2026-06-19 SD power "fix" had set the AXP2101 to ALDO1-only, blanking the AMOLED (it needs ALDO1+2+3 - git commit 6e3a765 "First pixel"); that was an SDMMC dead-end and was reverted. The SDMMC controller root cause remains unlocated and no longer blocks anything. See §14 for the original SDSPI write-up.

> ⚠️ KEY GOTCHA proven 2026-06-20: SDSPI only works if it runs on **pristine pins**. If nf's diagnostic probe does its heavy SDMMC abuse (multiple host init/deinit, clock forcing, controller resets) *first*, the subsequent SDSPI `card_init` fails with `0x106` (contamination). On pristine pins it returns `0x0`. The shipping path uses SDSPI exclusively, so there is no contamination.

---

## ⚠️ Correction vs rev 1 (read this first)

Rev 1 of this document claimed, as its headline, that "the SD card clock (CCLK) is physically dead - GPIO2 stuck low." **That specific evidence was a measurement artifact and is retracted.** It came from a software "scope" that samples `GPIO_IN` bit 2 in a tight loop. When that same scope was finally run on the *working* standalone, it returned **identical** "stuck low" readings (`[1=0 0=4000]`) - yet the standalone mounts the card 42/42. So `GPIO_IN`-sampling does **not** capture the SDMMC card clock (it idles low between bursts and the input synchronizer never catches the transient), and it cannot distinguish working from broken.

**The conclusion the artifact pointed at is still correct, but it is now proven the right way:** using the SDMMC controller's *own* status, a command that expects a response (CMD8) sets **neither command-done (CD) nor response-timeout (RTO)**. RTO can *only* assert if a command was actually clocked out to the card. Therefore **the command is genuinely never transmitted** - but this is established by controller status (`RINTSTS`), not by the discredited GPIO scope.

Lesson recorded: a novel measurement must be validated against the known-good reference *before* its output is trusted.

---

## TL;DR

An SD card mounts perfectly from a minimal bare ESP-IDF app (verified **42/42** successful mounts in one session, same physical card), from Windows, and from a standalone ESP-IDF app that links the full BT/Wi-Fi/coex/ADC stack. It **never** mounts under the nanoFramework runtime: `esp_vfs_fat_sdmmc_mount` returns **`0x107` (`ESP_ERR_TIMEOUT`)**.

What is rigorously established:

> Under nanoFramework, the **SDMMC controller accepts a command** (the `start_cmd` bit self-clears with **no** Hardware-Locked-Error) **but never executes it** - neither command-done nor response-timeout ever asserts (`RINTSTS = 0`), even with the controller interrupt fully masked. The command is never put on the wire. This happens with **every SDMMC, clock, reset, GPIO and pad register reading byte-for-byte identical** to the working bare app, with the **same `sdmmc_host.c` / `sdmmc_ll` code** compiled into both.

So the cause is a chip-state difference nanoFramework's startup creates before `app_main`, invisible to every documented register, that prevents the SDMMC command/interface unit (CIU) from clocking a command onto the bus. Every concrete hypothesis below has been **tested and eliminated** - including the most promising one from web research (an Espressif-confirmed SD+PSRAM regression that the nanoFramework lead himself reported).

---

## 1. Hardware setup (verified)

| Item | Value |
|---|---|
| SoC | ESP32-S3 (dual-core LX7), octal PSRAM (ESP32-S3R8) |
| Board | Waveshare ESP32-S3-Touch-AMOLED-2.06 |
| SD interface | SDMMC **slot 1**, **1-bit** mode |
| SD CLK / CMD / D0 | **GPIO2 / GPIO1 / GPIO3** |
| SD power rail | AXP2101 PMIC: **DC1 = 3.3 V** + **ALDO1 = 3.3 V** (reg `0x90 = 0x01`) |
| Flash | 32 MB physical; nf partition table built for 4 MB |
| Console (nf) | USB-OTG CDC (TinyUSB) + USB-Serial-JTAG secondary |
| Console (standalone) | USB-Serial-JTAG (USJ) |
| ESP-IDF | **v5.5.4** - the *same* install builds both nanoFramework and the standalone |

The ESP32-S3 SDMMC controller is Espressif's integration of the **Synopsys DesignWare Mobile Storage Host (`DW_apb_sdmmc`)**: `CTRL`, `CLKENA`, `CLKDIV`, `CMD`/`CMDARG`, `RINTSTS`, `STATUS`, plus the ESP-specific `SDMMC_CLOCK` source/divider register and `SYSTEM_PERIP_CLK_EN1.sdio_host_clk_en` bus-clock gate.

---

## 2. The symptom

```
esp_vfs_fat_sdmmc_mount(...) -> 0x107  (ESP_ERR_TIMEOUT)
```

`0x107` is the SDMMC host's command transaction timing out - the first card command never completes. The identical call (same pins, `SDMMC_HOST_DEFAULT()`, width 1, `flags=0`, same mount config) **succeeds** in the bare app on the same board and card.

---

## 3. Reading results out of a runtime that silences logging

nanoFramework silences `ESP_LOG` early in `app_main`. Four independent read paths were used:

1. **`Storage_DiagPrintf`** → `CLR_Debug::Printf` → wire-protocol debug channel, captured by `nf-deploy` on the USB-OTG COM port. Works whenever the CLR + wire protocol come up.
2. **A decoupled FreeRTOS diag task** - loops the probe verdict over the wire, independent of any deployed managed app.
3. **A crash-proof flash dump** - `app_main` writes the probe record (magic + results) to the free flash gap at **`0x3a0000`** (between the `deploy` partition end `0x3a0000` and the `config`/littlefs partition `0x3c0000`); read back with `esptool read_flash`. Survives any later CLR/USB crash.
4. **A "software scope"** (sampling `GPIO_IN` bit 2). **Discredited** - see the Correction box; it reads identically on the working build.

The standalone logs over USJ (`pyserial`; the USJ console needs a physical USB unplug/replug to (re)open reliably).

---

## 4. The pre-CLR native probe (isolation harness)

`native_sd_probe()` was added as the **first statement of `app_main`** in `targets/ESP32/_IDF/esp32s3/app_main.c`, running before `nvs_flash_init`, the wire-protocol receiver task, and the CLR main task. It:

1. brings up I²C0 + sets the AXP2101 SD rail (DC1 + ALDO1 = 3.3 V), exactly like the standalone;
2. calls the IDF SDMMC driver **directly** (`sdmmc_host_init` → `init_slot` → `set_card_clk` → `set_cclk_always_on`) - the *identical* code path the standalone uses;
3. attempts `esp_vfs_fat_sdmmc_mount`;
4. dumps the full SDMMC + system register set;
5. runs the low-level command/clock experiments below.

**Result: the probe fails identically (`0x107`).** Running pre-CLR and calling the IDF directly, this proves the failure is established **before `app_main`** - by the ESP-IDF startup for nanoFramework's link set, not by the CLR runtime or the managed SD wrapper.

---

## 5. The valid characterization: the command is never clocked out

Using the controller's own status (not the discredited GPIO scope):

| Experiment (all `card_number = 1` for slot 1; interrupt masked) | Result | Meaning |
|---|---|---|
| IDF `do_transaction(CMD0)` | `0x107`, `RINTSTS=0` | times out |
| Polled CMD0, PLL source | `start_cmd` clears, **CD=0** | accepted, never completes |
| Polled CMD0, XTAL source | `start_cmd` clears, **CD=0** | source-independent |
| Polled CMD0 after full `CTRL` reset (bits 0/1/2 self-clear) | **CD=0** | reset doesn't help |
| **CMD8 (response-expecting)** | **`RINTSTS=0`, CD=0, RTO=0** | **command never put on the wire** (RTO would assert if it were) |
| HLE check (`RINTSTS` bit 12) after CMD0 | **HLE = 0** | not a hardware-locked CIU |
| Clock-update command (`update_clock_registers_only`) with HLE-retry | committed in 1 try, `hle_seen=0` | clock-update is *not* stuck |

**`start_cmd` self-clears with no HLE → the command is accepted into the command path. Yet CD and RTO never assert → it is never transmitted.** That combination is the whole mystery.

---

## 6. Full register evidence - nf (fails) vs standalone (works)

Read at the same point (after `host_init`+`init_slot`+`set_card_clk`+`set_cclk_always_on`). **All SDMMC-relevant registers are identical.**

| Register | nf | standalone | Same? |
|---|---|---|---|
| `SDMMC_CLOCK` | `0x00932801` | `0x00932801` | ✅ (clk_sel=PLL160M, div, phase) |
| `CLKENA` / `CLKDIV` | `0x20002` / `0x1400` | `0x20002` / `0x1400` | ✅ |
| `CTRL` / `STATUS` / `RINTSTS` | `0x30` / `0x106` / `0x0` | same | ✅ |
| `SYSTEM_PERIP_CLK_EN1` (bit7 `sdio_host_clk_en`) | `0x480` | `0x480` | ✅ |
| `SYSTEM_PERIP_RST_EN1` | `0x34e` | `0x34e` | ✅ |
| GPIO matrix CLK/CMD/D0 (`func_out_sel`) | `173 / 179 / 213` | `173 / 179 / 213` | ✅ (SDHOST signals routed) |
| `GPIO_ENABLE` | `0xc00e` | `0xc00e` | ✅ (pins 1/2/3 output-enabled) |
| `IO_MUX_GPIO2` (pad cfg incl. SLP_SEL) | `0x1902` | `0x1902` | ✅ (no rogue pad hold) |
| `GPIO_OUT` | `0xc000` | `0xc000` | ✅ (SD pins matrix-driven, not GPIO-driven) |
| `SYSTEM_PERIP_CLK_EN0` | `0x6100e007` | `0x6100e083` | ❌ I2C0 (bit7) + UART (bit2) only |
| `SYSTEM_PERIP_RST_EN0` | `0x9eff1f78` | `0x9eff1f7c` | ❌ UART (bit2) only |

The only register difference is **I2C0 / UART** state (`PERIP_CLK_EN0`/`RST_EN0` bits 7 and 2) - the standalone leaves the I²C driver installed (its `axp_init` never deletes it); the probe deleted it. **Tested and ruled out** (below). `SDIO_HOST_CLK_EN` (the SDMMC bus clock, `EN1` bit 7) and everything SDMMC-specific is identical.

---

## 7. Everything tried - and ruled out (each *tested*, not assumed)

| Hypothesis / change | How tested | Outcome |
|---|---|---|
| Marginal SD contact | re-seat + re-verify minimal standalone | **Ruled out** - 42/42 mounts on the same card, same session |
| SD power / AXP rail | probe sets DC1+ALDO1 identically; `STATUS` DAT3 (card present) high | Ruled out |
| Clock source (PLL_F160M) | forced `clk_sel=0` (XTAL) | Ruled out - XTAL also never sends |
| Low-power CCLK gating | `sdmmc_host_set_cclk_always_on(slot,true)` | Ruled out |
| Interrupt / event delivery | polled CMD0/CMD8 with controller IRQ masked | Ruled out - never completes with no ISR involved |
| Controller stuck state | explicit `CTRL` controller+FIFO+DMA reset (self-clears) | Ruled out |
| **HLE / hardware-locked CIU** | read `RINTSTS` bit 12 after CMD0; clock-update with HLE-retry | **Ruled out** - HLE never set; clock-update commits first try |
| **Clock-tree refcount** (`esp_clk_tree_enable_src`) | force-enabled `SOC_MOD_CLK_PLL_F160M` before init | **Ruled out** - `enable_src=0x0`, command still never sent |
| **I2C0 left enabled vs deleted** | did *not* delete I2C0 in the probe (matched standalone, `clkEN0` bit7 ON) | **Ruled out** - still fails |
| **Pad hold / `SLP_SEL` on GPIO2** | read `IO_MUX_GPIO2` | **Ruled out** - identical (`0x1902`) |
| Pin routing / output drive | `func_out_sel` + `GPIO_ENABLE` + `GPIO_OUT` | Ruled out - routed + driven, matrix not GPIO |
| **PSRAM enabled / SD+PSRAM regression** | **disabled PSRAM entirely** (`CONFIG_SPIRAM=n`) | **Ruled out** - still fails identically (see §8) |
| **Card-Detect interlock** (DesignWare's classic silent-FSM-park: if the slot reads empty, the CIU drops commands with no HLE/RTO) | dumped `SDMMC_CDETECT` at the moment of failure | **Ruled out** - reads `0x00000000` (card **PRESENT**); CD input routed to const-zero (`0x3c`) |
| **RTC-IO pad mux override** (GPIO1/2/3 are RTC_GPIO1/2/3; the RTC mux outranks the digital matrix) | dumped `RTC_IO_TOUCH_PAD1/2/3` | **Ruled out** - `MUX_SEL` (bit19) clear on all three; pads under digital control |
| CPU freq / PSRAM speed / power management (DFS) | matched (160 MHz, 40 MHz, PM off); confirmed identical | Ruled out |
| Flash size (4 MB vs 32 MB) | built standalone at 4 MB | Ruled out |
| Bootloader | nf bootloader + standalone app | Ruled out - mounts (a success can't be faked) |
| Modem stack (BT + Wi-Fi + coex) | standalone with `CONFIG_BT_ENABLED`+`COEX_SW_COEXIST` (runs `coex_pre_init` pre-`app_main` like nf) | Ruled out |
| ADC clock-calibration constructors | standalone linked `esp_adc` + did an ADC read | Ruled out |
| **TinyUSB / USB-OTG** (the only IDF *component* nf links that the standalone doesn't) | `CONFIG_ESP32_USB_CDC=n` (TinyUSB out of build) | **Ruled out** - still fails |
| C++ static constructors | dumped + resolved all 15 `.init_array` entries | All benign (JPEG/graphics data, IDF calibration, libstdc++) |
| `ESP_SYSTEM_INIT_FN` startup hooks | compared `init_*` symbols | Matched (only `init_jk`=data, `init_source`=JPEG callback differ) |
| nanoFramework assemblies (Graphics/RMT/etc.) | reverse-strip | Run post-CLR (after the probe fails); Graphics is boot-critical but not the pre-probe cause |

---

## 8. The SD+PSRAM research thread (most promising lead - and why it is not ours)

Deep web research surfaced an **Espressif-confirmed regression** that fits the family of symptoms and was reported by the **nanoFramework lead himself**:

- **[esp-idf #13971 - "PSRAM and SD-Card don't work simultaneously" (IDFGH-13024)](https://github.com/espressif/esp-idf/issues/13971)** - **igrr (Ivan Grokhotkov, Espressif)** confirms: *"We have indeed broken the SDMMC driver for ESP32-S3 in case PSRAM is enabled in commit [`49b4bc1`](https://github.com/espressif/esp-idf/commit/49b4bc175ec24b273d19f371450a60686bd7f83c). Fixed with [`6ed7e93`](https://github.com/espressif/esp-idf/commit/6ed7e93676f6dfe5933865c9c9e45fe8512230ee)."*
- **[esp-idf #14093 - "Can't use SD Card with PSRAM" (IDFGH-13151)](https://github.com/espressif/esp-idf/issues/14093)** - opened by **`josesimoes` (José Simões, nanoFramework project lead)**, same `0x107`/`send_op_cond` failure, resolved for him by using IDF **v5.1.4** (before the regression).

Why it looked like a perfect match: same `0x107`, same chip, **nf uses PSRAM heavily** while the standalone barely does, and `SOC_SDMMC_PSRAM_DMA_CAPABLE` is correctly **unset** for ESP32-S3 (its PSRAM is not DMA-capable).

Why it is **not** our root cause (each point verified):

1. The fix `6ed7e93` is **present in our IDF 5.5.4** (`esp_ptr_external_ram` guards at `sdmmc_cmd.c:459/597`).
2. Both the regression `49b4bc1` (in `sdmmc_transaction.c`) and its fix only affect commands **with data** - the changed code is inside `if (cmdinfo->data) { ... }`. **Our failure is CMD0/CMD8, which carry no data**, so they skip that path entirely.
3. Our PSRAM **config is identical** to the working standalone (`SPIRAM_USE_MALLOC=y`, `BOOT_INIT=y`, OCT, 40 MHz, same `MALLOC_RESERVE_INTERNAL`); only `SPIRAM_IGNORE_NOTFOUND` differs, which is a no-op because PSRAM is present.
4. **Decisive test: built nf with `CONFIG_SPIRAM=n` (PSRAM fully disabled). It still fails identically** (`mount=263`, CMD8 `cd=0/rto=0`).

So the known SD+PSRAM bug is real, fixed, data-only, and **disproved for our case by direct test.** It remains the closest documented relative of our symptom and is worth citing to maintainers, but it is not the mechanism here.

---

## 9. What we believe vs what is verified

**Verified:** deterministic; pre-`app_main`; not register programming; not clock source; not interrupts; not HLE; not clock-tree refcount; not I2C0; not pad hold; not PSRAM; not TinyUSB; not config/components/constructors/bootloader. The command is accepted but never transmitted; every SDMMC register is identical to a build that works.

**Inference (not proven):** the cause is in nanoFramework's irreducible startup (the IDF init for its link set + the CLR's early/static init) and gates the SDMMC **command/interface unit's ability to put a command on the bus** at a layer not represented by any documented register. Because `start_cmd` clears (so the BIU→CIU handoff happens) yet nothing is transmitted, the most consistent remaining shape is the **CIU functional clock not actually toggling during a transaction** (or the FSM advancing without driving the line) - but this can no longer be measured in software (the GPIO scope is blind here).

---

## 10. Definitive next step that software cannot do

Because the only quasi-physical software measurement (GPIO sampling) is provably blind, the unambiguous next step is a **logic analyzer / oscilloscope on GPIO2 (CLK) and GPIO1 (CMD)** during a mount attempt, nf vs standalone. That settles for certain whether CLK physically toggles and whether command bits appear on CMD - which no register read can answer.

---

## 11. Fallback: a from-scratch SD / exFAT path (project direction)

Direction given: *"I don't care if we have to write our own TF-card driver from scratch to get nanoFramework working - in fact then we can support exFAT also."* Options, ranked by how much they sidestep the (unlocated) controller fault:

1. **SDSPI (SD in SPI mode) on the SPI peripheral — CONFIRMED WORKING at the card level.** The SPI peripheral **works in nf** (the display runs on it), so SDSPI sidesteps the broken SDMMC controller while reusing the proven IDF `esp_driver_sdspi`. The board wires a chip-select: `SDMMC_CS = GPIO17`, mapping **SCLK=2, MOSI=1 (CMD), MISO=3 (D0), CS=17**. **2026-06-20 standalone result: `sdmmc_card_init` over SPI returns `0x0`, reports the correct 960 MB card size (CSD read correctly over SPI), and a raw sector-0 read succeeds with real data** (`sdtest-espidf/main/main.c::sdspi_test()`). So SD-over-SPI is fully functional on this board - the prior "garbage" note was wrong. (The earlier `esp_vfs_fat_sdspi_mount` returning `0xffffffff` was a transient/FATFS-layer issue, not the card link; the raw block device works.) **Next: confirm the same in nf** (raw `sdmmc_card_init` added to the probe); if `cardinit=0x0` in nf, this is the shipping path and exFAT (`FF_FS_EXFAT=1`) rides on top. Note: SDSPI latches the card into SPI mode, which then makes the *SDMMC* path return `0x107` until a true power-cycle - that is expected contamination, not a regression.
2. **Bit-banged SD over GPIO (software host).** Drive CLK/CMD/DAT directly with GPIO, implementing the SD command set (CMD0/8/55/ACMD41/2/3, then block read/write via CMD17/24) in software. **Bypasses every SD peripheral entirely** - the surest bypass if even SDSPI is affected. Slow (tens-to-hundreds of KB/s), but functional and fully under our control. Reserve for if SDSPI fails.
3. **Fix the SDMMC controller** (the real bug). Needs the logic-analyzer step (declined) or maintainer insight first.

**exFAT does not require a from-scratch filesystem.** The IDF already bundles ChaN's FatFs, which supports exFAT at compile time - it is merely disabled: `FF_FS_EXFAT` is `0` in `components/fatfs/src/ffconf.h`. Setting `FF_FS_EXFAT = 1` (with LFN enabled and a compatible code page) gives exFAT + 64-bit LBA on top of **any** working block device - the bit-banged host, a fixed SDMMC, or SDSPI. So: get blocks moving by any of the three host options above, then flip `FF_FS_EXFAT=1`.

Either a working SDSPI host (option 1) or a from-scratch bit-banged host (option 2), plus FatFs-with-exFAT, is a route to "nanoFramework SD that also supports exFAT" without depending on resolving the controller mystery. SDSPI is the cheaper bet and is tried first.

---

## 12. Reproduction

**Working baseline (bare ESP-IDF 5.5.4):** AXP2101 DC1+ALDO1=3.3 V, then `esp_vfs_fat_sdmmc_mount` slot 1 / 1-bit / CLK2 CMD1 D0 3 / `flags=0` → mounts (`ESP_OK`), repeatedly (42/42).

**Failing (nanoFramework, preset `ESP32_S3_BLE_QSPI`, IDF 5.5.4):** the managed mount, and a pre-CLR native probe at the first line of `app_main`, both return `0x107`. CMD8 in the probe yields `RINTSTS=0` (no CD, no RTO) - the command is never transmitted. Persists with PSRAM disabled, TinyUSB removed, clock source forced, HLE-retry, and full controller reset.

---

## 13. External references

- **ESP32-S3 Technical Reference Manual** (clock tree, SDHOST, GPIO matrix, system registers): https://www.espressif.com/sites/default/files/documentation/esp32-s3_technical_reference_manual_en.pdf
- **ESP-IDF SDMMC Host driver (S3, v5.5)**: https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32s3/api-reference/peripherals/sdmmc_host.html
- **ESP-IDF SD/SDIO/MMC protocol layer (v5.5)**: https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32s3/api-reference/storage/sdmmc.html
- **ESP-IDF SDMMC source** (`sdmmc_host.c`, `sdmmc_transaction.c`, `sdmmc_ll.h`): https://github.com/espressif/esp-idf/tree/v5.5/components/esp_driver_sdmmc and https://github.com/espressif/esp-idf/blob/v5.5/components/hal/esp32s3/include/hal/sdmmc_ll.h
- **`esp_clk_tree` (source enable / refcount)**: https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32s3/api-reference/system/clk_tree.html
- **SD+PSRAM regression - esp-idf #13971 (IDFGH-13024)**: https://github.com/espressif/esp-idf/issues/13971  (regression commit https://github.com/espressif/esp-idf/commit/49b4bc175ec24b273d19f371450a60686bd7f83c , fix https://github.com/espressif/esp-idf/commit/6ed7e93676f6dfe5933865c9c9e45fe8512230ee)
- **nanoFramework lead's SD+PSRAM report - esp-idf #14093 (IDFGH-13151)**: https://github.com/espressif/esp-idf/issues/14093
- **esp-idf #10531 - clock-update / HLE infinite loop (IDFGH-9131)**: https://github.com/espressif/esp-idf/issues/10531
- **esp-idf #8521 - S3 microSD HIGHSPEED / pull-ups (IDFGH-6901)**: https://github.com/espressif/esp-idf/issues/8521
- **`ESP_ERR_TIMEOUT` (0x107)**: https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32s3/api-reference/error-codes.html
- **Synopsys DesignWare Mobile Storage Host (`DW_apb_sdmmc`)** - the IP behind the ESP32 SDMMC (`CTRL`, `CLKENA`, `CMD.start_cmd`, `CMD.update_clock_registers_only`, `RINTSTS.hle`, command FSM): https://www.synopsys.com/dw/ipdir.php?ds=dwc_mobile_storage
- **.NET nanoFramework `nf-interpreter`** (prior SD work: PRs #3008 pin-map/drive-index, #2805 4-bit, #2985 FileSystem): https://github.com/nanoframework/nf-interpreter
- **ChaN FatFs - exFAT (`FF_FS_EXFAT`) configuration**: http://elm-chan.org/fsw/ff/doc/config.html
- **AXP2101 PMIC** (DC1/ALDO1 regs `0x80/0x82/0x90/0x91/0x92`): https://github.com/lewisxhe/XPowersLib
- **Waveshare ESP32-S3-Touch-AMOLED-2.06 wiki**: https://www.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06

---

## 14. THE SOLUTION: SDSPI (and the path to ship)

**Confirmed 2026-06-20:** SD-over-SPI works in nanoFramework. Wiring: **SCLK=GPIO2, MOSI=GPIO1 (CMD), MISO=GPIO3 (D0), CS=GPIO17**, on `SPI2_HOST`. The raw block device (`sdmmc_card_init` + `sdmmc_read_sectors` via `esp_driver_sdspi`) initializes the card, reads the correct size, and returns real sector data - inside nf.

Why it works where SDMMC doesn't: SDSPI uses the **SPI peripheral** (which nf already drives for the display), not the DesignWare SDMMC controller (which nf's startup leaves unable to clock commands, for reasons still unlocated).

Path to ship:
1. **Verify the FATFS mount over SDSPI** end-to-end (`esp_vfs_fat_sdspi_mount`). The raw block device is proven; the card is FAT and was FATFS-mounted fine via SDMMC (42/42), so the mount should succeed. (One earlier `0xffffffff` was a transient/contaminated run.)
2. **Wire SDSPI into nanoFramework's managed Storage layer.** nf already has `Storage_MountSpi` (SDSPI) in `targets/ESP32/_common/Target_System_IO_FileSystem.c`. Configure it for this board (SPI2, SCLK2/MOSI1/MISO3/CS17) so the managed `System.IO.FileSystem` / SpawnWear mounts the SD via SPI instead of MMC.
3. **Enable exFAT**: set `FF_FS_EXFAT = 1` in the IDF FatFs `ffconf.h` (+ LFN + code page). Gives exFAT + 64-bit LBA on top of the working SDSPI block device.
4. **Remove all diagnostic code** from `app_main.c` (the native probe, the flash-dump, the diag task, the SDSPI/SDMMC test blocks) and `Target_System_IO_FileSystem.c` once SDSPI is wired in the managed path.
5. Pin-map / drive-index hygiene per nf PR #3008 patterns.

Note the contamination gotcha (top of doc): the managed shipping path must use SDSPI exclusively; do not run SDMMC init on the same pins first.

---

## Appendix A: registers and addresses used

| Name | Address | Purpose |
|---|---|---|
| `SDMMC_CLOCK_REG` | SDMMC + `0x800` | `clk_sel` (bit23: 0=XTAL,1=PLL_F160M), divider, phase |
| `SDMMC_CTRL_REG` | SDMMC + `0x00` | bit0 controller_reset, bit1 fifo_reset, bit2 dma_reset, bit4 int_enable |
| `SDMMC_CLKDIV/CLKENA` | SDMMC + `0x08`/`0x10` | card clock divider / per-card enable+low-power |
| `SDMMC_CMD_REG` | SDMMC + `0x2c` | bit31 start_cmd, 29 use_hold_reg, 21 update_clock_registers_only, 16-20 card_number, 15 send_initialization, 13 wait_prvdata, 8 check_crc, 6 response_expect, 0-5 index |
| `SDMMC_CMDARG_REG` | SDMMC + `0x28` | command argument |
| `SDMMC_RINTSTS_REG` | SDMMC + `0x44` | bit2 cmd_done, bit8 response_timeout (RTO), **bit12 HLE** |
| `SDMMC_STATUS_REG` | SDMMC + `0x48` | FIFO/FSM/DAT3 |
| `SYSTEM_PERIP_CLK_EN0/EN1` | SYSTEM + `0x18`/`0x1C` | EN1 bit7 = `sdio_host_clk_en`; EN0 bit2 UART, bit7 I2C_EXT0 |
| `SYSTEM_PERIP_RST_EN0/EN1` | SYSTEM + `0x20`/`0x24` | resets (EN1 bit7 = SDMMC) |
| `GPIO_ENABLE_REG` / `GPIO_OUT_REG` / `GPIO_IN_REG` | GPIO + `0x20`/`0x4`/`0x3C` | output-enable / output-drive / input level (bit2 = GPIO2) |
| `GPIO_FUNC0_OUT_SEL_CFG_REG` | GPIO + `0x554` (+4·pin) | GPIO-matrix output signal select |
| `IO_MUX_GPIO2_REG` | IO_MUX | pad config (SLP_SEL, FUN_IE, drive) |

## Appendix B: SDHOST GPIO-matrix signal indices (ESP32-S3)

| Signal | Index | On pin |
|---|---|---|
| `SDHOST_CCLK_OUT_2` | 173 | GPIO2 (CLK) |
| `SDHOST_CCMD_OUT_2` | 179 | GPIO1 (CMD) |
| `SDHOST_CDATA_OUT_20` | 213 | GPIO3 (D0) |

---

*Rev 2, 2026-06-20. All register values, mount results, and command-status (`RINTSTS`) readings were observed on the physical watch. The rev-1 GPIO "scope" evidence is retracted as an artifact; §5 re-establishes the same conclusion via controller status. Inferences are labeled in §9.*
