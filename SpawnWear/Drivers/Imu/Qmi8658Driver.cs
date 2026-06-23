using System;
using System.Device.I2c;

namespace SpawnWear.Drivers.Imu
{
    /// <summary>
    /// QCST QMI8658 / QMI8658A 6-axis IMU (3-axis accelerometer + 3-axis gyroscope
    /// + on-die temperature) I2C driver for the SpawnWear watch.
    ///
    /// Pure managed C# against <see cref="I2cDevice"/>; no nanoFramework IoT package
    /// dependency. Mirrors the style of
    /// <c>SpawnWear.Drivers.Rtc.Pcf85063Driver</c> - constructor takes an
    /// <see cref="I2cDevice"/>, simple <see cref="ReadReg"/> / <see cref="WriteReg"/>
    /// helpers over <c>WriteRead</c> / <c>Write</c>, plain arrays and bit shifts only
    /// (no Span, no unsafe, no LINQ) so it transpiles cleanly to nanoFramework.
    ///
    /// I2C address: 0x6B per <see cref="BoardPins.ImuI2cAddress"/> (SA0 high; the
    /// alternate 0x6A is SA0 low). INT line: GPIO21 (<see cref="BoardPins.ImuInt"/>),
    /// not used by this polling driver.
    ///
    /// All register addresses, configuration bit layouts, and the LSB-per-g /
    /// LSB-per-dps / LSB-per-degC scale factors below were read from the Waveshare
    /// vendor SensorLib QMI8658 driver:
    ///   _vendor-waveshare-demo/examples/Arduino-v3.2.0/libraries/SensorLib/src/REG/QMI8658Constants.h
    ///   _vendor-waveshare-demo/examples/Arduino-v3.2.0/libraries/SensorLib/src/SensorQMI8658.hpp
    /// Nothing here is guessed - each constant cites its vendor source line in a comment.
    /// </summary>
    public class Qmi8658Driver
    {
        // -------------------------------------------------------------------
        // Register map - from QMI8658Constants.h.
        // -------------------------------------------------------------------

        // General purpose registers.
        const byte REG_WHOAMI = 0x00;   // QMI8658Constants.h:49  QMI8658_REG_WHOAMI
        const byte REG_REVISION = 0x01; // QMI8658Constants.h:50  QMI8658_REG_REVISION

        // Setup / control registers.
        const byte REG_CTRL1 = 0x02;    // QMI8658Constants.h:54  serial interface + ADDR auto-increment + INT pin enables
        const byte REG_CTRL2 = 0x03;    // QMI8658Constants.h:55  accelerometer: range (bits 6:4) + ODR (bits 3:0)
        const byte REG_CTRL3 = 0x04;    // QMI8658Constants.h:56  gyroscope:     range (bits 6:4) + ODR (bits 3:0)
        const byte REG_CTRL5 = 0x06;    // QMI8658Constants.h:58  accel/gyro low-pass filter config (left at reset = LPF off)
        const byte REG_CTRL7 = 0x08;    // QMI8658Constants.h:60  sensor enable: bit0 = accel, bit1 = gyro

        // Status register - data availability flags.
        const byte REG_STATUS0 = 0x2E;  // QMI8658Constants.h:83  bit0 = accel data avail, bit1 = gyro data avail

        // Data output registers - 16-bit two's complement, little-endian (L then H).
        const byte REG_TEMPERATURE_L = 0x33; // QMI8658Constants.h:92
        const byte REG_AX_L = 0x35;          // QMI8658Constants.h:94  accel X low byte - start of the 12-byte accel+gyro burst
        // AX_L 0x35 .. GZ_H 0x40 is a contiguous 12-byte block (QMI8658Constants.h:94-105):
        //   AX_L AX_H AY_L AY_H AZ_L AZ_H GX_L GX_H GY_L GY_H GZ_L GZ_H

        // Reset register.
        const byte REG_RESET = 0x60;         // QMI8658Constants.h:133

        // -------------------------------------------------------------------
        // Constant / expected values - from QMI8658Constants.h.
        // -------------------------------------------------------------------

        // Expected WHO_AM_I value at register 0x00. QMI8658Constants.h:43
        //   static constexpr uint8_t QMI8658_REG_WHOAMI_DEFAULT = 0x05;
        const byte WHOAMI_EXPECTED = 0x05;

        // Soft-reset command byte written to REG_RESET. QMI8658Constants.h:45
        //   static constexpr uint8_t QMI8658_REG_RESET_DEFAULT = 0xB0;
        const byte RESET_CMD = 0xB0;

        // STATUS0 data-available bits. QMI8658Constants.h:139-140
        const byte STATUS0_ACCEL_AVAIL = 0x01;
        const byte STATUS0_GYRO_AVAIL = 0x02;

        // -------------------------------------------------------------------
        // Selected configuration.
        //
        // Accelerometer: +/-8 g full scale, 500 Hz ODR.
        // Gyroscope:     +/-1024 dps full scale, 448.4 Hz ODR.
        //
        // (The requested gyro +/-2048 dps is not a hardware option - the QMI8658
        //  gyro full-scale tops out at +/-1024 dps per the GyroRange enum in
        //  SensorQMI8658.hpp:54-62, so 1024 dps is used as the widest available range.)
        //
        // CTRL2/CTRL3 byte layout (SensorQMI8658.hpp:397,413,449,468):
        //   accel: writeRegister(CTRL2, 0x8F, range << 4); writeRegister(CTRL2, 0xF0, odr)
        //   gyro:  writeRegister(CTRL3, 0x8F, range << 4); writeRegister(CTRL3, 0xF0, odr)
        // i.e. range occupies bits 6:4, ODR occupies bits 3:0. Bit7 (self-test) left 0.
        // -------------------------------------------------------------------

        // AccelRange enum (SensorQMI8658.hpp:47-52): ACC_RANGE_2G=0, 4G=1, 8G=2, 16G=3.
        // 8 g -> (2 << 4) = 0x20.
        // AccelODR enum (SensorQMI8658.hpp:66-77): ACC_ODR_1000Hz=3, 500Hz=4, 250Hz=5, ...
        // 500 Hz -> 0x04.
        // CTRL2 = 0x20 | 0x04 = 0x24  -> accel +/-8 g, 500 Hz, no self-test.
        const byte CTRL2_VALUE = 0x24;

        // GyroRange enum (SensorQMI8658.hpp:54-62): 16DPS=0, 32=1, 64=2, 128=3, 256=4,
        //   512=5, 1024DPS=6.  1024 dps -> (6 << 4) = 0x60.
        // GyroODR enum (SensorQMI8658.hpp:79-89): 7174.4Hz=0, 3587.2=1, 1793.6=2,
        //   896.8=3, 448.4Hz=4, 224.2=5, ...  448.4 Hz -> 0x04.
        // CTRL3 = 0x60 | 0x04 = 0x64  -> gyro +/-1024 dps, 448.4 Hz, no self-test.
        const byte CTRL3_VALUE = 0x64;

        // CTRL1: bit6 = EN.ADDR_AI (register address auto-increment on burst reads),
        // set by reset() in SensorQMI8658.hpp:307/318. We keep the serial-interface
        // bits at their reset state and only assert auto-increment.
        //   bit6 = 1 -> 0x40.
        const byte CTRL1_VALUE = 0x40;

        // CTRL7: bit0 = accel enable (SensorQMI8658.hpp:771), bit1 = gyro enable
        // (SensorQMI8658.hpp:798). Enable both -> 0x03.
        // (QMI8658Constants.h:146 QMI8658_ACCEL_GYRO_EN_MASK = 0x03.)
        const byte CTRL7_VALUE = 0x03;

        // -------------------------------------------------------------------
        // Scale factors - EXACT values from SensorQMI8658.hpp.
        // -------------------------------------------------------------------

        // Accelerometer sensitivity for the +/-8 g range.
        // SensorQMI8658.hpp:408  accelScales = 8.0 / 32768.0;  (raw count -> g)
        // 8 / 32768 = 1 / 4096, i.e. 4096 LSB per g.
        const float ACCEL_SCALE_G_PER_LSB = 8.0f / 32768.0f;

        // Gyroscope sensitivity for the +/-1024 dps range.
        // SensorQMI8658.hpp:464  gyroScales = 1024.0 / 32768.0;  (raw count -> dps)
        // 1024 / 32768 = 1 / 32, i.e. 32 LSB per dps.
        const float GYRO_SCALE_DPS_PER_LSB = 1024.0f / 32768.0f;

        // Temperature conversion. SensorQMI8658.hpp:350-356 getTemperature_C():
        //   return (float)buffer[1] + ((float)buffer[0] / 256.0);
        // High byte is the integer degrees C, low byte is the 1/256 fraction
        // -> 256 LSB per degree C, signed.
        const float TEMP_LSB_PER_DEGC = 256.0f;

        readonly I2cDevice _i2c;

        public Qmi8658Driver(I2cDevice i2c)
        {
            _i2c = i2c;
        }

        /// <summary>
        /// A single synchronized snapshot of all six motion axes plus die
        /// temperature. Accel is in g, gyro is in degrees-per-second, temp in degC.
        /// </summary>
        public struct ImuSample
        {
            public float AccelX; // g
            public float AccelY; // g
            public float AccelZ; // g
            public float GyroX;  // dps
            public float GyroY;  // dps
            public float GyroZ;  // dps
            public float TempC;  // degrees Celsius
        }

        /// <summary>
        /// Reads WHO_AM_I (register 0x00) and returns true iff it matches the
        /// expected QMI8658 device id (0x05, per
        /// QMI8658Constants.h QMI8658_REG_WHOAMI_DEFAULT).
        /// </summary>
        public bool Probe()
        {
            byte id = ReadReg(REG_WHOAMI);
            return id == WHOAMI_EXPECTED;
        }

        /// <summary>
        /// Reads the silicon revision register (0x01). Informational only - the QMI8658
        /// SensorLib driver exposes this as getChipID(). Not used for presence detection
        /// (use <see cref="Probe"/> for that).
        /// </summary>
        public byte ReadRevision()
        {
            return ReadReg(REG_REVISION);
        }

        /// <summary>
        /// Issues a soft reset (writes 0xB0 to REG_RESET, 0x60). Maximum 15 ms for the
        /// reset to complete per the datasheet; callers should pause briefly before
        /// <see cref="Initialize"/>. Optional - <see cref="Initialize"/> is itself
        /// idempotent and does not require a prior reset.
        /// </summary>
        public void Reset()
        {
            WriteReg(REG_RESET, RESET_CMD);
        }

        /// <summary>
        /// Configures the IMU to sensible polling defaults and enables both sensors:
        ///   - CTRL1 = 0x40: register address auto-increment on (EN.ADDR_AI), so the
        ///     12-byte accel+gyro block can be burst-read in one transaction.
        ///   - CTRL2 = 0x24: accelerometer +/-8 g, 500 Hz ODR.
        ///   - CTRL3 = 0x64: gyroscope +/-1024 dps, 448.4 Hz ODR.
        ///   - CTRL7 = 0x03: accelerometer + gyroscope enabled.
        /// Low-pass filtering (CTRL5) is left at its reset state (off).
        ///
        /// Idempotent: every write is an absolute register value, so calling this
        /// repeatedly leaves the chip in the same state.
        /// </summary>
        public void Initialize()
        {
            // Address auto-increment first so subsequent multi-byte reads walk forward.
            WriteReg(REG_CTRL1, CTRL1_VALUE);

            // Per-sensor range + ODR.
            WriteReg(REG_CTRL2, CTRL2_VALUE);
            WriteReg(REG_CTRL3, CTRL3_VALUE);

            // Enable accelerometer + gyroscope last.
            WriteReg(REG_CTRL7, CTRL7_VALUE);
        }

        /// <summary>
        /// Burst-reads temperature + the six motion axes, converts raw counts to
        /// g / dps / degC using the exact vendor scale factors, and returns the
        /// snapshot in <paramref name="sample"/>.
        ///
        /// Returns false (with a zeroed sample) if STATUS0 reports neither accel nor
        /// gyro data is available yet - callers can poll until it returns true.
        /// </summary>
        public bool TryRead(out ImuSample sample)
        {
            sample = new ImuSample();

            byte status0 = ReadReg(REG_STATUS0);
            bool anyAvail =
                (status0 & STATUS0_ACCEL_AVAIL) != 0 ||
                (status0 & STATUS0_GYRO_AVAIL) != 0;
            if (!anyAvail)
            {
                return false;
            }

            // Temperature: 2 bytes at 0x33 (L = 1/256 fraction, H = integer degC).
            byte[] temp = new byte[2];
            ReadRegs(REG_TEMPERATURE_L, temp);
            // Signed: high byte carries the sign of the integer part.
            short tempRaw = (short)((temp[1] << 8) | temp[0]);
            sample.TempC = tempRaw / TEMP_LSB_PER_DEGC;

            // Accel + gyro: contiguous 12-byte block at 0x35 (little-endian L,H pairs).
            byte[] d = new byte[12];
            ReadRegs(REG_AX_L, d);

            short ax = (short)((d[1] << 8) | d[0]);
            short ay = (short)((d[3] << 8) | d[2]);
            short az = (short)((d[5] << 8) | d[4]);
            short gx = (short)((d[7] << 8) | d[6]);
            short gy = (short)((d[9] << 8) | d[8]);
            short gz = (short)((d[11] << 8) | d[10]);

            sample.AccelX = ax * ACCEL_SCALE_G_PER_LSB;
            sample.AccelY = ay * ACCEL_SCALE_G_PER_LSB;
            sample.AccelZ = az * ACCEL_SCALE_G_PER_LSB;

            sample.GyroX = gx * GYRO_SCALE_DPS_PER_LSB;
            sample.GyroY = gy * GYRO_SCALE_DPS_PER_LSB;
            sample.GyroZ = gz * GYRO_SCALE_DPS_PER_LSB;

            return true;
        }

        /// <summary>
        /// Reads a single register.
        /// </summary>
        public byte ReadReg(byte register)
        {
            byte[] read = new byte[1];
            lock (BoardSetup.I2cLock) { _i2c.WriteRead(new byte[] { register }, read); }
            return read[0];
        }

        /// <summary>
        /// Writes a single register.
        /// </summary>
        public void WriteReg(byte register, byte value)
        {
            lock (BoardSetup.I2cLock) { _i2c.Write(new byte[] { register, value }); }
        }

        /// <summary>
        /// Burst-reads <paramref name="buf"/>.Length bytes starting at
        /// <paramref name="start"/>. Relies on the chip's address auto-increment
        /// (CTRL1 bit6, enabled in <see cref="Initialize"/>).
        /// </summary>
        public void ReadRegs(byte start, byte[] buf)
        {
            lock (BoardSetup.I2cLock) { _i2c.WriteRead(new byte[] { start }, buf); }
        }
    }
}
