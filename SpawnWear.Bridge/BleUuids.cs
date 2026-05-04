using System;

namespace SpawnWear.Bridge;

/// <summary>
/// SpawnWear BLE GATT UUID namespace. Mirrors the firmware's
/// SpawnWear/BleUuids.cs - keep these two files in sync.
///
/// Custom UUID base: a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb. The "c1"
/// (not "c0" which NanoFrameTest1 uses) keeps device contracts distinct
/// when both PWAs are installed on the same phone.
/// </summary>
public static class BleUuids
{
    /// <summary>The single GATT service the watch advertises.</summary>
    public static readonly Guid WifiServiceUuid = new("a0e4f2c1-0001-0001-8000-00805f9b34fb");

    // WiFi config characteristics
    public static readonly Guid WifiStatusCharUuid       = new("a0e4f2c1-0001-0010-8000-00805f9b34fb");
    public static readonly Guid WifiScanCharUuid         = new("a0e4f2c1-0001-0011-8000-00805f9b34fb");
    public static readonly Guid WifiCredentialsCharUuid  = new("a0e4f2c1-0001-0012-8000-00805f9b34fb");
    public static readonly Guid WifiCommandCharUuid      = new("a0e4f2c1-0001-0013-8000-00805f9b34fb");

    // Watch profile (battery / IMU / RTC / button events)
    public static readonly Guid BatteryNotifyUuid        = new("a0e4f2c1-0001-0020-8000-00805f9b34fb");
    public static readonly Guid ImuSampleNotifyUuid      = new("a0e4f2c1-0001-0021-8000-00805f9b34fb");
    public static readonly Guid RtcTimeNotifyUuid        = new("a0e4f2c1-0001-0022-8000-00805f9b34fb");
    public static readonly Guid ButtonEventNotifyUuid    = new("a0e4f2c1-0001-0023-8000-00805f9b34fb");

    // Debug console
    public static readonly Guid DebugLogNotifyUuid       = new("a0e4f2c1-0001-0030-8000-00805f9b34fb");
    public static readonly Guid DebugCommandWriteUuid    = new("a0e4f2c1-0001-0031-8000-00805f9b34fb");
}
