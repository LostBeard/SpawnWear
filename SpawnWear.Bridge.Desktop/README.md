# SpawnWear.Bridge.Desktop

Desktop-side companion crate for [SpawnWear.Bridge](../SpawnWear.Bridge/). When you want to talk to a SpawnWear watch from a non-browser .NET app — a Windows tray utility, a Linux service, a macOS dashboard — reference this package instead of (or alongside) `SpawnWear.Bridge`.

**Phase 7 work.** This crate is a placeholder until the watch-side WebRTC stack lands. The shape is locked; the implementation arrives when Phase 7 begins. See [Plans/phase7-webrtc-handoff.md](../Plans/phase7-webrtc-handoff.md).

## What lives here (eventually)

- A desktop `ITransport` impl that pairs over BLE via a desktop BLE adapter (Windows: `Windows.Devices.Bluetooth`; Linux: BlueZ DBus; macOS: CoreBluetooth via the appropriate .NET wrapper).
- A WebRTC `ITransport` impl backed by `SpawnDev.RTC`'s desktop path (SipSorcery fork with BouncyCastle DTLS).
- The same `BridgeClient` events the browser-side Bridge surfaces — Battery / IMU / RTC / Button / WiFi / Debug — over whichever transport is active.

## Why a separate crate (not just multi-target the main Bridge)

Multi-targeting one `.csproj` to both `net10.0` (desktop) and `net10.0-browser` requires conditional code, conditional `PackageReference`s, and ships a slightly different surface depending on the consumer's target framework. That's friction every Bridge consumer would pay for the convenience of one package.

Splitting it: `SpawnWear.Bridge` is browser-only and clean; `SpawnWear.Bridge.Desktop` is desktop-only and clean. Common types live in the shared base package; each crate adds its platform-specific transport implementations.

## What this is NOT

- Not yet implemented. The csproj scaffolds + Bridge ProjectReference are here so the future Phase 7 work has a place to land. There's no functional code yet.
