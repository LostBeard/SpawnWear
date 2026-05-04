# Hardware Reference

Authoritative pin map, IC list, and bus addresses for the **Waveshare ESP32-S3-Touch-AMOLED-2.06**. This is a one-board project; nothing in this file is generic.

When a value here disagrees with a Waveshare wiki page, schematic PDF, or vendor `pin_config.h`, the latter sources win. This doc is a derivative copy; verify before relying on a number that just landed in production code.

Source-of-truth references:
- Schematic PDF: <https://files.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06/ESP32-S3-Touch-AMOLED-2.06.pdf>
- Vendor `pin_config.h`: cloned to `D:\users\tj\Projects\SpawnWear\_vendor-waveshare-demo\` (outside this repo)
- Wiki: <https://www.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06>

## SoC

| Field | Value |
|---|---|
| Part | **ESP32-S3R8** (Espressif, embedded PSRAM variant) |
| CPU | Xtensa **LX7** dual-core, up to **240 MHz** |
| SRAM | 512 KB internal |
| ROM | 384 KB internal |
| PSRAM | **8 MB** octal, in-package |
| Flash | **32 MB** external (W25Q256-class) |
| Radio | 2.4 GHz Wi-Fi 802.11 b/g/n + **Bluetooth 5 LE** |
| USB | Native **USB-OTG** off the ESP32-S3 (USB-C connector, CDC + JTAG) |
| Antenna | On-board SMD antenna |

## Display

| Field | Value |
|---|---|
| Panel | 2.06" AMOLED, capacitive touch |
| Resolution | **410 × 502** pixels |
| Color depth | 16.7M (24-bit), wire format is RGB565 (16-bit) |
| Driver IC | **CO5300** (QSPI, 80 MHz max) |
| Backlight | Software-controlled via CO5300 register `0x51` (0x00 dark → 0xFF bright) - no separate backlight pin |
| Quirks | See `Notes/co5300-quirks.md` (alignment, 2-pixel minimum write, MIPI DCS sleep order) |

## Touch

| Field | Value |
|---|---|
| Controller | **FT3168** self-capacitance (FocalTech) |
| Bus | I²C, address **0x38** |
| Speed | 10 kHz – 400 kHz |
| Quirks | See `Research/ft3168-burst-read-layout.md` (no reserved gap byte after FingerNum) |

## Sensors / On-board ICs

| IC | Role | Bus / Addr |
|---|---|---|
| **QMI8658** | 6-axis IMU (3-axis accel + 3-axis gyro), step-count, motion / gesture | I²C, addr **0x6B** (alt 0x6A) |
| **PCF85063** | Real-Time Clock, battery-backed via AXP2101 | I²C, addr **0x51** |
| **AXP2101** | Power Management IC - charging, multi-rail outputs, ADC for battery V/I/temp, **EXIO6** = PWR side button | I²C, addr **0x34** |
| **ES8311** | Audio codec (DAC + line-in ADC), drives speaker | I²C, addr **0x18** |
| **ES7210** | Echo-cancel ADC, drives dual PDM microphone array | I²C, addr **0x40** |
| Speaker | Onboard, driven through ES8311 + class-D amp (PA_EN on **GPIO46**) | - |
| Microphones | **Dual PDM array**, fed into ES7210 | - |
| TF / microSD | Slot, 4-bit SDMMC | dedicated GPIO (see pin map) |
| Buttons | **BOOT** (direct GPIO) + **PWR** (via AXP2101) | see pin map |
| Vibration | Not present on this SKU | - |

## Pin Map

Authoritative source: vendor `pin_config.h` and the schematic PDF above.

### AMOLED Display - QSPI (CO5300)

| Signal | GPIO |
|---|---|
| SDIO0 | **GPIO4** |
| SDIO1 | **GPIO5** |
| SDIO2 | **GPIO6** |
| SDIO3 | **GPIO7** |
| SCLK  | **GPIO11** |
| CS    | **GPIO12** |
| RESET | **GPIO8** |
| TE (Tearing-Effect sync, optional) | **GPIO13** |

### I²C bus (shared by FT3168, QMI8658, PCF85063, AXP2101, ES8311, ES7210)

| Signal | GPIO |
|---|---|
| SDA   | **GPIO15** |
| SCL   | **GPIO14** |

### Touch (FT3168) extra pins

| Signal | GPIO |
|---|---|
| INT   | **GPIO38** |
| RESET | **GPIO9**  |

### Sensor / RTC interrupts

| Signal | GPIO |
|---|---|
| QMI8658 INT (motion / data-ready) | **GPIO21** |
| PCF85063 INT (alarm)              | **GPIO39** |
| AXP2101 IRQ output (PWR button + charge events, falls when AXP raises any IRQ) | **GPIO10** |

### TF / microSD card (SDMMC)

| Signal | GPIO |
|---|---|
| CLK   | **GPIO2**  |
| CMD   | **GPIO1**  |
| DATA  | **GPIO3**  |
| CS    | **GPIO17** |

### Audio I²S (ES8311 playback / ES7210 record)

| Signal | GPIO |
|---|---|
| MCLK  | **GPIO16** |
| BCLK  | **GPIO41** |
| LRCLK / WS | **GPIO45** |
| DOUT (codec → speaker) | **GPIO40** |
| DIN  (mic → codec)     | **GPIO42** |
| PA enable (speaker amp) | **GPIO46** |

### Buttons

| Button | Path | Notes |
|---|---|---|
| **BOOT** | **GPIO0** (direct, active LOW) | Hold during power-on → ROM download mode. During normal boot, used as user button - single / double / multi / long press |
| **PWR**  | **AXP2101 EXIO6** (over I²C, active HIGH) | Hold 6 s → power off. From off + on charger → click to power on. Don't hold > 6 s during normal use or device powers off |

### USB

USB-C, **native USB-OTG** off the ESP32-S3 (CDC + JTAG via the same port). Auto-download circuit on board - no manual reset/boot dance needed for normal flashing.

## Memory Map

### Flash (32 MB external, partitioned per `targets/ESP32/_IDF/esp32s3/partitions_nanoclr_16mb.csv`)

| Partition | Type | Offset | Size |
|---|---|---|---|
| nvs | data, nvs | 0x9000 | 0x6000 (24 KB) |
| phy_init | data, phy | 0xF000 | 0x1000 (4 KB) |
| factory (nanoCLR) | app | 0x10000 | 0x1A0000 (1664 KB) |
| deploy (managed code) | data, 0x84 | 0x1B0000 | 0x2E0000 (2944 KB) |
| config (network, certs, user data) | data, littlefs | 0x490000 | 0x300000 (3 MB) |

Active deploy budget is constrained well below the 2.94 MB partition by an nf-interpreter wire-protocol bug at ~290 KB; see `Research/nf-interpreter-deploy-ceiling.md`.

### RAM (managed heap)

The CLR allocates its managed heap from PSRAM at boot. Largest free block minus a small ESP-IDF reserve (typically 128 KB) becomes the heap. With an 8 MB PSRAM SoC + 411 KB framebuffer + WiFi stack, free managed heap is in the multi-megabyte range.

Allocation logic: `targets/ESP32/_nanoCLR/Memory.cpp::HeapLocation`.
