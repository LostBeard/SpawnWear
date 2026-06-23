using System;
using System.Device.Gpio;
using System.Device.I2c;
using System.Threading;

namespace SpawnWear.Drivers.Touch
{
    /// <summary>
    /// Managed driver for the FocalTech FT3168 capacitive touch controller (the panel
    /// driver chip on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch).
    ///
    /// I2C, address 0x38, registers are 8-bit / data is 8-bit.
    ///
    /// Reverse-engineered from `Arduino_FT3x68.cpp` (Waveshare DriveBus library) and the
    /// FT3168 datasheet at <https://files.waveshare.com/wiki/common/FT3168.pdf>.
    /// </summary>
    public sealed class Ft3168Driver : IDisposable
    {
        // Register map (subset - the chip exposes ~256 registers, most of them factory-only).
        const byte RegFingerNum = 0x02; // Number of touches currently active (0..2 for this chip's report buffer).
        const byte RegX1PosH = 0x03;    // High byte of X for touch 1 - low 4 bits are pos[11:8], top 4 bits are event flags.
        const byte RegX1PosL = 0x04;
        const byte RegY1PosH = 0x05;    // Same shape as X1PosH.
        const byte RegY1PosL = 0x06;
        const byte RegX2PosH = 0x09;
        const byte RegX2PosL = 0x0A;
        const byte RegY2PosH = 0x0B;
        const byte RegY2PosL = 0x0C;
        const byte RegDeviceId = 0xA0;        // 0x03 = FT3168.
        const byte RegPowerMode = 0xA5;       // 0=Active, 1=Monitor, 2=Standby, 3=Hibernate.
        const byte RegProximityMode = 0xB0;   // 0=off, 1=on.
        const byte RegGestureMode = 0xD0;     // 0=off, 1=on.
        const byte RegGestureId = 0xD3;       // 0x00=none, 0x20=L, 0x21=R, 0x22=U, 0x23=D, 0x24=double-click.

        const byte ExpectedDeviceId = 0x03;

        readonly I2cDevice _i2c;
        readonly GpioPin _resetPin;
        readonly GpioPin _intPin;
        bool _disposed;

        /// <summary>Raised when the touch controller asserts INT (a touch event is available).</summary>
        public event TouchEventHandler TouchEvent;

        /// <summary>Touch event delegate.</summary>
        public delegate void TouchEventHandler(Ft3168Driver sender, TouchSnapshot snapshot);

        /// <summary>
        /// Construct a driver. The caller owns the I2C device and the optional reset / interrupt pins.
        /// Pass <c>null</c> for either GPIO pin to skip that capability (no hardware reset / no interrupt-driven dispatch).
        /// </summary>
        public Ft3168Driver(I2cDevice i2c, GpioPin resetPin, GpioPin intPin)
        {
            if (i2c == null)
            {
                throw new ArgumentNullException();
            }

            _i2c = i2c;
            _resetPin = resetPin;
            _intPin = intPin;
        }

        /// <summary>
        /// Hardware-reset the chip (if a reset pin was supplied), set power mode to Monitor,
        /// and wire up the INT-pin handler if one was supplied.
        /// </summary>
        public void Initialize()
        {
            // Hardware reset cycle per vendor sample: HIGH -> 1ms -> LOW -> 20ms -> HIGH -> 50ms.
            if (_resetPin != null)
            {
                _resetPin.SetPinMode(PinMode.Output);
                _resetPin.Write(PinValue.High);
                Thread.Sleep(1);
                _resetPin.Write(PinValue.Low);
                Thread.Sleep(20);
                _resetPin.Write(PinValue.High);
                Thread.Sleep(50);
            }

            // Default to Monitor power mode (the chip auto-wakes on touch, sleeps otherwise).
            SetPowerMode(Ft3168PowerMode.Monitor);
            Thread.Sleep(20);

            if (_intPin != null)
            {
                // INT goes LOW for ~1ms each time the chip latches a new report.
                _intPin.SetPinMode(PinMode.InputPullUp);
                _intPin.ValueChanged += OnIntPinChanged;
            }
        }

        /// <summary>Probe the device-ID register. Returns 0x03 for an FT3168.</summary>
        public byte ReadDeviceId() => ReadRegister(RegDeviceId);

        /// <summary>Returns true if the connected chip matches the FT3168 device-ID byte.</summary>
        public bool ProbeIsFt3168() => ReadDeviceId() == ExpectedDeviceId;

        /// <summary>Set the chip's power-state-machine mode.</summary>
        public void SetPowerMode(Ft3168PowerMode mode) => WriteRegister(RegPowerMode, (byte)mode);

        /// <summary>Toggle whether the chip emits gesture-id reports (single-touch swipes / double-click).</summary>
        public void SetGestureRecognition(bool enabled) => WriteRegister(RegGestureMode, (byte)(enabled ? 1 : 0));

        /// <summary>Toggle whether the chip emits proximity-sensing reports (hover detection).</summary>
        public void SetProximitySensing(bool enabled) => WriteRegister(RegProximityMode, (byte)(enabled ? 1 : 0));

        /// <summary>Read the latest gesture id, or <see cref="Ft3168Gesture.None"/> if none is queued.</summary>
        public Ft3168Gesture ReadGesture() => (Ft3168Gesture)ReadRegister(RegGestureId);

        /// <summary>
        /// Read a complete touch snapshot: finger count + up to two (x, y) points.
        /// Coordinates are in the panel's native frame (0..409, 0..501); apply rotation in the UI layer.
        /// </summary>
        public TouchSnapshot ReadTouch()
        {
            // Six bytes from 0x02: FingerNum (0x02), X1H (0x03), X1L (0x04),
            // Y1H (0x05), Y1L (0x06), then one trailing byte at 0x07 we don't use.
            // The earlier "reserved at offset 1" comment was wrong - there is no
            // reserved byte; the register stride between FingerNum and X1H is 1.
            // Reading two-finger data needs two extra reads at 0x09..0x0C; we
            // issue them only if FingerNum >= 2.
            SpanByte writeBuf = new byte[1];
            writeBuf[0] = RegFingerNum;
            SpanByte readBuf = new byte[6];
            lock (BoardSetup.I2cLock) { _i2c.WriteRead(writeBuf, readBuf); }

            byte fingerCount = readBuf[0];
            ushort x1 = Decode12Bit(readBuf[1], readBuf[2]); // X1H | X1L
            ushort y1 = Decode12Bit(readBuf[3], readBuf[4]); // Y1H | Y1L

            ushort x2 = 0;
            ushort y2 = 0;
            if (fingerCount >= 2)
            {
                writeBuf[0] = RegX2PosH;
                SpanByte t2 = new byte[4];
                lock (BoardSetup.I2cLock) { _i2c.WriteRead(writeBuf, t2); }
                x2 = Decode12Bit(t2[0], t2[1]);
                y2 = Decode12Bit(t2[2], t2[3]);
            }

            return new TouchSnapshot(fingerCount, x1, y1, x2, y2);
        }

        void OnIntPinChanged(object sender, PinValueChangedEventArgs args)
        {
            // The chip pulls INT low to signal a new report; ignore the rising edge.
            if (args.ChangeType != PinEventTypes.Falling) return;

            // Reading the snapshot also clears the interrupt latch in the chip's report buffer.
            TouchSnapshot snapshot = ReadTouch();
            TouchEvent?.Invoke(this, snapshot);
        }

        byte ReadRegister(byte reg)
        {
            SpanByte writeBuf = new byte[1];
            writeBuf[0] = reg;
            SpanByte readBuf = new byte[1];
            lock (BoardSetup.I2cLock) { _i2c.WriteRead(writeBuf, readBuf); }
            return readBuf[0];
        }

        void WriteRegister(byte reg, byte value)
        {
            SpanByte buf = new byte[2];
            buf[0] = reg;
            buf[1] = value;
            lock (BoardSetup.I2cLock) { _i2c.Write(buf); }
        }

        // The X/Y high byte uses bits 11:8 in the low nibble; the upper nibble is touch-event flags
        // (Down / Up / Contact) which we ignore here - they are interpretable from finger-count change.
        static ushort Decode12Bit(byte hi, byte lo) => (ushort)(((hi & 0x0F) << 8) | lo);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_intPin != null)
            {
                _intPin.ValueChanged -= OnIntPinChanged;
            }
        }
    }

    /// <summary>FT3168 power-state-machine modes (register 0xA5).</summary>
    public enum Ft3168PowerMode : byte
    {
        Active = 0,
        Monitor = 1,
        Standby = 2,
        Hibernate = 3,
    }

    /// <summary>Single-touch gesture codes (register 0xD3) - requires gesture mode enabled.</summary>
    public enum Ft3168Gesture : byte
    {
        None = 0x00,
        SwipeLeft = 0x20,
        SwipeRight = 0x21,
        SwipeUp = 0x22,
        SwipeDown = 0x23,
        DoubleClick = 0x24,
    }

    /// <summary>Snapshot of the FT3168 touch report buffer.</summary>
    public struct TouchSnapshot
    {
        public byte FingerCount;
        public ushort X1, Y1;
        public ushort X2, Y2;

        public TouchSnapshot(byte fingerCount, ushort x1, ushort y1, ushort x2, ushort y2)
        {
            FingerCount = fingerCount;
            X1 = x1; Y1 = y1;
            X2 = x2; Y2 = y2;
        }
    }
}
