# SpawnWear transport - WebRTC link + multiplexed channel bus

The watch talks to the Blazor Companion PWA (and, ultimately, the AI Assistant backend) over **one
authenticated WebRTC data channel**, shared by the OS and all loadable apps through a **multiplexed
channel bus**. This is the foundation the flagship AI Assistant is built on. Status: live and proven
end to end as of 2026-06-23 (Phase 7).

## Layers

```
  apps  ──IAppChannel──┐                      ┌── app.<id>.* surface (Companion / PWA)
  OS    ──Bus.Send─────┤   TransportBus       │
                       │  (named channels,    │
                       │   namespaced,        │
                       │   isolated)          │
                       └──────────┬───────────┘
                                  │  WebRtcDataFraming  [cidLen][cid][plen LE][payload]
                          one WebRTC data channel  (DTLS-SRTP encrypted)
                                  │
                       authenticated at open (mutual Ed25519, BLE-paired identities)
```

- **Watch side:** `WebRtcTransportService` (autonomous: connect → stay → reconnect, gated paired+WiFi)
  owns the connection on its own thread and pumps `TransportBus`. The native WebRTC is libpeer
  (`SpawnDev.WebRTC` interop, lock-free TX/RX rings so the CLR never stalls).
- **Companion side:** `SpawnWear.Bridge` (`WebRtcTransport` over SpawnDev.RTC) + `BridgeClient`.

## The channel bus (`Services/TransportBus.cs`)

One link carries many logical channels, keyed by the channel id in each frame. Both the OS and apps
send/receive through the bus; they cannot collide, spoof, or crash each other:

- **Namespacing.** The OS owns reserved names (`battery`, `imu`, `rtc`, `sys.*`, ...). Apps get a
  **scoped `IAppChannel`** (`Bus.OpenAppChannel(appId)`) that forces every send/subscribe into
  `app.<appId>.*` - an app physically cannot touch a system channel or another app's channels.
- **Isolation.** Inbound frames route to the registered handler with per-handler exception catching;
  a throwing app handler can't take down the pump or the others. App channels auto-unsubscribe on
  `Close()` (call it when the app unloads).
- **Send queue.** `Bus.Send` enqueues (thread-safe, bounded drop-oldest); the pump loop drains to the
  wire. Callers never touch the radio. The actual native `Send` is lock-free (TX ring).

## Encoding is per-channel (not transport-wide)

The bus carries **opaque bytes** per channel, so each lane picks its own encoding:

- **System lanes:** compact fixed binary schemas (the same ones `WatchProfileService` notifies over
  BLE - e.g. `battery` = `[percent:u8][flags:u8][mV:u16-LE][mA:i16-LE]`). The Companion's
  `BridgeClient` already decodes these.
- **App lanes:** **MessagePack** - typed, self-describing, evolvable. The watch encodes with
  `Services/MsgPackWriter.cs` (a minimal hand-rolled nanoFramework encoder: maps/arrays/strings/ints/
  float32/bool/nil, big-endian; the full MessagePack-CSharp can't run on nanoFramework). The Companion
  (.NET) and PWA (JS wrapper) decode with the full MessagePack libraries.

## Disconnect detection

- **Graceful:** the Companion sends `sys.disconnect` right before teardown → the watch flips its
  link state immediately.
- **Ungraceful** (crash, dead WiFi, browser killed): libpeer's ICE keepalive timeout
  (`CONFIG_KEEPALIVE_TIMEOUT`, 10s) - no peer STUN binding-requests for that long → link closed.

## Building an app on the bus (the template)

```csharp
var ch = transportBus.OpenAppChannel("myapp");      // confined to app.myapp.*
ch.OnMessage("cmd", (cid, payload) => { /* decode MessagePack, act */ });
var w = new MsgPackWriter(); w.WriteMapHeader(2);
w.WriteString("type"); w.WriteString("hello");
w.WriteString("v");    w.WriteInt(1);
ch.Send("event", w.ToArray());                       // -> app.myapp.event
// on unload:
ch.Close();
```

The Companion-side surface subscribes to `app.myapp.*` (via `Bridge.GetUnderlyingTransport()
.MessageReceived`, filtered by channel id) and decodes the MessagePack. The **AI Assistant** follows
exactly this: an `app.assistant.*` lane carrying voice/text/command messages, multiplexed with the OS
telemetry over the same authenticated link.

## Source map

| Piece | File |
|-------|------|
| Autonomous connection service | `SpawnWear/Services/WebRtcTransportService.cs` |
| Channel bus + IAppChannel | `SpawnWear/Services/TransportBus.cs` |
| MessagePack encoder (watch) | `SpawnWear/Services/MsgPackWriter.cs` |
| Connect/challenge/pump loop | `SpawnWear/Program.cs` (`WebRtcConnectRun`) |
| Native WebRTC interop (lock-free) | nf-interpreter `InteropAssemblies/SpawnDev.WebRTC/...PeerConnection.cpp` |
| libpeer (forked, open) | [`LostBeard/libpeer`](https://github.com/LostBeard/libpeer) branch `spawnwear` |
| Companion transport / decode | `SpawnWear.Bridge/WebRtc/WebRtcTransport.cs`, `BridgeClient.cs`; `SpawnWear.Companion/Pages/Home.razor` |
