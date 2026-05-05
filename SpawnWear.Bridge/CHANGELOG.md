# SpawnWear.Bridge — CHANGELOG

All notable changes to `SpawnWear.Bridge` are recorded here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) loosely; SemVer for the package version.

## [Unreleased]

### Tests (2026-05-04 — Riker)

Three new unit-test files in `SpawnWear.Bridge.Tests` driving real production code paths via real `TransportMessage` bytes through `FakeTransport` / `HookedFakeTransport`. No mocks beyond the wire-stub transports. Bridge.Tests grew 82 → 113 tests (31 new).

- `BridgeClientLogBufferTests.cs` (8 tests) — pins the `_recentLogLines` ring buffer that backs `Console.razor`'s late-mount backfill: capture path, live event coexistence, FIFO ordering, 500-line cap with oldest-evicted, clear semantics, channel-id isolation (battery/button/rtc don't pollute), multi-line frame preservation, snapshot independence.
- `BridgeClientSendPathTests.cs` (13 tests) — pins the exact wire bytes for every outbound BLE write the Companion makes: `SetWifiAsync` order + UTF-8 + null-password normalization + newline-in-SSID rejection, `DisconnectWifiAsync` 0x02, `ForgetWifiAsync` 0x03, `ScanWifiAsync` 0x01, `SendDebugCommandAsync` UTF-8 + empty-rejection, `SendAsync` without transport throws.
- `PairingWebRtcIntegrationTests.cs` (10 tests) — END-TO-END Phase 7a → Phase 7b interlock. Each test runs `PairingFlow.PairAsync` to produce a real `PairingRecord`, then runs `WebRtcChallenge` primitives against that record's stored keys. Covers: companion verifies watch-signed challenge under stored `WatchPubKey`; companion's stored `OurPrivKey` (PKCS8) re-imports + signs verifiable under stored `OurPubKey`; imposter watch with separate keypair fails; tampered nonce + tampered signature both rejected; multi-watch trust-anchor isolation; re-pair invalidates old companion privkey; replay-detection via echoed-nonce mismatch; 1000-call nonce uniqueness; record round-trips with sign-capable fidelity.

Mutation-tested: 4 production-code mutations break specific tests as predicted (capacity 500→100, AddLast→AddFirst, WifiCmdConnect→WifiCmdDisconnect, SSID-newline validation removed); 2 mutations on PairingFlow + WebRtcChallenge break 5 integration tests as predicted.

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
