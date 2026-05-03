using System;
using System.Diagnostics;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace SpawnWear
{
    public class Program
    {
        public static void Main()
        {
            // Diagnostic build #6 (2026-05-03): minimal BLE re-introduced.
            // Each step is a separate Debug.WriteLine so VS Output narrates how far we got.
            // Set breakpoints on M1, M2, M3, M4, M5 to step through and catch the exception.
            Debug.WriteLine("[SpawnWear] M0 - Main reached");

            try
            {
                Debug.WriteLine("[SpawnWear] M1 - About to call BluetoothLEServer.Instance");
                BluetoothLEServer server = BluetoothLEServer.Instance;
                Debug.WriteLine("[SpawnWear] M2 - Got BluetoothLEServer.Instance OK");

                server.DeviceName = "SW-MIN";
                Debug.WriteLine("[SpawnWear] M3 - DeviceName='SW-MIN' set");

                var result = GattServiceProvider.Create(BleUuids.WifiServiceUuid);
                Debug.WriteLine("[SpawnWear] M4 - GattServiceProvider.Create returned, error=" + result.Error);

                if (result.Error != BluetoothError.Success)
                {
                    Debug.WriteLine("[SpawnWear] M4-fail - bailing");
                }
                else
                {
                    result.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                    {
                        IsConnectable = true,
                        IsDiscoverable = true,
                    });
                    Debug.WriteLine("[SpawnWear] M5 - StartAdvertising returned, advertising as 'SW-MIN'");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] EX " + ex.GetType().Name + ": " + ex.Message);
                Debug.WriteLine("[SpawnWear] EX stack: " + ex.StackTrace);
            }

            // Keep Main alive so we can observe BLE state externally + via VS
            int beat = 0;
            while (true)
            {
                Debug.WriteLine("[SpawnWear] heartbeat #" + beat);
                beat++;
                Thread.Sleep(5000);
            }
        }
    }
}
