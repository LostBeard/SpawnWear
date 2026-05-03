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
        // Boot status markers encoded into the BLE device name. Pattern: 'SW-<displayStatus>-<touchStatus>'.
        // displayStatus="Skip" until the custom nanoCLR-with-CO5300-Graphics is built and flashed
        // (see Notes/qspi-display-driver-design.md). Until then the spawnwear-1 Graphics PEs cannot
        // link against the standard nanoCLR runtime (native version mismatch).
        static string _displayStatus = "Skip";
        static string _touchStatus = "?";

        public static void Main()
        {
            // Build #9 (2026-05-03): full original minus display.
            // Touch + BLE all enabled. Display remains parked behind the custom-runtime build.
            Debug.WriteLine("[SpawnWear] M0 - Main reached");

            StartTouchProbe();
            StartBleAdvertising();

            int beat = 0;
            while (true)
            {
                Debug.WriteLine("[SpawnWear] heartbeat #" + beat);
                beat++;
                Thread.Sleep(5000);
            }
        }

        static void StartTouchProbe()
        {
            try
            {
                Debug.WriteLine("[Touch] T1 - Opening I2C device + reset/int pins");
                var touchI2c = BoardSetup.OpenI2cDevice(BoardPins.TouchI2cAddress);
                var resetPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchReset);
                var intPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchInt);

                Debug.WriteLine("[Touch] T2 - Constructing FT3168 driver");
                var touch = new Ft3168Driver(touchI2c, resetPin, intPin);
                Debug.WriteLine("[Touch] T3 - Calling Initialize");
                touch.Initialize();

                Debug.WriteLine("[Touch] T4 - Reading device id");
                byte id = touch.ReadDeviceId();
                _touchStatus = id == 0x03 ? "Tok" : "T" + id.ToString("X2");
                Debug.WriteLine("[Touch] T5 - Device id=0x" + id.ToString("X2") + " status=" + _touchStatus);

                touch.TouchEvent += (sender, snapshot) =>
                {
                    Debug.WriteLine("[Touch] fingers=" + snapshot.FingerCount +
                                    " p1=(" + snapshot.X1 + "," + snapshot.Y1 + ")" +
                                    (snapshot.FingerCount > 1 ? " p2=(" + snapshot.X2 + "," + snapshot.Y2 + ")" : ""));
                };
            }
            catch (Exception ex)
            {
                string t = ex.GetType().Name;
                if (t.Length > 8) t = t.Substring(0, 8);
                _touchStatus = "Tex" + t;
                Debug.WriteLine("[Touch] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void StartBleAdvertising()
        {
            try
            {
                Debug.WriteLine("[SpawnWear] BLE-1 - Calling BluetoothLEServer.Instance");
                BluetoothLEServer server = BluetoothLEServer.Instance;
                Debug.WriteLine("[SpawnWear] BLE-2 - Got BluetoothLEServer.Instance");

                string name = "SW-" + _displayStatus + "-" + _touchStatus;
                if (name.Length > 20) name = name.Substring(0, 20);
                server.DeviceName = name;
                Debug.WriteLine("[SpawnWear] BLE-3 - DeviceName='" + name + "'");

                Debug.WriteLine("[SpawnWear] BLE-4 - Constructing helper services");
                var debug = new DebugConsoleService();
                var profile = new WatchProfileService();
                var wifi = new WifiConfigService(debug, profile);
                Debug.WriteLine("[SpawnWear] BLE-5 - Helper services constructed");

                Debug.WriteLine("[SpawnWear] BLE-6 - Calling wifi.Initialize()");
                if (!wifi.Initialize())
                {
                    Debug.WriteLine("[SpawnWear] BLE-7-fail - wifi.Initialize returned false");
                    return;
                }
                Debug.WriteLine("[SpawnWear] BLE-7 - wifi.Initialize OK");

                var serviceDataWriter = new DataWriter();
                serviceDataWriter.WriteByte(0x01);

                Debug.WriteLine("[SpawnWear] BLE-8 - Calling StartAdvertising");
                wifi.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                    ServiceData = serviceDataWriter.DetachBuffer()
                });
                Debug.WriteLine("[SpawnWear] BLE-9 - Advertising as '" + name + "'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] BLE-EX " + ex.GetType().Name + ": " + ex.Message);
                Debug.WriteLine("[SpawnWear] BLE-EX stack: " + ex.StackTrace);
            }
        }
    }
}
