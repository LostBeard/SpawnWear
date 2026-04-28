using System;
using System.Diagnostics;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.Runtime.Native;

namespace SpawnWear
{
    /// <summary>
    /// Standard BLE Device Information Service (0x180A).
    /// Reserved for a future second <see cref="GattServiceProvider"/> if/when nanoFramework
    /// supports advertising more than one service reliably on ESP32. Today the watch advertises
    /// only the WiFi/primary service — Manufacturer/Model/Firmware values are also reachable
    /// over the SpawnWear primary service if the PWA wants them sooner.
    /// </summary>
    public class DeviceInfoService
    {
        // Standard Bluetooth SIG UUIDs for Device Information Service
        static readonly Guid ServiceUuid = new("0000180a-0000-1000-8000-00805f9b34fb");
        static readonly Guid ManufacturerNameUuid = new("00002a29-0000-1000-8000-00805f9b34fb");
        static readonly Guid ModelNumberUuid = new("00002a24-0000-1000-8000-00805f9b34fb");
        static readonly Guid FirmwareRevisionUuid = new("00002a26-0000-1000-8000-00805f9b34fb");
        static readonly Guid SoftwareRevisionUuid = new("00002a28-0000-1000-8000-00805f9b34fb");

        public GattServiceProvider ServiceProvider { get; private set; }

        public bool Initialize()
        {
            var result = GattServiceProvider.Create(ServiceUuid);
            if (result.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[DeviceInfo] Failed to create service: " + result.Error);
                return false;
            }

            ServiceProvider = result.ServiceProvider;
            var service = ServiceProvider.Service;

            AddStaticCharacteristic(service, ManufacturerNameUuid, "SpawnDev", "Manufacturer Name");
            AddStaticCharacteristic(service, ModelNumberUuid, "ESP32-S3-Touch-AMOLED-2.06", "Model Number");
            AddStaticCharacteristic(service, FirmwareRevisionUuid, GetFirmwareVersion(), "Firmware Revision");
            AddStaticCharacteristic(service, SoftwareRevisionUuid, "0.1.0", "Software Revision");

            Debug.WriteLine("[DeviceInfo] Service initialized");
            return true;
        }

        static void AddStaticCharacteristic(GattLocalService service, Guid uuid, string value, string description)
        {
            var writer = new DataWriter();
            writer.WriteString(value);

            var parameters = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                UserDescription = description,
                StaticValue = writer.DetachBuffer()
            };

            var charResult = service.CreateCharacteristic(uuid, parameters);
            if (charResult.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[DeviceInfo] Failed to create characteristic: " + description);
            }
        }

        static string GetFirmwareVersion()
        {
            return SystemInfo.Version.ToString();
        }
    }
}
