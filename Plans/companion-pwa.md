# Companion Blazor WASM PWA

A complementary web app that mirrors the watch over BLE + WiFi. **Optional** - the watch is fully functional without it. The PWA is a remote, not a tether.

Phase 4 starts the PWA in parallel with the watch's Settings app, since both surfaces need the same configuration data and writing them in lockstep prevents drift.

## Why have one at all

Three concrete reasons, in priority order:

1. **First-time WiFi setup before the on-device keyboard is comfortable.** Typing a 12-character WPA2 password on a 2.06" panel is painful. The PWA's job is to take that pain off the watch.
2. **Live debugging from a laptop.** Pixel-perfect remote view of the watch's screen + console log streaming over BLE is the difference between "swap COM cables and re-flash" and "open a browser tab."
3. **Showing off.** SpawnDev.BlazorJS exists to prove Blazor WASM PWAs can be first-class apps. The companion proves SpawnWear's surface is rich enough to drive from a browser.

## Architecture

Same C# language on both sides. The watch firmware exposes its system services over two transports:

- **BLE GATT** - always available, low bandwidth (a few KB/s sustained), works without router or WiFi config
- **WiFi HTTP** - faster, supports binary screenshot streaming, requires an SSID + password to be configured already

The PWA picks whichever is reachable. Both transports terminate at the same in-firmware service host, so the companion's view is consistent regardless of which path it took.

```
┌──────────────────────────────────────────────────────────────────┐
│  Companion PWA (Blazor WASM, runs in browser)                    │
│  - SpawnDev.BlazorJS for typed JS interop                        │
│  - Web Bluetooth (Chrome / Edge) for BLE GATT                    │
│  - fetch() / WebSocket for HTTP                                  │
│  - Mirrors every Settings page + every built-in app              │
└──────────────────────┬───────────────────────────────────────────┘
                       │
        ┌──────────────┴──────────────┐
        ▼                             ▼
   BLE GATT                      HTTP / WebSocket
   (always-on)                   (when WiFi up)
        │                             │
        ▼                             ▼
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

- [ ] PWA scaffolded with SpawnDev.BlazorJS, registered as a PWA (manifest + service worker)
- [ ] Web Bluetooth pairing flow against `WatchProfileService` (custom UUID base `a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb`)
- [ ] WifiConfigService write characteristic - PWA sends SSID + password, watch stores via `Wireless80211Configuration.SaveConfiguration` and reconnects
- [ ] Live battery + RTC + IP readout via WatchProfileService

### Phase 4b - Debug console mirror

- [ ] DebugConsoleService BLE notify subscriber on the PWA - shows the watch's `Debug.WriteLine` output in a terminal-style page in real time
- [ ] PWA can send commands back to the watch (e.g. "force sleep", "wake", "redraw screen") via a write characteristic

### Phase 4c - Live screen mirror over WiFi

- [ ] PWA queries `http://<watch-ip>:8080/screenshot.bin`, decodes the RGB565 BE payload, renders to a canvas
- [ ] Refresh button + auto-refresh toggle (1 fps when WiFi is good)
- [ ] Touch coordinates from the PWA → POST to a `/touch` endpoint → injected into the watch's event loop (this turns the PWA into a fully-remote launcher)

### Phase 5 onwards - Per-app mirrors

Each built-in app gets a corresponding PWA page that drives its UI remotely. Settings and Clock are easiest (mostly read-write of state); Voice Recorder and AI Assistant need bidirectional audio over WebRTC.

## What the PWA is NOT

- **Not a substitute for the watch UI.** The on-device launcher and apps are the canonical UI. The PWA is a remote.
- **Not required for any core feature.** If the PWA never loads, every watch app still works.
- **Not a single shared UI codebase.** The watch and PWA share the C# language and some service-data contracts, but their renderers are different (CO5300 framebuffer vs HTML / canvas). Don't try to abstract one renderer that targets both.

## Cross-references

- **SpawnDev.BlazorJS** - `D:/users/tj/Projects/SpawnDev.BlazorJS/`. Use typed interop, never raw `IJSRuntime`. See its CLAUDE.md.
- **NanoFrameTest1** - `D:/users/tj/Projects/NanoFrameTest1/`. The reference architecture for "nanoFramework GATT + Blazor PWA + Playwright tests". Mirror its shape when in doubt.
- **BLE GATT layout** - SpawnWear's UUID namespace base is `a0e4f2c1-SSSS-CCCC-8000-00805f9b34fb` (note `c1`, not `c0` which NanoFrameTest1 uses, so a phone with both PWAs installed doesn't get device contracts confused).
