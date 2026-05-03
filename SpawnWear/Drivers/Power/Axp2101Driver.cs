using System.Device.I2c;

namespace SpawnWear.Drivers.Power
{
    /// <summary>
    /// Minimal AXP2101 PMIC driver for the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch.
    /// Enables the DC1 + ALDO1 rails at 3300 mV which the AMOLED panel needs to be
    /// powered before the CO5300 display init sequence can light it up.
    ///
    /// Rail mapping (from infinition/waveshare-watch-rs/src/peripherals/power.rs init() comments
    /// + the vendor 01_AXP2101 ESP-IDF demo at _vendor-waveshare-demo/.../port_axp2101.cpp):
    ///   DC1   = main 3.3 V rail (always-on for SoC peripherals; voltage at reg 0x82)
    ///   ALDO1 = display / peripheral 3.3 V rail (voltage at reg 0x92)
    ///
    /// Without this init, the watch's AMOLED stays dark even though every CO5300
    /// SPI command appears to send successfully - the panel has no Vdd.
    ///
    /// I2C address: 0x34 (per BoardPins.AxpI2cAddress).
    /// Future expansion: PWR-button via IRQ on GPIO10, battery monitoring, charging
    /// status, low-power-mode coordination across services.
    /// </summary>
    public class Axp2101Driver
    {
        // AXP2101 register map (subset).
        const byte REG_DC_ONOFF = 0x80;     // DC1-DC5 on/off control
        const byte REG_DC_VOL0 = 0x82;      // DCDC1 voltage setting
        const byte REG_LDO_ONOFF0 = 0x90;   // ALDO1-4 + BLDO1-2 + CPUSLDO + DLDO1 on/off
        const byte REG_LDO_ONOFF1 = 0x91;   // DLDO2 on/off
        const byte REG_ALDO1_VOL = 0x92;    // ALDO1 voltage (mV - 500) / 100
        const byte REG_ALDO2_VOL = 0x93;    // ALDO2 voltage (mV - 500) / 100
        const byte REG_ALDO3_VOL = 0x94;    // ALDO3 voltage
        const byte REG_ALDO4_VOL = 0x95;    // ALDO4 voltage

        readonly I2cDevice _i2c;

        public Axp2101Driver(I2cDevice i2c)
        {
            _i2c = i2c;
        }

        /// <summary>
        /// Brings up DC1 and ALDO1 at 3300 mV. Idempotent - existing enable bits in
        /// the on/off register are preserved.
        /// </summary>
        public void EnableDisplayRails()
        {
            // Empirically (verified 2026-05-03 by reading AXP2101 register state),
            // every rail (DC1-4, ALDO1-4, BLDO1-2, CPUSLDO, DLDO1-2) is already
            // enabled at AXP2101 POR / bootloader handoff on this watch. The
            // panel does not need any explicit rail toggling. We re-write the
            // 3.3V voltages defensively (in case a future power-save state has
            // dropped them) and bit-OR the existing enable register so we
            // never accidentally turn off a rail another driver depends on.
            byte dcCtrl = ReadReg(REG_DC_ONOFF);
            byte ldoCtrl = ReadReg(REG_LDO_ONOFF0);

            WriteReg(REG_DC_VOL0, (byte)((3300 - 1500) / 100)); // DC1 = 3.3V
            WriteReg(REG_ALDO1_VOL, (byte)((3300 - 500) / 100)); // ALDO1 = 3.3V
            WriteReg(REG_ALDO2_VOL, (byte)((3300 - 500) / 100)); // ALDO2 = 3.3V
            WriteReg(REG_ALDO3_VOL, (byte)((3300 - 500) / 100)); // ALDO3 = 3.3V

            WriteReg(REG_DC_ONOFF, (byte)(dcCtrl | 0x01));        // DC1 on
            WriteReg(REG_LDO_ONOFF0, (byte)(ldoCtrl | 0x07));    // ALDO1+2+3 on
        }

        public byte ReadReg(byte register)
        {
            byte[] read = new byte[1];
            _i2c.WriteRead(new byte[] { register }, read);
            return read[0];
        }

        public void WriteReg(byte register, byte value)
        {
            _i2c.Write(new byte[] { register, value });
        }
    }
}
