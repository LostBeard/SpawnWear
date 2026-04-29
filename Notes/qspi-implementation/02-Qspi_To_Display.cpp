//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//
// Qspi_To_Display.cpp - hybrid QSPI driver path for AMOLED panels that use the
// flash-style protocol (1-line command, 1-line address, quad-line data).
//
// Targets: CO5300, AXS15231B, RM67162, SH8601A, and similar QSPI panels common
// on 1.x" - 2.x" round and rectangular AMOLEDs from Waveshare, LilyGO, M5Stack,
// and other vendors.
//
// Selected at build time via CONFIG_NF_FEATURE_USE_QSPI_DISPLAY_DRIVER. Mutually
// exclusive with Spi_To_Display.cpp; only one DisplayInterface implementation
// links into a given firmware image.
//
// First consumer / first-light board: Waveshare ESP32-S3-Touch-AMOLED-2.06 watch
// with the CO5300 driver IC. Reverse-engineering notes for that chip live in the
// SpawnWear repo at Notes/co5300-quirks.md.
//

#ifndef QSPI_TO_DISPLAY_
#define QSPI_TO_DISPLAY_

#include "DisplayInterface.h"

#include <nanoPAL.h>
#include <target_platform.h>

// ESP32-IDF SPI master driver (the only currently-supported QSPI host platform).
// Future ports for other hosts add their own #if guards here.
#if defined(CONFIG_IDF_TARGET_ESP32S3) || defined(CONFIG_IDF_TARGET_ESP32S2) ||                                         \
    defined(CONFIG_IDF_TARGET_ESP32) || defined(CONFIG_IDF_TARGET_ESP32C3) ||                                          \
    defined(CONFIG_IDF_TARGET_ESP32C6) || defined(CONFIG_IDF_TARGET_ESP32P4)
#define QSPI_HOST_ESP_IDF 1
#include "driver/spi_master.h"
#include "driver/gpio.h"
#include "esp_log.h"
static const char *QSPI_TAG = "nf_qspi_disp";
#else
#error "Qspi_To_Display.cpp currently only supports ESP-IDF hosts; add a port for your platform."
#endif

// Frame chunking - 16 KB transfers strike a balance between DMA overhead and
// memory pressure. Each transfer carries chunkSize / 2 RGB565 pixels.
#define QSPI_MAX_TRANSFER_BYTES (16 * 1024)
#define QSPI_MAX_TRANSFER_PIXELS (QSPI_MAX_TRANSFER_BYTES / 2)

struct DisplayInterface g_DisplayInterface;
DisplayInterfaceConfig g_DisplayInterfaceConfig;

// State
static spi_device_handle_t s_qspiDevice = NULL;
static CLR_INT16 s_lcdReset = -1;
static CLR_INT16 s_lcdBacklight = -1;
static uint8_t s_pingPong[2][QSPI_MAX_TRANSFER_BYTES] __attribute__((aligned(4)));
static int s_currentBuffer = 0;
static uint32_t s_bytesQueued = 0; // bytes currently staged in s_pingPong[s_currentBuffer]
static bool s_inPixelBlock = false; // true between begin_pixels-equivalent and end_pixels

static inline uint8_t *current_buffer()
{
    return s_pingPong[s_currentBuffer];
}

static void swap_buffers()
{
    s_currentBuffer ^= 1;
    s_bytesQueued = 0;
}

// Single-line command + single-line address + (optional) single-line data.
// Used for every register write and for SetColumnAddress / SetRowAddress.
static esp_err_t qspi_send_register(uint8_t reg, const uint8_t *data, size_t dataLen)
{
    spi_transaction_ext_t t = {0};
    t.base.flags = SPI_TRANS_VARIABLE_CMD | SPI_TRANS_VARIABLE_ADDR;
    t.command_bits = 8;
    t.address_bits = 24;
    t.base.cmd = g_DisplayInterfaceConfig.GenericDriverCommands.QspiRegisterWriteCommand; // 0x02 for CO5300
    t.base.addr = ((uint32_t)reg) << 8;                                                  // register byte in the high byte of the 24-bit addr
    t.base.length = dataLen * 8;
    t.base.tx_buffer = (dataLen > 0) ? data : NULL;
    return spi_device_polling_transmit(s_qspiDevice, (spi_transaction_t *)&t);
}

// Quad-line data, single-line command + single-line address, used for memory-write pixel streams.
// `firstChunk` controls whether to issue the cmd/addr (true on the first chunk, false on continuations).
static esp_err_t qspi_send_pixel_chunk(const uint8_t *data, size_t dataLen, bool firstChunk)
{
    spi_transaction_ext_t t = {0};
    if (firstChunk)
    {
        t.base.flags = SPI_TRANS_MODE_QIO | SPI_TRANS_VARIABLE_CMD | SPI_TRANS_VARIABLE_ADDR | SPI_TRANS_CS_KEEP_ACTIVE;
        t.command_bits = 8;
        t.address_bits = 24;
        t.base.cmd = g_DisplayInterfaceConfig.GenericDriverCommands.QspiMemoryWriteCommand; // 0x32 for CO5300
        t.base.addr = g_DisplayInterfaceConfig.GenericDriverCommands.QspiMemoryWriteAddress; // 0x003C00 for CO5300
    }
    else
    {
        t.base.flags = SPI_TRANS_MODE_QIO | SPI_TRANS_CS_KEEP_ACTIVE;
        t.command_bits = 0;
        t.address_bits = 0;
    }
    t.base.length = dataLen * 8;
    t.base.tx_buffer = data;
    return spi_device_polling_transmit(s_qspiDevice, (spi_transaction_t *)&t);
}

// CS-release-only transaction. The spi_master driver groups CS-keep-active transactions into
// a single CS assertion; this issues an empty trailing transaction without CS_KEEP_ACTIVE so
// the driver releases CS at the end of a pixel burst.
static esp_err_t qspi_release_cs()
{
    spi_transaction_t t = {0};
    t.flags = 0;
    t.length = 0;
    return spi_device_polling_transmit(s_qspiDevice, &t);
}

void DisplayInterface::Initialize(DisplayInterfaceConfig &config)
{
    g_DisplayInterfaceConfig = config;
    s_lcdReset = config.Qspi.reset;
    s_lcdBacklight = config.Qspi.backLight;

    // Note: nf-interpreter's standard SPI binding (nanoSPI) does not currently expose quad-line
    // bus init, so we go directly to ESP-IDF's spi_master here. Once nf-interpreter's nanoSPI
    // gains quad pin plumbing, this path can be refactored to use it - until then, this driver
    // is the dedicated QSPI display path that bypasses nanoSPI for the bus level.

#if QSPI_HOST_ESP_IDF
    // ESP32 SPI bus configuration - all 4 data lines + clock + CS.
    // Pin assignments come from the target's CMake-time defaults via the standard nanoSPI
    // pin map; this driver assumes the bus has not yet been initialized for this host.
    spi_bus_config_t buscfg = {0};
    // The current DisplayInterfaceConfig.Qspi struct does not yet carry per-line pin numbers
    // because the standard nanoSPI binding configures them via SetPinFunction. For the QSPI
    // driver we need access to all four data lines. The bus index supplied in config selects
    // which ESP32 SPI host (SPI2 or SPI3); pins come from target_system_device_spi_config.cpp.
    // Once the polymorphic DisplayInterface lands, the four pin numbers + CS + SCLK can be
    // brought into the union directly.
    //
    // For now: read pin assignments from a target-local helper that the firmware fills in.
    extern void Qspi_GetDisplayPins(uint8_t spiHost, int *clk, int *cs, int *d0, int *d1, int *d2, int *d3);
    int clk, cs, d0, d1, d2, d3;
    Qspi_GetDisplayPins(config.Qspi.spiBus, &clk, &cs, &d0, &d1, &d2, &d3);

    buscfg.sclk_io_num = clk;
    buscfg.mosi_io_num = d0;
    buscfg.miso_io_num = d1;
    buscfg.data2_io_num = d2;
    buscfg.data3_io_num = d3;
    buscfg.max_transfer_sz = QSPI_MAX_TRANSFER_BYTES;
    buscfg.flags = SPICOMMON_BUSFLAG_MASTER | SPICOMMON_BUSFLAG_QUAD;

    spi_host_device_t host = (spi_host_device_t)(config.Qspi.spiBus + SPI2_HOST);

    esp_err_t ret = spi_bus_initialize(host, &buscfg, SPI_DMA_CH_AUTO);
    if (ret != ESP_OK)
    {
        ESP_LOGE(QSPI_TAG, "spi_bus_initialize failed: %d", ret);
        return;
    }

    spi_device_interface_config_t devcfg = {0};
    devcfg.clock_speed_hz = 40 * 1000 * 1000; // CO5300 datasheet allows up to 80 MHz; start at 40 for first-light, raise after.
    devcfg.mode = 0;
    devcfg.spics_io_num = cs;
    devcfg.queue_size = 4;
    devcfg.flags = SPI_DEVICE_HALFDUPLEX; // QSPI displays are unidirectional - we only ever write.

    ret = spi_bus_add_device(host, &devcfg, &s_qspiDevice);
    if (ret != ESP_OK)
    {
        ESP_LOGE(QSPI_TAG, "spi_bus_add_device failed: %d", ret);
        return;
    }

    // GPIOs - reset is a real pin (asserted to bring the panel out of POR), backlight on this
    // chip is software-controlled via register 0x51 so the pin field is typically -1.
    if (s_lcdReset >= 0)
    {
        gpio_reset_pin((gpio_num_t)s_lcdReset);
        gpio_set_direction((gpio_num_t)s_lcdReset, GPIO_MODE_OUTPUT);
        gpio_set_level((gpio_num_t)s_lcdReset, 1);
        OS_DELAY(10);
        gpio_set_level((gpio_num_t)s_lcdReset, 0);
        OS_DELAY(200); // CO5300 needs ~120ms; we go 200 to be safe
        gpio_set_level((gpio_num_t)s_lcdReset, 1);
        OS_DELAY(200);
    }

    if (s_lcdBacklight >= 0)
    {
        gpio_reset_pin((gpio_num_t)s_lcdBacklight);
        gpio_set_direction((gpio_num_t)s_lcdBacklight, GPIO_MODE_OUTPUT);
        gpio_set_level((gpio_num_t)s_lcdBacklight, 1);
    }
#endif // QSPI_HOST_ESP_IDF
}

void DisplayInterface::SetCommandMode(int /*mode*/)
{
    // No-op for QSPI - command vs data discrimination is in the cmd byte of each transaction,
    // not via a DC pin. The standard SPI driver uses SetCommandMode to choose between
    // "first byte is the command, rest is data" (mode 0) and "all bytes are command" (mode 1);
    // our model is always mode 0 with the command byte being the FIRST argument to SendCommand.
}

void DisplayInterface::GetTransferBuffer(CLR_UINT8 *&TransferBuffer, CLR_UINT32 &TransferBufferSize)
{
    TransferBuffer = current_buffer();
    TransferBufferSize = QSPI_MAX_TRANSFER_BYTES;
}

void DisplayInterface::ClearFrameBuffer()
{
    // Not used for QSPI displays - the panel maintains its own RAM, we just push pixels.
}

void DisplayInterface::WriteToFrameBuffer(
    CLR_UINT8 command,
    CLR_UINT8 data[],
    CLR_UINT32 dataCount,
    CLR_UINT32 /*frameOffset*/)
{
    // Used by ProcessCommand (init / orientation / power / clear sequences). Maps to a single
    // register-write transaction in QSPI mode.
    qspi_send_register(command, data, dataCount);
}

void DisplayInterface::SendCommand(CLR_UINT8 arg_count, ...)
{
    va_list ap;
    va_start(ap, arg_count);

    CLR_UINT8 parameters[16]; // CO5300's longest single-cmd payload is the 4-byte CASET / PASET; 16 is comfortable headroom.
    if (arg_count > sizeof(parameters))
    {
        arg_count = sizeof(parameters);
    }

    for (int i = 0; i < arg_count; i++)
    {
        parameters[i] = va_arg(ap, int);
    }
    va_end(ap);

    // Standard SPI mode 0 contract: first byte is the command, remainder is data.
    if (arg_count >= 1)
    {
        qspi_send_register(parameters[0], (arg_count > 1) ? &parameters[1] : NULL, arg_count - 1);
    }
}

void DisplayInterface::DisplayBacklight(bool on)
{
    if (s_lcdBacklight >= 0)
    {
        gpio_set_level((gpio_num_t)s_lcdBacklight, on ? 1 : 0);
    }
    // For chips like CO5300 the backlight is actually controlled by writing the brightness
    // register (0x51) via SendCommand - the higher-level DisplayDriver::DisplayBrightness
    // path handles that.
}

void DisplayInterface::SendBytes(CLR_UINT8 *data, CLR_UINT32 length)
{
    // Single-line bytes (used by orientation and power command sequences). Reuses the
    // register-write path with cmd=0 if length is short; for longer writes the existing
    // SendCommand path is preferred. This entry point is mostly historical.
    if (length == 0) return;
    qspi_send_register(data[0], (length > 1) ? &data[1] : NULL, length - 1);
}

// Byte-swap a 16-bit pixel stream into the destination buffer.
static void copy_pixels_byte_swapped(uint8_t *dst, const CLR_UINT16 *src, size_t pixelCount)
{
    for (size_t i = 0; i < pixelCount; i++)
    {
        CLR_UINT16 px = src[i];
        dst[i * 2 + 0] = (uint8_t)(px >> 8);
        dst[i * 2 + 1] = (uint8_t)(px & 0xFF);
    }
}

// Push a row of pixels through the pingpong buffer + DMA chunks.
static void send_pixels_row(const CLR_UINT16 *row, size_t pixelCount, bool doByteSwap, bool *firstChunk)
{
    while (pixelCount > 0)
    {
        size_t roomPixels = (QSPI_MAX_TRANSFER_BYTES - s_bytesQueued) / 2;
        size_t take = (pixelCount < roomPixels) ? pixelCount : roomPixels;
        if (take == 0)
        {
            // Buffer is full; flush it.
            qspi_send_pixel_chunk(current_buffer(), s_bytesQueued, *firstChunk);
            *firstChunk = false;
            swap_buffers();
            continue;
        }

        if (doByteSwap)
        {
            copy_pixels_byte_swapped(current_buffer() + s_bytesQueued, row, take);
        }
        else
        {
            memcpy(current_buffer() + s_bytesQueued, row, take * 2);
        }
        s_bytesQueued += take * 2;
        row += take;
        pixelCount -= take;

        if (s_bytesQueued == QSPI_MAX_TRANSFER_BYTES)
        {
            qspi_send_pixel_chunk(current_buffer(), s_bytesQueued, *firstChunk);
            *firstChunk = false;
            swap_buffers();
        }
    }
}

void DisplayInterface::SendData16Windowed(
    CLR_UINT16 *data,
    CLR_UINT32 startX,
    CLR_UINT32 startY,
    CLR_UINT32 width,
    CLR_UINT32 height,
    CLR_UINT32 stride,
    bool doByteSwap)
{
    bool firstChunk = true;
    s_bytesQueued = 0;
    CLR_UINT16 *startOfLine = data + (startY * stride) + startX;

    if (width == stride)
    {
        // Contiguous block - one big push, the chunker handles DMA-sized splits.
        send_pixels_row(startOfLine, (size_t)width * height, doByteSwap, &firstChunk);
    }
    else
    {
        for (CLR_UINT32 row = 0; row < height; row++)
        {
            send_pixels_row(startOfLine, width, doByteSwap, &firstChunk);
            startOfLine += stride;
        }
    }

    // Flush any remainder.
    if (s_bytesQueued > 0)
    {
        qspi_send_pixel_chunk(current_buffer(), s_bytesQueued, firstChunk);
        s_bytesQueued = 0;
    }
    qspi_release_cs();
}

void DisplayInterface::FillData16(CLR_UINT16 fillValue, CLR_UINT32 fillLength)
{
    bool firstChunk = true;
    s_bytesQueued = 0;

    // Fill one chunk's worth in advance; reuse it for each transfer.
    uint16_t *fillBuf = (uint16_t *)current_buffer();
    size_t chunkPixels = QSPI_MAX_TRANSFER_PIXELS;
    for (size_t i = 0; i < chunkPixels; i++)
    {
        // Pre-byteswapped because FillData16 callers do not pass a doByteSwap flag.
        fillBuf[i] = (uint16_t)((fillValue >> 8) | (fillValue << 8));
    }

    while (fillLength > 0)
    {
        size_t take = (fillLength < chunkPixels) ? fillLength : chunkPixels;
        qspi_send_pixel_chunk((const uint8_t *)fillBuf, take * 2, firstChunk);
        firstChunk = false;
        fillLength -= take;
    }

    qspi_release_cs();
}

#endif // QSPI_TO_DISPLAY_
