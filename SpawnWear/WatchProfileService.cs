using System.Diagnostics;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace SpawnWear
{
    /// <summary>
    /// Watch-domain characteristics — battery state, IMU samples, RTC time, button events.
    /// Attached to the primary GATT service alongside WiFi + Debug. Phase 1 stands these up
    /// as plumbing only; Phase 2 wires them to the AXP2101 / QMI8658 / PCF85063 I²C drivers.
    ///
    /// Wire formats (kept compact so they fit in a single notify under default 23-byte ATT MTU):
    ///   BatteryState  — [percent:u8][flags:u8][voltage_mV:u16-LE][current_mA:i16-LE]
    ///                   flags: bit0=charging, bit1=usbVbusPresent, bit2=lowBattery
    ///   ImuSample     — [ax:i16][ay:i16][az:i16][gx:i16][gy:i16][gz:i16]  (LE, accel in mg, gyro in 0.1 dps)
    ///   RtcTime       — [year:u16][month:u8][day:u8][hour:u8][min:u8][sec:u8][weekday:u8]  (LE)
    ///   ButtonEvent   — [button:u8][action:u8]  (see <see cref="BleUuids"/> constants)
    /// </summary>
    public class WatchProfileService
    {
        GattLocalCharacteristic _batteryChar;
        GattLocalCharacteristic _imuChar;
        GattLocalCharacteristic _rtcChar;
        GattLocalCharacteristic _buttonChar;

        bool _batteryHasSubs;
        bool _imuHasSubs;
        bool _rtcHasSubs;
        bool _buttonHasSubs;

        bool _initialized;

        public bool Initialize(GattLocalService service)
        {
            // Battery state — read + notify
            var batParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                UserDescription = "Battery State"
            };
            var batRes = service.CreateCharacteristic(BleUuids.BatteryStateUuid, batParams);
            if (batRes.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[WatchProfile] Battery characteristic failed: " + batRes.Error);
                return false;
            }
            _batteryChar = batRes.Characteristic;
            _batteryChar.SubscribedClientsChanged += (s, _) => _batteryHasSubs = s.SubscribedClients.Length > 0;

            // IMU sample — notify only (high-rate stream)
            var imuParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                UserDescription = "IMU Sample"
            };
            var imuRes = service.CreateCharacteristic(BleUuids.ImuSampleUuid, imuParams);
            if (imuRes.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[WatchProfile] IMU characteristic failed: " + imuRes.Error);
                return false;
            }
            _imuChar = imuRes.Characteristic;
            _imuChar.SubscribedClientsChanged += (s, _) => _imuHasSubs = s.SubscribedClients.Length > 0;

            // RTC time — read + write (write to set, read to get) + notify
            var rtcParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.Notify,
                UserDescription = "RTC Time"
            };
            var rtcRes = service.CreateCharacteristic(BleUuids.RtcTimeUuid, rtcParams);
            if (rtcRes.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[WatchProfile] RTC characteristic failed: " + rtcRes.Error);
                return false;
            }
            _rtcChar = rtcRes.Characteristic;
            _rtcChar.SubscribedClientsChanged += (s, _) => _rtcHasSubs = s.SubscribedClients.Length > 0;

            // Button events — notify only
            var btnParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                UserDescription = "Button Event"
            };
            var btnRes = service.CreateCharacteristic(BleUuids.ButtonEventUuid, btnParams);
            if (btnRes.Error != BluetoothError.Success)
            {
                Debug.WriteLine("[WatchProfile] Button characteristic failed: " + btnRes.Error);
                return false;
            }
            _buttonChar = btnRes.Characteristic;
            _buttonChar.SubscribedClientsChanged += (s, _) => _buttonHasSubs = s.SubscribedClients.Length > 0;

            _initialized = true;
            Debug.WriteLine("[WatchProfile] Characteristics attached to primary service");
            return true;
        }

        public void NotifyBatteryState(byte percent, bool charging, bool usbVbus, bool lowBattery, ushort voltageMv, short currentMa)
        {
            if (!_initialized || !_batteryHasSubs) return;

            byte flags = 0;
            if (charging) flags |= 0x01;
            if (usbVbus) flags |= 0x02;
            if (lowBattery) flags |= 0x04;

            var writer = new DataWriter();
            writer.WriteByte(percent);
            writer.WriteByte(flags);
            writer.WriteUInt16(voltageMv);
            writer.WriteInt16(currentMa);
            _batteryChar.NotifyValue(writer.DetachBuffer());
        }

        public void NotifyImuSample(short ax, short ay, short az, short gx, short gy, short gz)
        {
            if (!_initialized || !_imuHasSubs) return;

            var writer = new DataWriter();
            writer.WriteInt16(ax);
            writer.WriteInt16(ay);
            writer.WriteInt16(az);
            writer.WriteInt16(gx);
            writer.WriteInt16(gy);
            writer.WriteInt16(gz);
            _imuChar.NotifyValue(writer.DetachBuffer());
        }

        public void NotifyRtcTime(ushort year, byte month, byte day, byte hour, byte minute, byte second, byte weekday)
        {
            if (!_initialized || !_rtcHasSubs) return;

            var writer = new DataWriter();
            writer.WriteUInt16(year);
            writer.WriteByte(month);
            writer.WriteByte(day);
            writer.WriteByte(hour);
            writer.WriteByte(minute);
            writer.WriteByte(second);
            writer.WriteByte(weekday);
            _rtcChar.NotifyValue(writer.DetachBuffer());
        }

        public void NotifyButtonEvent(byte button, byte action)
        {
            if (!_initialized || !_buttonHasSubs) return;

            var writer = new DataWriter();
            writer.WriteByte(button);
            writer.WriteByte(action);
            _buttonChar.NotifyValue(writer.DetachBuffer());
        }
    }
}
