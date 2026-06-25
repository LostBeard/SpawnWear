# SpawnWear.Bridge.Desktop

Desktop-side companion crate for [SpawnWear.Bridge](../SpawnWear.Bridge/). When you want to talk to a SpawnWear watch from a non-browser .NET app — a Windows tray utility, a Linux service, a macOS dashboard — reference this package instead of (or alongside) `SpawnWear.Bridge`.

**Phase 7 - live.** The watch-side WebRTC stack landed and was proven end to end 2026-06-23. This crate currently ships a working **two-peer WebRTC self-test** (`Program.cs`): it spins up a simulated companion + a simulated watch, both pointed at the real SpawnDev.RTC hub, and proves hub signaling + SDP/ICE + datachannel open + mutual Ed25519 challenge + `TransportMessage` framing end to end. Run it with `dotnet run --project SpawnWear.Bridge.Desktop`. See [`Docs/transport.md`](../Docs/transport.md).

## What lives here

- The WebRTC self-test driver (`Program.cs`) - the non-firmware de-risk path, exercising `WebRtcTransport` over `SpawnDev.RTC`'s desktop path (SipSorcery fork with BouncyCastle DTLS).
- The same `BridgeClient` events the browser-side Bridge surfaces — Battery / IMU / RTC / Button / WiFi / Debug — over whichever transport is active.

## Still to come

- A desktop `ITransport` impl that pairs over BLE via a desktop BLE adapter (Windows: `Windows.Devices.Bluetooth`; Linux: BlueZ DBus; macOS: CoreBluetooth via the appropriate .NET wrapper).

## Why a separate crate (not just multi-target the main Bridge)

Multi-targeting one `.csproj` to both `net10.0` (desktop) and `net10.0-browser` requires conditional code, conditional `PackageReference`s, and ships a slightly different surface depending on the consumer's target framework. That's friction every Bridge consumer would pay for the convenience of one package.

Splitting it: `SpawnWear.Bridge` is browser-only and clean; `SpawnWear.Bridge.Desktop` is desktop-only and clean. Common types live in the shared base package; each crate adds its platform-specific transport implementations.

## What this is NOT (yet)

- Not yet a packaged consumer library - it's currently the WebRTC self-test/de-risk harness. The desktop BLE-adapter `ITransport` is still to come; the WebRTC path is live and exercised by `Program.cs`.
