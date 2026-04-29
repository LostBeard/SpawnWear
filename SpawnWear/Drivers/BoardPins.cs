namespace SpawnWear.Drivers
{
    /// <summary>
    /// Pin constants for the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch.
    /// Sourced from the vendor pin_config.h plus the Rust port board.rs.
    /// Documented in detail in README.md (root) and Notes/co5300-quirks.md.
    /// </summary>
    public static class BoardPins
    {
        // -----------------------------------------------------------------------
        // I2C bus (single bus shared by FT3168, AXP2101, QMI8658, PCF85063, ES8311, ES7210)
        // -----------------------------------------------------------------------
        public const int I2cSda = 15;
        public const int I2cScl = 14;
        // ESP32-S3 has two I2C controllers; we use bus 1 for everything on the shared SDA/SCL.
        public const int I2cBusId = 1;

        // -----------------------------------------------------------------------
        // FT3168 capacitive touch
        // -----------------------------------------------------------------------
        public const byte TouchI2cAddress = 0x38;
        public const int TouchInt = 38;
        public const int TouchReset = 9;

        // -----------------------------------------------------------------------
        // AXP2101 PMIC (battery / charging / rails / PWR-button-via-IRQ)
        // -----------------------------------------------------------------------
        public const byte AxpI2cAddress = 0x34;
        public const int AxpIrqLine = 10;

        // -----------------------------------------------------------------------
        // PCF85063 RTC (battery-backed via the AXP2101 coin cell rail)
        // -----------------------------------------------------------------------
        public const byte RtcI2cAddress = 0x51;
        public const int RtcInt = 39;

        // -----------------------------------------------------------------------
        // QMI8658 6-axis IMU
        // -----------------------------------------------------------------------
        public const byte ImuI2cAddress = 0x6B;
        public const int ImuInt = 21;

        // -----------------------------------------------------------------------
        // CO5300 AMOLED via QSPI - 410 x 502
        // -----------------------------------------------------------------------
        public const int LcdReset = 8;
        public const int LcdCs = 12;
        public const int LcdSclk = 11;
        public const int LcdSdio0 = 4;
        public const int LcdSdio1 = 5;
        public const int LcdSdio2 = 6;
        public const int LcdSdio3 = 7;
        public const int LcdTearingEffect = 13;
        public const int LcdWidth = 410;
        public const int LcdHeight = 502;
        public const int LcdColumnOffset = 22; // panel 410 lives inside a wider RAM region

        // -----------------------------------------------------------------------
        // TF / microSD slot (4-bit SDMMC mode)
        // -----------------------------------------------------------------------
        public const int SdClk = 2;
        public const int SdCmd = 1;
        public const int SdData = 3;
        public const int SdCs = 17;

        // -----------------------------------------------------------------------
        // Audio I2S - ES8311 playback + ES7210 dual PDM mic capture
        // -----------------------------------------------------------------------
        public const int I2sMclk = 16;
        public const int I2sBclk = 41;
        public const int I2sLrclk = 45;
        public const int I2sDout = 40;          // codec -> speaker
        public const int I2sDin = 42;           // mic -> codec
        public const int SpeakerPaEnable = 46;  // class-D amp gate
        public const byte AudioCodecI2cAddress = 0x18; // ES8311
        public const byte EchoCancelI2cAddress = 0x40; // ES7210

        // -----------------------------------------------------------------------
        // Buttons
        // -----------------------------------------------------------------------
        // BOOT button on right edge top, wired to GPIO0 directly.
        // PWR button on right edge bottom, routed through AXP2101 - read via the
        // AXP IRQ line (AxpIrqLine) and PMIC register reads, NOT a direct GPIO.
        public const int BootButton = 0;
    }
}
