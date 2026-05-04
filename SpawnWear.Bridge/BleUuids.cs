using System;

namespace SpawnWear.Bridge;

/// <summary>
/// SpawnWear BLE GATT UUID namespace.
///
/// IMPORTANT: This file MIRRORS the firmware's <c>SpawnWear/BleUuids.cs</c>.
/// The two files MUST stay in lockstep — the watch advertises the
/// firmware's UUIDs, and the Bridge can only resolve characteristics it
/// looks up by the same UUID. If you change one, change the other in
/// the same commit.
///
/// (When duplication starts to hurt, graduate to a shared
/// <c>SpawnWear.Protocol</c> library multi-targeting <c>netnano1.0;net10.0</c>.
/// Until then, mirror.)
///
/// Custom UUID base: <c>a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb</c>
/// where SSSS = service index, CCCC = characteristic index.
///
/// The "c1" (vs NanoFrameTest1's "c0") keeps device contracts distinct
/// when both PWAs are installed on the same phone.
/// </summary>
public static class BleUuids
{
    /// <summary>The single GATT service the watch advertises.</summary>
    public static readonly Guid WifiServiceUuid = new("a0e4f2c1-0001-1000-8000-00805f9b34fb");

    // WiFi config characteristics
    public static readonly Guid WifiStatusUuid       = new("a0e4f2c1-0001-0001-8000-00805f9b34fb");
    public static readonly Guid WifiScanUuid         = new("a0e4f2c1-0001-0002-8000-00805f9b34fb");
    public static readonly Guid WifiCredentialsUuid  = new("a0e4f2c1-0001-0003-8000-00805f9b34fb");
    public static readonly Guid WifiCommandUuid      = new("a0e4f2c1-0001-0004-8000-00805f9b34fb");

    // Watch profile (battery / IMU / RTC / button events)
    public static readonly Guid BatteryStateUuid     = new("a0e4f2c1-0001-0010-8000-00805f9b34fb");
    public static readonly Guid ImuSampleUuid        = new("a0e4f2c1-0001-0011-8000-00805f9b34fb");
    public static readonly Guid RtcTimeUuid          = new("a0e4f2c1-0001-0012-8000-00805f9b34fb");
    public static readonly Guid ButtonEventUuid      = new("a0e4f2c1-0001-0013-8000-00805f9b34fb");

    // Debug console
    public static readonly Guid DebugLogOutputUuid   = new("a0e4f2c1-0001-00f0-8000-00805f9b34fb");
    public static readonly Guid DebugCommandInputUuid = new("a0e4f2c1-0001-00f1-8000-00805f9b34fb");

    // Pairing service (Phase 7) - Ed25519 pubkey exchange + handshake.
    // Reserved here so Phase 7's first implementation commit doesn't
    // have to renumber. See Plans/phase7-webrtc-handoff.md.
    public static readonly Guid PairingPubKeyUuid    = new("a0e4f2c1-0001-00a0-8000-00805f9b34fb");
    public static readonly Guid PairingHandshakeUuid = new("a0e4f2c1-0001-00a1-8000-00805f9b34fb");

    // WiFi commands (single byte written to WifiCommandUuid)
    public const byte WifiCmdConnect    = 0x01;
    public const byte WifiCmdDisconnect = 0x02;
    public const byte WifiCmdForget     = 0x03;

    // Button event codes (notified on ButtonEventUuid as [button][action])
    public const byte ButtonBoot         = 0x01;
    public const byte ButtonPwr          = 0x02;
    public const byte ActionDown         = 0x01;
    public const byte ActionUp           = 0x02;
    public const byte ActionClick        = 0x03;
    public const byte ActionDoubleClick  = 0x04;
    public const byte ActionLongPress    = 0x05;
}
