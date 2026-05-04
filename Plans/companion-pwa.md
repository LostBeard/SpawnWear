# SpawnWear.Companion (Blazor WASM PWA) + SpawnWear.Bridge (RCL)

A complementary web app that mirrors the watch over BLE + WiFi + WebRTC. **Optional** - the watch is fully functional without it. The PWA is a remote, not a tether.

Phase 4 starts the PWA in parallel with the watch's Settings app, since both surfaces need the same configuration data and writing them in lockstep prevents drift.

## Two projects, not one

The companion ships as **two** distinct .NET projects so any third-party Blazor app (or future SpawnWear.Bridge.Desktop consumer) can pair with the watch without copy-pasting:

- **`SpawnWear.Bridge`** - Razor Class Library (`Microsoft.NET.Sdk.Razor`, `net10.0`, browser platform). Holds **all watch-interaction code**: ITransport abstraction, BleTransport (Web Bluetooth), WebRtcTransport (SpawnDev.RTC), BridgeClient (typed events + commands), BleUuids namespace, channel-id constants, BLE / WebRTC payload schemas. Consumers add `<ProjectReference>` (or NuGet ref later) + `builder.Services.AddSpawnWearBridge()` and they have a `BridgeClient` injected wherever they want it.
- **`SpawnWear.Companion`** - Blazor WebAssembly PWA (`Microsoft.NET.Sdk.BlazorWebAssembly`, `net10.0`). Reference UI on top of the Bridge. Consists of pages (Home, Apps, Mirror, Console) that demonstrate everything the Bridge can do. Doubles as TJ's daily-driver remote.

A future **`SpawnWear.Bridge.Desktop`** crate (no work today; Phase 7 territory) targets `net10.0` + a desktop BLE adapter, exposes the same surface, and reuses the WebRTC transport from `SpawnWear.Bridge` (since `SpawnDev.RTC` works on both browser and desktop). Two desktop apps on different LANs can then peer-to-peer with the watch via BLE-as-signaling + WebRTC media.

## Why have one at all

Three concrete reasons, in priority order:

1. **First-time WiFi setup before the on-device keyboard is comfortable.** Typing a 12-character WPA2 password on a 2.06" panel is painful. The PWA's job is to take that pain off the watch.
2. **Live debugging from a laptop.** Pixel-perfect remote view of the watch's screen + console log streaming over BLE is the difference between "swap COM cables and re-flash" and "open a browser tab."
3. **Showing off.** SpawnDev.BlazorJS exists to prove Blazor WASM PWAs can be first-class apps. The companion proves SpawnWear's surface is rich enough to drive from a browser.

## Architecture

Same C# language on both sides. The watch firmware exposes its system services over three transports:

- **BLE GATT** - always available, low bandwidth (a few KB/s sustained), works without router or WiFi config
- **WiFi HTTP** - faster, supports binary screenshot streaming, requires an SSID + password to be configured already
- **WebRTC data channel** (Phase 7) - peer-to-peer high-bandwidth (audio, video, large file transfer). Signaling rides on BLE so the two devices never need to share a network.

The Bridge picks whichever is reachable. All three transports terminate at the same in-firmware service host, so the consumer's view is consistent regardless of which path it took.

```
┌──────────────────────────────────────────────────────────────────┐
│  SpawnWear.Companion (Blazor WASM PWA, runs in browser)          │
│  - References SpawnWear.Bridge                                   │
│  - Pages: Home, Apps, Mirror, Console                            │
│  - Mirrors every Settings page + every built-in app              │
└──────────────────────┬───────────────────────────────────────────┘
                       │ injects BridgeClient
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│  SpawnWear.Bridge (Razor Class Library)                          │
│  - ITransport (BleTransport, WebRtcTransport)                    │
│  - BridgeClient (typed events + commands)                        │
│  - SpawnDev.BlazorJS for Web Bluetooth                           │
│  - SpawnDev.RTC for WebRTC (Phase 7)                             │
└──────────────────────┬───────────────────────────────────────────┘
                       │
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
   BLE GATT      HTTP/WS         WebRTC data channel
   (always-on)   (when WiFi up)  (Phase 7, BLE signaling)
        │              │              │
        ▼              ▼              ▼
┌──────────────────────────────────────────────────────────────────┐
│  Watch firmware (SpawnWear)                                      │
│  - WatchProfileService (BLE GATT) → exposes RTC, battery, ID     │
│  - DebugConsoleService (BLE GATT notify) → live Debug.WriteLine  │
│  - WifiConfigService (BLE GATT) → SSID + password write          │
│  - HttpServer (port 8080) → /screenshot.bin + JSON system info   │
└──────────────────────────────────────────────────────────────────┘
```

## Phase plan

### Phase 4a - Provisioning (concurrent with Settings app)

- [x] **`SpawnWear.Bridge` RCL** (2026-05-05) - SpawnDev.BlazorJS 3.5.3 + SpawnDev.RTC 1.1.0; ITransport abstraction; BridgeClient with typed events; AddSpawnWearBridge DI extension; BleUuids + ChannelIds duplicated from firmware (drift-locked by `BleUuidsParityTest`).
- [x] **`SpawnWear.Companion` PWA** (2026-05-05) - Blazor WASM PWA + service worker; six pages (Home / Stats / WiFi / Mirror / Apps / Console); ProjectReference to Bridge; SpawnWear-branded dark theme app-wide; persistent StatusBar component across all pages.
- [x] **BleTransport real implementation** (2026-05-05) - Web Bluetooth `RequestDevice` filtered on `WifiServiceUuid` + `SW-` name-prefix fallback, every characteristic resolved (battery / IMU / RTC / button / wifi-status / wifi-scan / debug-log on notify side; wifi-cmd / wifi-creds / debug-cmd on write side), `StartNotifications` + `OnCharacteristicValueChanged` subscriptions wired, `Device.OnGATTServerDisconnected` cleanup. SpawnDev.BlazorJS typed wrappers throughout - no raw JS, no IJSRuntime.
- [x] **`Home.razor`** (2026-05-05) - Connect button + live Battery / IMU / Last button / Last log cards.
- [x] **`Stats.razor`** (2026-05-05) - All-channel telemetry dashboard with notify counters + 1Hz IMU rate gauge; useful for sanity-checking firmware notify rates.
- [x] **`Wifi.razor`** (2026-05-05) - SSID + password form (`SSID\nPassword` UTF-8 to credentials char), Save & connect / Disconnect / Forget command bytes, "Scan networks" trigger + RSSI-bar list of results, watch-reported status pill.
- [x] **`Console.razor`** (2026-05-05) - Live `Debug.WriteLine` stream from the watch (UTF-8 from `DebugLogOutputUuid`); command-line input writes UTF-8 to `DebugCommandInputUuid`. Capped at 500 lines.
- [ ] **Verify on real silicon** - browser pairing UI + GATT subscription against the actual watch firmware on TJ's daily watch.

### Phase 4b - Debug console mirror

- [x] **DebugConsoleService BLE notify subscriber** (2026-05-05) - `Console.razor`, see above.
- [x] **PWA-to-watch debug commands** (2026-05-05) - command bar in `Console.razor` writes UTF-8 to `DebugCommandInputUuid`. Watch-side handler is wired in firmware's DebugConsoleService.

### Phase 4c - Live screen mirror over WiFi

- [x] **`Mirror.razor`** (2026-05-05) - Watch URL input (auto-fills from `WifiStatusChanged` IP), Refresh button + 1 Hz auto-refresh toggle (suspends when tab hidden via Page Visibility API), RGB565 BE → RGBA8 conversion in managed C#, single `ImageData` push to `<canvas>` via SpawnDev.BlazorJS typed Canvas API. Cache-buster query string per fetch.
- [x] **CORS headers on watch HTTP server** (2026-05-05) - `Access-Control-Allow-Origin: *` + OPTIONS preflight so the PWA can fetch `/screenshot.bin` from a different origin.
- [ ] **Tap-to-touch on Mirror canvas** - PWA-side tap handler scales canvas coords to panel coords and POSTs to `/touch`; watch-side `/touch` endpoint injects into `ScreenNavigator.HandleTap`. (Firmware-side endpoint half-built in working tree, on hold pending Captain sign-off.)

### Phase 5 - Drop-on-watch app installer

- [x] **`Apps.razor`** (2026-05-05) - click-to-pick a `.pe` file, POSTs raw bytes to the watch's `http://<watch-ip>/loadapp` endpoint. Pulls watch URL from `WifiStatusChanged`. Hooks the firmware's existing dynamic-load path (reflection-loaded, pushed onto screen stack as foreground app).
- [ ] Drag-drop binary file handoff (currently click-to-pick only; needs a typed `DataTransfer.files` wrapper round in SpawnDev.BlazorJS to work cleanly through Blazor's `DragEventArgs`).

### Phase 6 onwards - Per-app mirrors

Each built-in app gets a corresponding PWA page that drives its UI remotely. Settings and Clock are easiest (mostly read-write of state); Voice Recorder and AI Assistant need bidirectional audio over WebRTC.

### Phase 7 - BLE pairing → Ed25519 trust → WebRTC always-on

Pair once over BLE, exchange Ed25519 keypairs, agree on a room id. From then on the watch and Companion meet at `hub.spawndev.com`, mutually verify with signed challenges, and talk over a WebRTC data channel regardless of which network either side is on. Bluetooth becomes the recovery / re-pair path, not the everyday transport.

Full design: [`Plans/phase7-webrtc-handoff.md`](phase7-webrtc-handoff.md).

## Test surface

`SpawnWear.Bridge.Tests` (xUnit, net10.0) holds 25 wire-format regression tests:

- Channel decoders: Battery (full + low + too-short), IMU, RTC, Button (5 mappings + too-short), WifiStatus (4 states + IP roundtrip + state-only), WifiScan (3 lines + pipe-in-name + empty), DebugLog (UTF-8 with emoji).
- BridgeClient: connection lifecycle event re-fire, SendAsync routing through transport, SendAsync without transport throws.
- `BleUuidsParityTest`: reads `SpawnWear/BleUuids.cs` (firmware) and `SpawnWear.Bridge/BleUuids.cs` (bridge) at test time, regex-extracts every named GUID + every byte constant, asserts byte-for-byte parity. Drift in either file fails the test.

Run with `dotnet test SpawnWear.Bridge.Tests/SpawnWear.Bridge.Tests.csproj`.

## What the PWA is NOT

- **Not a substitute for the watch UI.** The on-device launcher and apps are the canonical UI. The PWA is a remote.
- **Not required for any core feature.** If the PWA never loads, every watch app still works.
- **Not a single shared UI codebase.** The watch and PWA share the C# language and some service-data contracts, but their renderers are different (CO5300 framebuffer vs HTML / canvas). Don't try to abstract one renderer that targets both.

## Why Bridge is its own project (vs referencing the firmware)

A third-party Blazor consumer can't reference the firmware project directly because nanoFramework's `mscorlib` is a different binary (different surface, different runtime semantics) than .NET 10's. Anything we want both sides to share has to live somewhere they can both compile against.

**Today's pragmatic answer**: don't share - duplicate. The shared surface is tiny (BLE UUIDs in `SpawnWear/BleUuids.cs` + `SpawnWear.Bridge/BleUuids.cs`, channel-id strings in `SpawnWear.Bridge/BridgeClient.cs::ChannelIds`, a handful of packed payload structs). Mirroring two ~30-line files is cheaper than the multi-targeting friction.

**When duplication starts to hurt** (more shared types, schema drift, etc.) we graduate the shared surface to a fourth project: **`SpawnWear.Protocol`**, multi-targeting `netnano1.0;net10.0`, holding only the type definitions both sides need. Both `SpawnWear` (firmware) and `SpawnWear.Bridge` reference it; nobody copies anymore.

Until that pain shows up, the duplication note at the top of `BleUuids.cs` ("mirrors the firmware's `SpawnWear/BleUuids.cs` - keep these two files in sync") is the canonical reminder.

## Cross-references

- **SpawnDev.BlazorJS** - `D:/users/tj/Projects/SpawnDev.BlazorJS/`. Use typed interop, never raw `IJSRuntime`. See its CLAUDE.md.
- **SpawnDev.RTC** - WebRTC peer-to-peer for both Blazor browser AND .NET desktop. Phase 7 wires this into `WebRtcTransport`.
- **NanoFrameTest1** - `D:/users/tj/Projects/NanoFrameTest1/`. The reference architecture for "nanoFramework GATT + Blazor PWA + Playwright tests". Mirror its shape when in doubt.
- **BLE GATT layout** - SpawnWear's UUID namespace base is `a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb` (note `c1`, not `c0` which NanoFrameTest1 uses, so a phone with both PWAs installed doesn't get device contracts confused). Duplicated in firmware `SpawnWear/BleUuids.cs` and Bridge `SpawnWear.Bridge/BleUuids.cs` - keep in sync.
