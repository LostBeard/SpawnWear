# QSPI Display Driver Design - Upstream Contribution to nanoFramework

This doc describes how SpawnWear adds **hybrid-QSPI display panel support** (single-line command, single-line address, quad-line data) to .NET nanoFramework. The work targets `nf-interpreter` (the runtime) + `nanoFramework.Graphics` (the managed library) and lands the CO5300 panel on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch as the first consumer.

The design is intentionally generic. Once landed, the same path supports CO5300, AXS15231B, RM67162, SH8601A, and every other QSPI AMOLED that uses the flash-style hybrid protocol (the protocol Espressif's `spi_master` driver was originally built around for QSPI flash and which round-AMOLED panel makers all adopted).

## Goal

Apps written against `nanoFramework.Graphics` continue to use the same `Screen`, `Bitmap`, `Pen`, `Brush`, `Font` APIs. Adding support for a QSPI panel becomes "drop in a `Co5300` descriptor and configure the bus pins" - no app code changes. Same shape as the existing managed drivers (`Gc9A01`, `St7789`, `Ili9341`, etc.), just on a quad-line bus.

## Existing nanoFramework display architecture

```
App layer (managed C#)
   |  draws into PAL_GFX_Bitmap (RAM framebuffer, 16-bit RGB565)
   v
GraphicsDriver (CPU pixel ops, src/nanoFramework.Graphics/Graphics/Core/GraphicsDriver.cpp)
   |  pixel/line/rect/ellipse/blit primitives operate on the bitmap in RAM
   |  Screen_Flush() ->
   v
DisplayDriver (descriptor consumer, src/nanoFramework.Graphics/Graphics/Displays/Generic_SPI.cpp)
   |  Initialize() reads a GraphicDriver descriptor (managed C# byte-array spec) and
   |    walks it, sending init-sequence commands via DisplayInterface.SendCommand()
   |  BitBlt() does:
   |    SetWindow(x1,y1,x2,y2)                  - column + row address commands
   |    SendCommand(MemoryWrite)                - tells the panel "data follows"
   |    SendData16Windowed(pixels, ...)         - pushes the 16-bit pixel stream
   v
DisplayInterface (bus abstraction, src/nanoFramework.Graphics/Graphics/Displays/Spi_To_Display.cpp)
   |  SendCommand: pulls DC pin LOW, sends command byte, raises DC HIGH, sends data
   |  SendBytes / SendData16: full-duplex SPI through nanoSPI_Write_Read
   |
   |  CONFIG: DisplayInterfaceConfig union in DisplayInterface.h holds:
   |    .Spi { spiBus, chipSelect, dataCommand (DC pin), reset, backLight }
   |    .I2c { i2cBus, address, fastMode }
   |    .VideoDisplay { ... DSI / RGB-panel signals ... }
   |    .Screen { x, y, width, height }
   |    .GenericDriverCommands { ... copied from the managed descriptor ... }
   v
nanoSPI (src/System.Device.Spi/) -> ESP32 cpu_spi.cpp
   |  Standard ESP-IDF spi_master driver, full-duplex single-line MOSI/MISO
   |  ESP-IDF supports half-duplex + quad-mode natively via spi_transaction_t flags;
   |  the nanoFramework binding currently does not expose them.
   v
ESP-IDF / hardware
```

**Why standard SPI displays use a DC pin:** standard MIPI DCS panels (ILI9341, ST7789V, GC9A01) get a single SPI MOSI line and discriminate command vs data via an external GPIO ("DC" or "RS"). The CO5300 family does not have a DC pin - command vs data is encoded in the cmd-byte of the SPI transaction itself, and the data phase optionally switches to quad-line (4 parallel data signals) for bandwidth.

## What the CO5300 hybrid QSPI protocol actually looks like

Every transaction has three phases:

| Phase | Wire mode | Length | What it carries |
|---|---|---|---|
| Command | **single-line** (D0 only) | 8 bits | `0x02` for register write, `0x32` for memory write, etc. |
| Address | **single-line** (D0 only) | 24 bits | For register write: `(regByte << 8) | 0x0000`. For memory write: `0x003C00` (constant). |
| Data | depends | variable | **single-line** for register data, **quad-line** (D0..D3 in parallel) for pixel data after `0x32` |

ESP32-S3's `spi_device_interface_config_t` and `spi_transaction_t` API supports exactly this pattern. From `cpu_spi.cpp`'s existing code we see ESP-IDF's `SPI_DEVICE_HALFDUPLEX` flag is already used; we just need to extend the binding to also accept quad-mode flags on a per-transaction basis.

## Proposed extensions

### 1. Managed side - extend the descriptor (`nanoFramework.Graphics/Primitive/GraphicDriver.cs`)

Add three optional fields. They default to "standard SPI" behavior so existing descriptors continue to work unchanged.

```csharp
public class GraphicDriver
{
    // ... existing fields ...

    /// <summary>Bus type for this panel. Default = Spi (standard MIPI DCS with DC pin).</summary>
    public DisplayBusType BusType { get; set; } = DisplayBusType.Spi;

    /// <summary>QSPI: command byte for register-write transactions (e.g. 0x02 for CO5300). Ignored when BusType != Qspi.</summary>
    public byte QspiRegisterWriteCommand { get; set; }

    /// <summary>QSPI: command byte for memory-write transactions (e.g. 0x32 for CO5300). Ignored when BusType != Qspi.</summary>
    public byte QspiMemoryWriteCommand { get; set; }

    /// <summary>QSPI: 24-bit address payload accompanying memory-write (e.g. 0x003C00 for CO5300). Ignored when BusType != Qspi.</summary>
    public uint QspiMemoryWriteAddress { get; set; }
}

public enum DisplayBusType : byte
{
    Spi = 0,    // existing - 1-line MOSI, DC pin discriminates
    Qspi = 1,   // new - 1-line cmd, 1-line addr, quad-line memory data
    // future: I2c, ParallelRgb, Dsi, etc. already exist as separate union members
}
```

### 2. Managed side - new descriptor `Co5300.cs`

Drop into `_vendor-nanoframework-graphics/ManagedDrivers/Co5300/Co5300.cs`, mirrors `Gc9A01.cs` shape.

```csharp
public static class Co5300
{
    public static ushort Width  { get; } = 410;
    public static ushort Height { get; } = 502;

    public static GraphicDriver GraphicDriver => _driver ??= new GraphicDriver
    {
        BusType                  = DisplayBusType.Qspi,
        QspiRegisterWriteCommand = 0x02,
        QspiMemoryWriteCommand   = 0x32,
        QspiMemoryWriteAddress   = 0x003C00,

        MemoryWrite              = 0x2C,    // logical MemoryWrite cmd, translated to QSPI memory-write at runtime
        SetColumnAddress         = 0x2A,
        SetRowAddress            = 0x2B,
        BitsPerPixel             = 16,
        Brightness               = 0x51,
        SetWindowType            = SetWindowType.X16bitsY16Bit,

        InitializationSequence   = new byte[]
        {
            (byte)GraphicDriverCommandType.Command, 1, 0x11,                                 // SLPOUT
            (byte)GraphicDriverCommandType.Sleep,   12,                                      // 120ms
            (byte)GraphicDriverCommandType.Command, 2, 0xFE, 0x00,                           // vendor page select
            (byte)GraphicDriverCommandType.Command, 2, 0xC4, 0x80,                           // SPI mode control
            (byte)GraphicDriverCommandType.Command, 2, 0x3A, 0x55,                           // RGB565
            (byte)GraphicDriverCommandType.Command, 2, 0x53, 0x20,                           // CTRL display
            (byte)GraphicDriverCommandType.Command, 2, 0x63, 0xFF,                           // HBM brightness max
            (byte)GraphicDriverCommandType.Command, 1, 0x29,                                 // DISPON
            (byte)GraphicDriverCommandType.Command, 2, 0x51, 0xD0,                           // normal brightness
            (byte)GraphicDriverCommandType.Command, 2, 0x58, 0x00,                           // contrast enhancement off
            (byte)GraphicDriverCommandType.Command, 2, 0x36, 0x00,                           // MADCTL
            (byte)GraphicDriverCommandType.Sleep,   1,
            (byte)GraphicDriverCommandType.Command, 1, 0x20,                                 // INVOFF
        },
        // OrientationLandscape / Portrait / etc as needed; sleep / power-mode commands
    };
}
```

Note the column offset of 22 (the panel is 410 wide inside a wider RAM) is best applied via `DisplayInterfaceConfig.Screen.x = 22`; the existing `SetWindowX16bitsY16Bit` handler already adds `Screen.x` to column-set values.

### 3. Native side - extend `DisplayInterfaceConfig` (`DisplayInterface.h`)

Add `Qspi` variant to the union and propagate the QSPI translation fields from the descriptor:

```cpp
struct DisplayInterfaceConfig
{
    union {
        struct { /* existing Spi { spiBus, chipSelect, dataCommand, reset, backLight } */ } Spi;
        struct {
            CLR_UINT8 spiBus;
            CLR_INT32 chipSelect;
            CLR_INT32 reset;
            CLR_INT32 backLight;
        } Qspi;
        // existing I2c / VideoDisplay variants
    };
    struct { /* Screen */ } Screen;
    struct {
        // existing GenericDriverCommands fields ...
        CLR_UINT8 BusType;                    // 0=Spi, 1=Qspi, ...
        CLR_UINT8 QspiRegisterWriteCommand;
        CLR_UINT8 QspiMemoryWriteCommand;
        CLR_UINT32 QspiMemoryWriteAddress;
    } GenericDriverCommands;
};
```

### 4. Native side - new `Qspi_To_Display.cpp`

Parallel to `Spi_To_Display.cpp` but uses ESP-IDF's half-duplex SPI with explicit cmd/addr/data phases.

```cpp
// Pseudo-shape - each method maps onto a single ESP-IDF spi_transaction_t with appropriate flags.

void DisplayInterface::SendCommand(CLR_UINT8 arg_count, ...)
{
    // arg_count >= 1: first byte is the logical register, rest is data
    // QSPI translation: spi cmd byte = QspiRegisterWriteCommand (0x02),
    //                   spi addr (24-bit) = (regByte << 8),
    //                   data phase = remaining bytes in single-line mode.
    spi_transaction_ext_t t = { 0 };
    t.base.flags = SPI_TRANS_VARIABLE_CMD | SPI_TRANS_VARIABLE_ADDR;
    t.command_bits = 8;
    t.address_bits = 24;
    t.base.cmd  = config.QspiRegisterWriteCommand;
    t.base.addr = ((uint32_t)regByte) << 8;
    t.base.length = (arg_count - 1) * 8;
    t.base.tx_buffer = &args[1];
    spi_device_polling_transmit(handle, &t.base);
}

void DisplayInterface::SendData16Windowed(CLR_UINT16 *data, ..., bool doByteSwap)
{
    // Pixel stream: SPI cmd byte = QspiMemoryWriteCommand (0x32),
    //               addr (24-bit) = QspiMemoryWriteAddress (0x003C00),
    //               data phase = 16-bit pixels in QUAD-line mode.
    spi_transaction_ext_t first = { 0 };
    first.base.flags = SPI_TRANS_VARIABLE_CMD | SPI_TRANS_VARIABLE_ADDR | SPI_TRANS_MODE_QIO;
    first.command_bits = 8;
    first.address_bits = 24;
    first.base.cmd  = config.QspiMemoryWriteCommand;
    first.base.addr = config.QspiMemoryWriteAddress;
    first.base.length = chunk_bytes * 8;
    first.base.tx_buffer = chunk_buffer;
    spi_device_polling_transmit(handle, &first.base);

    // Subsequent chunks (if pixel count exceeds DMA chunk size): no cmd, no addr, quad-line data.
    spi_transaction_ext_t cont = { 0 };
    cont.base.flags = SPI_TRANS_MODE_QIO;
    cont.base.length = chunk_bytes * 8;
    cont.base.tx_buffer = chunk_buffer;
    spi_device_polling_transmit(handle, &cont.base);
}
```

Key implementation notes:
- ESP-IDF's `spi_device_polling_transmit` is the synchronous path; `spi_device_queue_trans` + `_get_trans_result` for async / DMA pipelining.
- `SPI_TRANS_MODE_QIO` is the per-transaction flag that switches the data phase to quad-line. Command and address phases stay single-line (the default) without further flags - the chip's hybrid mode is exactly this combination.
- DMA chunk size: existing `SPI_MAX_TRANSFER_SIZE = 320 * 2 * 8 = 5120` bytes. CO5300 frame is 410*502*2 = 411,640 bytes, so ~80 DMA chunks per full-screen flush. Pre-allocate two ping-pong buffers (already the pattern in `Spi_To_Display.cpp` via `spiBuffer` / `spiBuffer2`), pipeline DMA submission against pixel byte-swap.
- Bus init: open the SPI device with all four data lines (D0=MOSI, D1=MISO, D2/D3 as quad), `spi_bus_config_t::quadwp_io_num` and `quadhd_io_num` populated from the watch's SDIO2 (GPIO6) and SDIO3 (GPIO7) pins.

### 5. Native side - extend `Generic_SPI.cpp` to dispatch on BusType

`DisplayDriver::Initialize` already reads the descriptor and calls `g_DisplayInterface.WriteToFrameBuffer / SendCommand` etc. The polymorphism point is `DisplayInterface` itself. The cleanest split is per-build:

- New build option `NF_FEATURE_USE_QSPI_DISPLAY_DRIVER` (matching the existing `NF_FEATURE_USE_SPI_DISPLAY_DRIVER` etc. patterns in `Kconfig.graphics`).
- A target's CMakeLists chooses **either** `Spi_To_Display.cpp` **or** `Qspi_To_Display.cpp` to compile in. Both define the same global `g_DisplayInterface` struct so `DisplayDriver` is unchanged.
- Future: a polymorphic `DisplayInterface` virtual class that switches at runtime based on `config.GenericDriverCommands.BusType`. For now, per-build is simpler and matches existing nanoFramework conventions.

### 6. ESP32 cpu_spi.cpp - quad-mode plumbing

`cpu_spi.cpp::CPU_SPI_Initialize` constructs `spi_bus_config_t`. Today it sets `mosi_io_num` and `miso_io_num`. We extend to also populate `quadwp_io_num` and `quadhd_io_num` when the bus is configured with a new `SpiBusConfiguration_QuadHalfDuplex` (or similar enum value). This is a small, additive change to the existing SPI binding; existing single-line consumers are unaffected.

### 7. Custom firmware target

A new defconfig: `targets/ESP32/defconfig/ESP32_S3_BLE_QSPI_defconfig`. Inherits from `ESP32_S3_BLE_defconfig`, adds:
- `CONFIG_NF_FEATURE_USE_QSPI_DISPLAY_DRIVER=y`
- `CONFIG_NF_FEATURE_USE_SPI_DISPLAY_DRIVER=n`  (mutually exclusive for now; can both be enabled once the polymorphic DisplayInterface lands)
- `CONFIG_NF_FEATURE_USE_GRAPHICS=y`
- everything else inherited

Resulting firmware image: `ESP32_S3_BLE_QSPI-1.16.0.X.zip`, flashed to the watch with the standard `nanoff --target ESP32_S3_BLE_QSPI --serialport COMx --update --masserase` recipe.

## Performance budget

Frame size: 410 × 502 × 2 bytes = **411,640 bytes**.
Theoretical bandwidth at 80 MHz QSPI clock × 4 data lines / 8 bits = **40 MB/s**.
Per-frame transmission time at theoretical max: **10.3 ms** = 97 fps ceiling.

Practical hits:
- DMA setup + chunk overhead: ~1-2% per chunk
- SPI clock probably tops out at 75-80 MHz on this PCB (CO5300 says 80 max but signal integrity often limits to less)
- Memory-bound on the framebuffer copy: PSRAM read at ~100 MB/s in typical ESP32-S3 PSRAM configs
- Byte-swap overhead (RGB565 endianness): the current `CopyData16ByteSwapped` is a tight loop - consider inline assembly or 32-bit lane swap

Realistic full-screen-flush target: **30-60 fps** for solid color fills, **20-40 fps** for arbitrary pixel updates (memory-bandwidth limited). For watch UI (mostly small dirty rects per frame), interactive performance should be excellent.

## Future cleanups (after first-light)

1. **Polymorphic DisplayInterface** - virtual class with `Spi`, `Qspi` (and later `I2c`, `Dsi`) implementations selected at runtime, both compiled in.
2. **Per-region partial updates** - apps already produce dirty rects via `Bitmap.Invalidate(rect)`; pipe them through to small QSPI memory-write transactions.
3. **TE-pin sync** - GPIO13 on this watch is the CO5300's tearing-effect output. Wire an interrupt on rising edge, schedule the BitBlt to start within the vertical blanking window. Eliminates tearing for high-frame-rate animations.
4. **Compressed-stream memory-write** - some QSPI panels accept run-length-encoded or palettized streams that reduce bus time. CO5300 doesn't (RGB565 raw only).
5. **Backlight via brightness command** - CO5300 register 0x51 is already the brightness register; make sure `DisplayDriver::DisplayBrightness` works through the QSPI translation (it should, since `SendCommand` handles arbitrary register writes uniformly).

## Files we will touch

| File | Type | Change |
|---|---|---|
| `nanoFramework.Graphics/nanoFramework.Graphics/Primitive/GraphicDriver.cs` | managed | Add 3 fields + `DisplayBusType` enum file |
| `nanoFramework.Graphics/nanoFramework.Graphics/Primitive/DisplayBusType.cs` | managed | NEW enum |
| `nanoFramework.Graphics/ManagedDrivers/Co5300/Co5300.cs` | managed | NEW descriptor |
| `nanoFramework.Graphics/ManagedDrivers/Co5300/nanoFramework.Graphics.Co5300.nfproj` | project | NEW |
| `nf-interpreter/src/nanoFramework.Graphics/Graphics/Displays/DisplayInterface.h` | native header | Add Qspi config variant + 4 new descriptor fields |
| `nf-interpreter/src/nanoFramework.Graphics/Graphics/Displays/Qspi_To_Display.cpp` | native | NEW - parallel to Spi_To_Display.cpp |
| `nf-interpreter/src/nanoFramework.Graphics/Graphics/Displays/CMakeLists.txt` | build | Conditional compile of Qspi_To_Display.cpp |
| `nf-interpreter/Kconfig.graphics` | build | New `NF_FEATURE_USE_QSPI_DISPLAY_DRIVER` option |
| `nf-interpreter/targets/ESP32/_nanoCLR/System.Device.Spi/cpu_spi.cpp` | native | Quad-mode bus init when configured for QSPI |
| `nf-interpreter/targets/ESP32/defconfig/ESP32_S3_BLE_QSPI_defconfig` | build | NEW custom firmware target |
| `nf-interpreter/targets/ESP32/ESP32_S3/target_system_device_spi_config.cpp` | native | (maybe) Default SPI pin assignments for QSPI mode |

Also touched in this repo (SpawnWear):
| File | Change |
|---|---|
| `SpawnWear/packages.config` | Add `nanoFramework.Graphics` + `nanoFramework.Graphics.Co5300` |
| `SpawnWear/SpawnWear.nfproj` | Same, plus references |
| `SpawnWear/Program.cs` | Initialize the display in Main() and write "Hello SpawnWear" to confirm pixels |
| `Notes/build-environment.md` | NEW - ESP-IDF + custom firmware build recipe (next task) |

## Out of scope for this design

- **FT3168 touch driver** is a separate workstream (managed-only, plain I2C; see task 1.4 in the project task list).
- **Audio (ES8311 / ES7210)** has its own native I2S surface in nf-interpreter; will be a separate Phase 6 contribution.
- **Vendor-pre-existing drivers** (ILI9341, ST7789V, GC9A01) keep their current implementation. The QSPI changes are additive.

## Status

Design committed `<this commit>`. Implementation starts in `_vendor-nf-interpreter` after build environment is set up (task 1.2).
