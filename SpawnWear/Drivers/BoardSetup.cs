using System.Device.Gpio;
using System.Device.I2c;
using nanoFramework.Hardware.Esp32;

namespace SpawnWear.Drivers
{
    /// <summary>
    /// Central place that wires the watch's GPIO mux for each subsystem.
    /// Call <see cref="ConfigureI2cBus"/> exactly once at boot before opening
    /// any I2C device that lives on the shared SDA/SCL bus (FT3168, AXP2101,
    /// QMI8658, PCF85063, ES8311, ES7210).
    /// </summary>
    public static class BoardSetup
    {
        private static bool _i2cConfigured;
        private static GpioController _gpio;

        /// <summary>Lazy global GPIO controller. Cheap to call repeatedly.</summary>
        public static GpioController GpioController
        {
            get
            {
                if (_gpio == null)
                {
                    _gpio = new GpioController();
                }
                return _gpio;
            }
        }

        /// <summary>
        /// Mux GPIO15 = SDA, GPIO14 = SCL onto ESP32-S3 I2C bus 1. Idempotent.
        /// </summary>
        public static void ConfigureI2cBus()
        {
            if (_i2cConfigured) return;

            // ESP32-S3 has two I2C controllers. We use bus 1 for all chip-level peripherals
            // on the shared bus. The native firmware also uses I2C0 internally for some
            // configurations - leaving bus 1 free for us avoids contention.
            Configuration.SetPinFunction(BoardPins.I2cSda, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(BoardPins.I2cScl, DeviceFunction.I2C1_CLOCK);

            _i2cConfigured = true;
        }

        /// <summary>
        /// Open an I2C device on the shared peripheral bus. Defaults to 400 kHz fast-mode
        /// which every chip on this watch supports (FT3168 / AXP2101 / QMI8658 / PCF85063 /
        /// ES8311 / ES7210 are all 400 kHz capable).
        /// </summary>
        public static I2cDevice OpenI2cDevice(byte deviceAddress, I2cBusSpeed busSpeed = I2cBusSpeed.FastMode)
        {
            ConfigureI2cBus();
            return I2cDevice.Create(new I2cConnectionSettings(BoardPins.I2cBusId, deviceAddress, busSpeed));
        }
    }
}
