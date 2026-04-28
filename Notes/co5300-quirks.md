# CO5300 AMOLED Driver - Reverse-Engineering Notes

The CO5300 is the AMOLED driver IC on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch. **None of the QSPI protocol, init sequence, or alignment quirks are documented in any datasheet or app note.** Everything here was reverse-engineered from Waveshare's Arduino C++ source by the Rust port author and copied into a notes file so we don't have to repeat the pain.

## Sources

- **Waveshare Arduino sample** - `_vendor-waveshare-demo/examples/Arduino-v3.2.0/libraries/Arduino_GFX/src/display/Arduino_CO5300.{h,cpp}` (in the project parent folder, outside this repo)
- **Rust port** - <https://github.com/infinition/waveshare-watch-rs> (cloned to `_vendor-rust-watch/`); see `src/drivers/co5300.rs` and `src/drivers/qspi_bus.rs`
- **Hackaday article + comments** - <https://hackaday.com/2026/04/11/rust-y-firmware-for-waveshare-smartwatch/> - the comment thread is where the author publicly summarized the gotchas

## QSPI bus protocol (the hybrid mode)

The CO5300 expects the same flash-style QSPI protocol that ESP32-S3 hardware uses for memory writes:

| Phase | Wire mode | Bytes | Purpose |
|---|---|---|---|
| Command | **Single-line** (MOSI only) | 8-bit | `0x02` for register write, `0x32` for memory write |
| Address | **Single-line** | 24-bit | For register writes: `(reg << 8)` (one byte of register, two bytes 0). For memory writes: `0x003C00` (constant, names the RAM-write window) |
| Dummy | none | 0 | None for this chip |
| Data | depends on op | variable | **Single-line** for register data, **Quad-line** (4-bit-wide) for pixel data |

**Key implication:** the bus driver must support "command in single mode, data in quad mode" on the same transaction. ESP32-S3's `spi_master` driver does this through `half_duplex_write` with separate `DataMode` for command and data phases. nanoFramework's `System.Device.Spi` does **not** expose this today. We will need to add a managed binding for it (likely as `nanoFramework.Hardware.Esp32.QspiDevice` or similar) - see Phase 1 in `README.md` and task #9.

### Reference Rust calls (translate one-for-one)

```rust
// Register write (cmd 0x02, register reg, optional data byte(s) in single mode)
spi.half_duplex_write(
    DataMode::Single,                                      // data phase = single
    Command::_8Bit(0x02, DataMode::Single),                // cmd = 0x02 in single
    Address::_24Bit((reg << 8) as u32, DataMode::Single),  // 24-bit addr in single
    0,                                                     // 0 dummy cycles
    data_bytes,                                            // 0..N bytes
);

// Pixel stream start / continue (cmd 0x32, addr 0x003C00, data in quad)
spi.half_duplex_write(
    DataMode::Quad,
    Command::_8Bit(0x32, DataMode::Single),
    Address::_24Bit(0x003C00, DataMode::Single),
    0,
    pixel_bytes,
);

// Pixel stream continuation (no cmd, no addr, data in quad)
spi.half_duplex_write(
    DataMode::Quad,
    Command::None,
    Address::None,
    0,
    more_pixel_bytes,
);
```

## Quirks (the things that bite)

These are the constraints from the chip itself, not the driver's choice:

1. **Minimum 2-pixel writes.** Single pixels do not commit. The driver MUST round to at least 2 pixels per write. The Rust port draws every individual pixel as a 2x2 block (`fill_contiguous` doubles the row when `height < 2`).
2. **Even-aligned address windows.** `CASET` (column-address-set, cmd `0x2A`) and `PASET` (page-address-set, cmd `0x2B`) values must round x_start / y_start down to even, and x_end / y_end up to odd. Driver primitives must handle this internally - callers should be able to pass any rectangle.
3. **Column offset of 22.** The 410-pixel-wide panel is laid out inside a wider RAM. Any user-facing x coordinate must add `+22` before being sent to `CASET`. Row offset is 0.
4. **Min 2-line writes.** Same alignment story for the y axis - 1-line-tall rectangles need the row duplicated. The Rust port handles this in `fill_contiguous` with a `needs_row_dup` flag.
5. **MIPI DCS sleep order.** Power on: SLPOUT (`0x11`) -> wait 120ms -> DISPON (`0x29`) -> wait 20ms. Power off: DISPOFF (`0x28`) -> wait 20ms -> SLPIN (`0x10`) -> wait 120ms. Skipping the delays leaves the panel in an inconsistent state.

## Init sequence (canonical)

Hardware reset:
- RST high, wait 10 ms
- RST low,  wait 200 ms
- RST high, wait 200 ms

Software init (each line is a register write; `cmd / data`):
| Cmd  | Data | Meaning |
|------|------|---------|
| 0x11 |      | SLPOUT (sleep out) - then wait 120 ms |
| 0xFE | 0x00 | Vendor register page select |
| 0xC4 | 0x80 | SPI mode control |
| 0x3A | 0x55 | Pixel format = 16-bit RGB565 |
| 0x53 | 0x20 | Write CTRL Display 1 |
| 0x63 | 0xFF | HBM (high-brightness mode) brightness = max |
| 0x29 |      | DISPON (display on) |
| 0x51 | 0xD0 | Normal-mode brightness = 0xD0 (208/255) |
| 0x58 | 0x00 | WCE - contrast enhancement off |
| 0x36 | 0x00 | MADCTL - RGB order, no rotation |
| (delay 10 ms) |  |  |
| 0x20 |      | INVOFF (display inversion off) |

## Useful command reference

| Cmd  | Name | Notes |
|------|------|-------|
| 0x01 | SWRESET | Software reset |
| 0x10 | SLPIN | Enter sleep |
| 0x11 | SLPOUT | Exit sleep |
| 0x20 | INVOFF | Inversion off |
| 0x21 | INVON | Inversion on |
| 0x28 | DISPOFF | Display off |
| 0x29 | DISPON | Display on |
| 0x2A | CASET | Column address set (4 data bytes: x_start hi/lo, x_end hi/lo) |
| 0x2B | PASET | Page (row) address set (4 data bytes: y_start hi/lo, y_end hi/lo) |
| 0x2C | RAMWR | Memory write start |
| 0x32 | RAMWR_QUAD | Memory write start in QSPI quad-mode (custom) |
| 0x36 | MADCTL | Memory access control - rotation + RGB/BGR |
| 0x3A | PIXFMT | Pixel format - `0x55` = RGB565 |
| 0x51 | BRIGHTNESS | Normal-mode brightness - 0x00..0xFF |
| 0x53 | WCTRLD1 | Write CTRL Display 1 |
| 0x58 | WCE | Contrast enhancement |
| 0x63 | HBM_BRIGHTNESS | High-brightness-mode brightness |
| 0xC4 | SPIMODECTL | SPI mode control - `0x80` enables QSPI |
| 0xFE | PAGE | Vendor register page select - `0x00` = main |

## Pins (from `src/board.rs` in the Rust port - matches Waveshare schematic)

```
LCD QSPI:
  SDIO0=GPIO4   SDIO1=GPIO5   SDIO2=GPIO6   SDIO3=GPIO7
  SCLK=GPIO11   CS=GPIO12     RESET=GPIO8
  TE  =GPIO13   (Tearing-Effect sync output from CO5300, optional but recommended)
```

The TE pin is optional but lets the firmware avoid drawing during the panel's vertical-sync window (no tearing). Phase 1 driver should expose it.

## Performance hints from the Rust port

- DMA transfers in 8 KB chunks (`DMA_CHUNK = 8000` in `qspi_bus.rs`).
- A pre-allocated heap scratch buffer holds one chunk's worth of pixels (16-bit big-endian byte order on the wire). Avoids per-frame allocations.
- `fill_solid` rebuilds the scratch buffer once with the repeated color, then DMA-streams it without re-touching memory between chunks.
- nanoFramework SPI may not expose DMA chunking directly; if not, the binding should batch large transfers itself.

## What we will NOT inherit from the Rust port

- The Rust port uses `embedded-graphics` traits. Our managed driver should sit on `nanoFramework.Graphics` (or an equivalent) so apps written against the framework's drawing primitives work without porting.
- The Rust port assumes a single global framebuffer in PSRAM. We can match that initially, but services / apps that produce dirty rectangles let us do partial updates and save power on the AMOLED (only the changed pixels light up extra current draw).
