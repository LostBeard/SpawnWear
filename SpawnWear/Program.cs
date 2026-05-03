using System;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace SpawnWear
{
    public class Program
    {
        // Boot status markers encoded into the BLE device name so an external
        // BLE scan can read the boot state without a debugger attach.
        // Display + touch are intentionally skipped at this stage of bring-up
        // (need AXP2101 PMIC rail-enable code first - see Notes/flashing.md).
        static string _displayStatus = "Skip";
        static string _touchStatus = "Skip";

        public static void Main()
        {
            // Diagnostic build #7 (2026-05-03): full helper-service BLE chain.
            // Display + touch still skipped (no AXP2101 driver yet).
            // Validates that WifiConfigService + DebugConsoleService + WatchProfileService
            // GATT layout works on this watch. Expected advert: 'SW-Skip-Skip'.
            Debug.WriteLine("[SpawnWear] M0 - Main reached");

            StartBleAdvertising();

            int beat = 0;
            while (true)
            {
                Debug.WriteLine("[SpawnWear] heartbeat #" + beat);
                beat++;
                Thread.Sleep(5000);
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
