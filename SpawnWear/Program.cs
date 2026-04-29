using System;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using SpawnWear.Drivers;
using SpawnWear.Drivers.Touch;

namespace SpawnWear
{
    public class Program
    {
        public static void Main()
        {
            Debug.WriteLine("[SpawnWear] Boot - Waveshare ESP32-S3-Touch-AMOLED-2.06 watch firmware");

            StartTouchProbe();
            StartBleAdvertising();

            Thread.Sleep(Timeout.Infinite);
        }

        // -------------------------------------------------------------------
        // Touch (FT3168) - probe at boot, log every touch event for now.
        // Will be lifted into a system service once the display + UI shell exist.
        // -------------------------------------------------------------------
        static void StartTouchProbe()
        {
            try
            {
                var touchI2c = BoardSetup.OpenI2cDevice(BoardPins.TouchI2cAddress);
                var resetPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchReset);
                var intPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchInt);

                var touch = new Ft3168Driver(touchI2c, resetPin, intPin);
                touch.Initialize();

                byte id = touch.ReadDeviceId();
                Debug.WriteLine("[SpawnWear] FT3168 device id = 0x" + id.ToString("X2") + (id == 0x03 ? " (OK)" : " (UNEXPECTED)"));

                touch.TouchEvent += (sender, snapshot) =>
                {
                    Debug.WriteLine("[Touch] fingers=" + snapshot.FingerCount +
                                    " p1=(" + snapshot.X1 + "," + snapshot.Y1 + ")" +
                                    (snapshot.FingerCount > 1 ? " p2=(" + snapshot.X2 + "," + snapshot.Y2 + ")" : ""));
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] Touch init failed: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // BLE - same scaffold as before; still useful for headless debugging
        // until the on-device Settings shell exists.
        // -------------------------------------------------------------------
        static void StartBleAdvertising()
        {
            try
            {
                BluetoothLEServer server = BluetoothLEServer.Instance;
                server.DeviceName = "SpawnWear";

                var debug = new DebugConsoleService();
                var profile = new WatchProfileService();
                var wifi = new WifiConfigService(debug, profile);

                if (!wifi.Initialize())
                {
                    Debug.WriteLine("[SpawnWear] BLE initialization failed");
                    return;
                }

                var serviceDataWriter = new DataWriter();
                serviceDataWriter.WriteByte(0x01);

                wifi.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                    ServiceData = serviceDataWriter.DetachBuffer()
                });

                debug.Log("[SpawnWear] Advertising as 'SpawnWear'.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] BLE start failed: " + ex.Message);
            }
        }
    }
}
