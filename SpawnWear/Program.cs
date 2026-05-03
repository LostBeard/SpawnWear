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
        // displayStatus="Skip" because Graphics packages are temporarily out for deploy-size bisect.
        static string _displayStatus = "Skip";
        static string _touchStatus = "?";

        public static void Main()
        {
            // Build #14 (2026-05-03): no Graphics. Touch + WifiOnly BLE only.
            // Investigating a deploy regression at ~271 KB total (the size of the full
            // BLE+Graphics+Touch deploy). Smaller deploys land cleanly. This build is
            // ~175 KB and validates the Wifi-only BLE path while we figure out the size
            // issue. Display will return after the deploy issue is resolved.
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

                Debug.WriteLine("[SpawnWear] BLE-4 - Constructing WifiConfigService (no helpers)");
                var wifi = new WifiConfigService(null, null);
                Debug.WriteLine("[SpawnWear] BLE-5 - Calling wifi.InitializeWifiOnly()");
                if (!wifi.InitializeWifiOnly())
                {
                    Debug.WriteLine("[SpawnWear] BLE-6-fail - InitializeWifiOnly returned false");
                    return;
                }
                Debug.WriteLine("[SpawnWear] BLE-6 - Wifi service initialized");

                var serviceDataWriter = new DataWriter();
                serviceDataWriter.WriteByte(0x01);

                wifi.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                    ServiceData = serviceDataWriter.DetachBuffer()
                });
                Debug.WriteLine("[SpawnWear] BLE-7 - Advertising as '" + name + "'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] BLE-EX " + ex.GetType().Name + ": " + ex.Message);
                Debug.WriteLine("[SpawnWear] BLE-EX stack: " + ex.StackTrace);
            }
        }
    }
}
