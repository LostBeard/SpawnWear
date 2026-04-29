//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

namespace nanoFramework.UI.GraphicDrivers
{
    /// <summary>
    /// Managed driver descriptor for the Chipone CO5300 AMOLED panel driver IC. Common on
    /// 1.x" - 2.x" round and rectangular AMOLED smartwatch / dev boards from Waveshare,
    /// LilyGO, and others. Uses the flash-style hybrid QSPI protocol: 1-line command,
    /// 1-line address, 4-line (quad) data on memory writes.
    ///
    /// Requires firmware built with <c>NF_FEATURE_USE_QSPI_DISPLAY_DRIVER</c> enabled
    /// (the QSPI variant of <c>DisplayInterface</c>). Mutually exclusive with the
    /// standard SPI display driver in a single firmware image; pick one per build.
    ///
    /// Tested first on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch (410 x 502).
    /// Reverse-engineering notes for the chip's QSPI quirks live in the SpawnWear repo
    /// at <c>Notes/co5300-quirks.md</c> on github.com/LostBeard/SpawnWear.
    /// </summary>
    public static class Co5300
    {
        // CO5300 commands (subset; full table in Notes/co5300-quirks.md).
        private enum CO5300_CMD
        {
            SoftwareReset = 0x01,
            SleepIn = 0x10,
            SleepOut = 0x11,
            InversionOff = 0x20,
            InversionOn = 0x21,
            DisplayOff = 0x28,
            DisplayOn = 0x29,
            ColumnAddressSet = 0x2A,
            PageAddressSet = 0x2B,
            MemoryWrite = 0x2C,           // Logical MemoryWrite cmd; the QSPI bus uses
                                          // QspiMemoryWriteCommand (0x32) on the wire.
            MemoryAccessControl = 0x36,
            PixelFormatSet = 0x3A,
            BrightnessNormal = 0x51,
            CtrlDisplay1 = 0x53,
            ContrastEnhancement = 0x58,
            BrightnessHbm = 0x63,
            SpiModeControl = 0xC4,
            VendorPageSelect = 0xFE,
        }

        private enum CO5300_PIXEL_FORMAT
        {
            Pixel16Bit = 0x55, // RGB565
            Pixel18Bit = 0x66, // RGB666
            Pixel24Bit = 0x77, // RGB888
        }

        // Constant 24-bit address phase that accompanies the memory-write QSPI command (0x32)
        // on every CO5300 pixel-stream transaction. Reverse-engineered from the Arduino sample.
        private const uint MemoryWriteAddress = 0x003C00u;

        // QSPI command bytes used on the wire (NOT the same as the logical MemoryWrite cmd).
        private const byte QspiRegisterWrite = 0x02;
        private const byte QspiMemoryWrite = 0x32;

        /// <summary>
        /// Default panel width (Waveshare ESP32-S3-Touch-AMOLED-2.06: 410). Override via
        /// <see cref="GraphicDriver.Width"/> for variants on other boards.
        /// </summary>
        public static ushort Width { get; } = 410;

        /// <summary>
        /// Default panel height (Waveshare ESP32-S3-Touch-AMOLED-2.06: 502). Override via
        /// <see cref="GraphicDriver.Height"/> for variants.
        /// </summary>
        public static ushort Height { get; } = 502;

        private static GraphicDriver _driver;

        /// <summary>
        /// Gets the graphic driver descriptor for the CO5300.
        /// </summary>
        public static GraphicDriver GraphicDriver
        {
            get
            {
                if (_driver == null)
                {
                    _driver = new GraphicDriver
                    {
                        BusType = DisplayBusType.Qspi,
                        QspiRegisterWriteCommand = QspiRegisterWrite,
                        QspiMemoryWriteCommand = QspiMemoryWrite,
                        QspiMemoryWriteAddress = MemoryWriteAddress,

                        MemoryWrite = (byte)CO5300_CMD.MemoryWrite,
                        SetColumnAddress = (byte)CO5300_CMD.ColumnAddressSet,
                        SetRowAddress = (byte)CO5300_CMD.PageAddressSet,
                        BitsPerPixel = 16,
                        Brightness = (byte)CO5300_CMD.BrightnessNormal,
                        SetWindowType = SetWindowType.X16bitsY16Bit,
                        DefaultOrientation = DisplayOrientation.Portrait,

                        // Init sequence reverse-engineered from Arduino_CO5300.h.cpp.
                        InitializationSequence = new byte[]
                        {
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.SleepOut,
                            (byte)GraphicDriverCommandType.Sleep,   12, // 120 ms (units of 10 ms)
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.VendorPageSelect, 0x00,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.SpiModeControl, 0x80,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.PixelFormatSet, (byte)CO5300_PIXEL_FORMAT.Pixel16Bit,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.CtrlDisplay1, 0x20,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.BrightnessHbm, 0xFF,
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.DisplayOn,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.BrightnessNormal, 0xD0,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.ContrastEnhancement, 0x00,
                            (byte)GraphicDriverCommandType.Command, 2, (byte)CO5300_CMD.MemoryAccessControl, 0x00,
                            (byte)GraphicDriverCommandType.Sleep,   1, // 10 ms
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.InversionOff,
                        },

                        PowerModeNormal = new byte[]
                        {
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.SleepOut,
                            (byte)GraphicDriverCommandType.Sleep,   12,
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.DisplayOn,
                            (byte)GraphicDriverCommandType.Sleep,   2,
                        },
                        PowerModeSleep = new byte[]
                        {
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.DisplayOff,
                            (byte)GraphicDriverCommandType.Sleep,   2,
                            (byte)GraphicDriverCommandType.Command, 1, (byte)CO5300_CMD.SleepIn,
                            (byte)GraphicDriverCommandType.Sleep,   12,
                        },
                    };
                }

                return _driver;
            }
        }
    }
}
