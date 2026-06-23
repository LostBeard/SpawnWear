using System;
using System.Device.I2c;

namespace SpawnWear.Drivers.Rtc
{
    /// <summary>
    /// PCF85063A I2C RTC driver. The watch keeps the chip alive across main-battery
    /// removal via the AXP2101 coin-cell pin (when populated), so wall-clock time
    /// survives reboots and battery swaps.
    ///
    /// Pure managed C# against <see cref="I2cDevice"/>; no nanoFramework IoT package
    /// dependency. Mirrors the Rust port at
    /// <c>_vendor-rust-watch/src/peripherals/rtc.rs</c> which itself was ported from
    /// the Waveshare OLEDS3Watch BSP example.
    ///
    /// Time format on the wire is BCD - all helpers below convert to / from decimal
    /// at the API boundary so callers never see BCD.
    ///
    /// I2C address: 0x51 per <see cref="BoardPins.RtcI2cAddress"/>.
    /// </summary>
    public class Pcf85063Driver
    {
        // PCF85063 register map (subset).
        const byte REG_CTRL1 = 0x00;
        const byte REG_CTRL2 = 0x01;
        const byte REG_SECONDS = 0x04;
        const byte REG_MINUTES = 0x05;
        const byte REG_HOURS = 0x06;
        const byte REG_DAYS = 0x07;
        const byte REG_WEEKDAYS = 0x08;
        const byte REG_MONTHS = 0x09;
        const byte REG_YEARS = 0x0A;

        readonly I2cDevice _i2c;

        public Pcf85063Driver(I2cDevice i2c)
        {
            _i2c = i2c;
        }

        /// <summary>
        /// Snapshot of the chip's current time. <see cref="Year"/> is 2000..2099 (the
        /// chip stores year as 0..99 mapped to 2000..2099 by convention).
        /// </summary>
        public struct RtcTime
        {
            public int Year;
            public int Month;
            public int Day;
            public int Hour;
            public int Minute;
            public int Second;
            public int Weekday; // 0..6, application-defined start day
        }

        /// <summary>
        /// Initialize RTC: clear the STOP bit and force 24-hour mode. Idempotent - if
        /// the chip is already running in 24h mode this is a no-op write.
        /// </summary>
        public void Initialize()
        {
            byte ctrl1 = ReadReg(REG_CTRL1);
            // STOP bit (5) clears -> oscillator runs.
            // 12_24 bit (1) clears -> 24-hour mode.
            byte newCtrl1 = (byte)(ctrl1 & ~0x22);
            if (newCtrl1 != ctrl1)
            {
                WriteReg(REG_CTRL1, newCtrl1);
            }
        }

        /// <summary>
        /// Reads the current date / time from the chip in a single I2C burst.
        /// Returns false if the chip reports its oscillator-stopped (OS) flag, which
        /// indicates the time has not been set since power-up - callers should treat
        /// the snapshot as invalid and call <see cref="Set"/> with a known time first.
        /// </summary>
        public bool TryRead(out RtcTime time)
        {
            byte[] cmd = new byte[] { REG_SECONDS };
            byte[] buf = new byte[7];
            lock (BoardSetup.I2cLock) { _i2c.WriteRead(cmd, buf); }

            // Bit 7 of the seconds register is the OS (oscillator stop) flag.
            bool osFlag = (buf[0] & 0x80) != 0;

            time = new RtcTime
            {
                Second = BcdToDec((byte)(buf[0] & 0x7F)),
                Minute = BcdToDec((byte)(buf[1] & 0x7F)),
                Hour = BcdToDec((byte)(buf[2] & 0x3F)),
                Day = BcdToDec((byte)(buf[3] & 0x3F)),
                Weekday = buf[4] & 0x07,
                Month = BcdToDec((byte)(buf[5] & 0x1F)),
                Year = 2000 + BcdToDec(buf[6]),
            };
            return !osFlag;
        }

        /// <summary>
        /// Writes the given time to the chip. Stops the oscillator briefly (sub-ms)
        /// during the write to avoid a partial-update race, then restarts it.
        /// </summary>
        public void Set(RtcTime time)
        {
            byte ctrl1 = ReadReg(REG_CTRL1);
            WriteReg(REG_CTRL1, (byte)(ctrl1 | 0x20)); // STOP

            WriteReg(REG_SECONDS, DecToBcd(time.Second));
            WriteReg(REG_MINUTES, DecToBcd(time.Minute));
            WriteReg(REG_HOURS, DecToBcd(time.Hour));
            WriteReg(REG_DAYS, DecToBcd(time.Day));
            WriteReg(REG_WEEKDAYS, (byte)(time.Weekday & 0x07));
            WriteReg(REG_MONTHS, DecToBcd(time.Month));
            int yy = time.Year % 100;
            if (yy < 0) yy = 0;
            WriteReg(REG_YEARS, DecToBcd(yy));

            WriteReg(REG_CTRL1, (byte)(ctrl1 & ~0x20)); // RUN
        }

        public byte ReadReg(byte register)
        {
            byte[] read = new byte[1];
            lock (BoardSetup.I2cLock) { _i2c.WriteRead(new byte[] { register }, read); }
            return read[0];
        }

        public void WriteReg(byte register, byte value)
        {
            lock (BoardSetup.I2cLock) { _i2c.Write(new byte[] { register, value }); }
        }

        static int BcdToDec(byte bcd) => (bcd >> 4) * 10 + (bcd & 0x0F);

        static byte DecToBcd(int dec)
        {
            if (dec < 0) dec = 0;
            return (byte)(((dec / 10) << 4) | (dec % 10));
        }
    }
}
