using System;

namespace SpawnWear
{
    /// <summary>
    /// BLE service and characteristic UUIDs for the SpawnWear watch firmware.
    /// Custom UUIDs use the base: a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb
    /// where SSSS = service index, CCCC = characteristic index.
    /// (NanoFrameTest1 owns a0e4f2c0-... — SpawnWear is c1 so a phone running both PWAs
    /// can tell the device GATT contracts apart.)
    /// </summary>
    public static class BleUuids
    {
        // WiFi Configuration Service (the primary advertised service — provisioning lives here)
        public static readonly Guid WifiServiceUuid = new("a0e4f2c1-0001-1000-8000-00805f9b34fb");
        public static readonly Guid WifiStatusUuid = new("a0e4f2c1-0001-0001-8000-00805f9b34fb");
        public static readonly Guid WifiScanUuid = new("a0e4f2c1-0001-0002-8000-00805f9b34fb");
        public static readonly Guid WifiCredentialsUuid = new("a0e4f2c1-0001-0003-8000-00805f9b34fb");
        public static readonly Guid WifiCommandUuid = new("a0e4f2c1-0001-0004-8000-00805f9b34fb");

        // Watch Profile Service — battery / charge / IMU / RTC. Lives on the same primary GATT service.
        public static readonly Guid BatteryStateUuid = new("a0e4f2c1-0001-0010-8000-00805f9b34fb");
        public static readonly Guid ImuSampleUuid = new("a0e4f2c1-0001-0011-8000-00805f9b34fb");
        public static readonly Guid RtcTimeUuid = new("a0e4f2c1-0001-0012-8000-00805f9b34fb");
        public static readonly Guid ButtonEventUuid = new("a0e4f2c1-0001-0013-8000-00805f9b34fb");

        // Debug Console Service — log notify + command write. Lives on the primary GATT service.
        public static readonly Guid DebugLogOutputUuid = new("a0e4f2c1-0001-00f0-8000-00805f9b34fb");
        public static readonly Guid DebugCommandInputUuid = new("a0e4f2c1-0001-00f1-8000-00805f9b34fb");

        // Pairing Service (Phase 7) — Ed25519 pubkey exchange + handshake.
        // Reserved here so Phase 7's first implementation commit doesn't
        // have to renumber. See Plans/phase7-webrtc-handoff.md.
        public static readonly Guid PairingPubKeyUuid = new("a0e4f2c1-0001-00a0-8000-00805f9b34fb");
        public static readonly Guid PairingHandshakeUuid = new("a0e4f2c1-0001-00a1-8000-00805f9b34fb");

        // WiFi commands (single byte written to WifiCommandUuid)
        public const byte WifiCmdConnect = 0x01;
        public const byte WifiCmdDisconnect = 0x02;
        public const byte WifiCmdForget = 0x03;

        // Button event codes (notified on ButtonEventUuid as [button][action])
        public const byte ButtonBoot = 0x01;
        public const byte ButtonPwr = 0x02;
        public const byte ActionDown = 0x01;
        public const byte ActionUp = 0x02;
        public const byte ActionClick = 0x03;
        public const byte ActionDoubleClick = 0x04;
        public const byte ActionLongPress = 0x05;
    }
}
