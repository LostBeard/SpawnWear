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
            Debug.WriteLine("[SpawnWear] Boot — Waveshare ESP32-S3-Touch-AMOLED-2.06 watch firmware");

            BluetoothLEServer server = BluetoothLEServer.Instance;
            server.DeviceName = "SpawnWear";

            var debug = new DebugConsoleService();
            var profile = new WatchProfileService();
            var wifi = new WifiConfigService(debug, profile);

            if (!wifi.Initialize())
            {
                Debug.WriteLine("[SpawnWear] BLE initialization failed");
                Thread.Sleep(Timeout.Infinite);
                return;
            }

            // ServiceData nudges Windows + Chrome Web Bluetooth to include the 128-bit primary
            // service UUID in the advertisement payload — service discovery is much more reliable
            // when the central can see the UUID before the GATT connection completes.
            var serviceDataWriter = new DataWriter();
            serviceDataWriter.WriteByte(0x01);

            wifi.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters()
            {
                IsConnectable = true,
                IsDiscoverable = true,
                ServiceData = serviceDataWriter.DetachBuffer()
            });

            debug.Log("[SpawnWear] Advertising as 'SpawnWear'. Pair via Web Bluetooth from the companion PWA.");

            Thread.Sleep(Timeout.Infinite);
        }
    }
}
