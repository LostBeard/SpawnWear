# SpawnWear.Bridge — CHANGELOG

All notable changes to `SpawnWear.Bridge` are recorded here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely; SemVer for the package version.

## [Unreleased]

## [0.1.0] — 2026-05-05

Initial public surface.

### Added
- `ITransport` abstraction with `ConnectAsync` / `SendAsync` / `RefreshAsync` / `DisconnectAsync` + `IsConnected` / `PeerName` properties + `ConnectionChanged` / `MessageReceived` events.
- `BleTransport` (Web Bluetooth via SpawnDev.BlazorJS): `requestDevice` filtered on the SpawnWear primary GATT service UUID with `SW-` name-prefix fallback, GATT connect, primary service resolve, every characteristic resolved (battery / IMU / RTC / button / wifi-status / wifi-scan / debug-log on notify side; wifi-cmd / wifi-creds / debug-cmd on write side), `StartNotifications` + `OnCharacteristicValueChanged` subscriptions wired, `Device.OnGATTServerDisconnected` cleanup.
- `WebRtcTransport` stub (Phase 7) with the `BLE-as-signaling` plan documented in code.
- `BridgeClient` with strongly-typed events: `BatteryChanged` / `ImuSampleReceived` / `RtcTimeReceived` / `ButtonEventReceived` / `WifiStatusChanged` / `WifiScanResultsReceived` / `DebugLogReceived`. `RefreshAsync` triggers an on-demand read of every readable characteristic so consumers see current state immediately after pairing.
- Channel ID constants (`ChannelIds.Battery`, `ImuSample`, `RtcTime`, `Button`, `WifiStatus`, `WifiScan`, `WifiCommand`, `WifiCredentials`, `DebugLog`, `DebugCmd`).
- `BleUuids` mirror of the firmware's UUID + byte-constant namespace (drift-locked by the `BleUuidsParityTest` regression test in `SpawnWear.Bridge.Tests`).
- DI registration: `services.AddSpawnWearBridge()` registers `BleTransport` + `BridgeClient` as scoped (one per browser tab).
- Records: `BatteryState`, `ImuSample`, `RtcTime`, `WifiStatus` + `WifiState` enum, `WifiScanResult`, `ButtonEvent` + `WatchButton` + `ButtonAction` enums.
- XML documentation file (`SpawnWear.Bridge.xml`) shipped alongside the dll for IntelliSense in consuming projects.

### Notes
- Browser-only target (net10.0 + `SupportedPlatform: browser`). A future `SpawnWear.Bridge.Desktop` crate will offer the same surface for non-browser .NET consumers using SpawnDev.RTC for WebRTC.
- Wire formats are mirror-copies of firmware schemas in `SpawnWear/BleUuids.cs`, `SpawnWear/WatchProfileService.cs`, and `SpawnWear/WifiConfigService.cs`. See `README.md` for the byte-layout table.
