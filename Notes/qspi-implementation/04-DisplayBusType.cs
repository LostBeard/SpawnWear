//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

namespace nanoFramework.UI
{
    /// <summary>
    /// Bus type used to drive a display panel. Selected by the firmware build configuration -
    /// only one bus implementation links into a given firmware image at a time.
    /// </summary>
    public enum DisplayBusType : byte
    {
        /// <summary>
        /// Standard MIPI DCS over single-line SPI with a separate DC (Data/Command) GPIO pin.
        /// This is the default and matches every display driver in the existing managed driver
        /// catalog (ILI9341, ST7789, GC9A01, SSD1306, SSD1331, ST7735).
        /// </summary>
        Spi = 0,

        /// <summary>
        /// Hybrid QSPI: 1-line command, 1-line address, 4-line (quad) data. The flash-style
        /// protocol used by CO5300, AXS15231B, RM67162, SH8601A, and similar AMOLED chips on
        /// modern smartwatch and round-display dev boards. No DC pin - command vs data is
        /// encoded in the command byte of the SPI transaction itself.
        /// </summary>
        Qspi = 1,
    }
}
