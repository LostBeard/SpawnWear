# Phase 7 — BLE pairing → Ed25519 trust → WebRTC always-on

The watch and a Companion (Blazor WASM PWA OR a future SpawnWear.Bridge.Desktop consumer) pair **once** over BLE. After that, they reach each other through `hub.spawndev.com` over WebRTC for as long as both have internet — same network, different network, doesn't matter. Bluetooth is only needed for the initial trust bootstrap.

This document captures the design. Nothing in here is implemented yet beyond the BLE pairing primitives that already shipped on 2026-05-05.

## Mental model

```
   ┌─────────────────────────┐                ┌─────────────────────────┐
   │  Companion (browser)    │  pair once     │  Watch                  │
   │  Ed25519 keypair (LS)   │ ◄────BLE────►  │  Ed25519 keypair (NV)   │
   │  knows watch.pubkey     │                │  knows companion.pubkey │
   │  knows shared room id   │                │  knows shared room id   │
   └────────────┬────────────┘                └────────────┬────────────┘
                │                                          │
                │            from now on:                  │
                │                                          │
                ▼                                          ▼
       ┌─────────────────────────────────────────────────────────┐
       │           hub.spawndev.com  (WebRTC signaling)          │
       │  - mesh / SFU rooms                                     │
       │  - relays SDP offer/answer + ICE candidates             │
       │  - does NOT see app payload (E2E auth via Ed25519)      │
       └─────────────────────────────────────────────────────────┘
                │                                          │
                └─── peer-to-peer WebRTC data + media ─────┘
                     (LAN-direct when possible, TURN otherwise)
```

The hub never sees data-channel content — it's just a meeting point for SDP exchange. Both peers verify each other with Ed25519-signed challenges before unmuting the data channel for real traffic. A compromised hub can drop traffic but can't impersonate either side or read what flows.

## Why this shape

- **Bluetooth is the trust anchor.** Web Bluetooth requires the user to physically pick a device from a picker. A successful BLE pair → key exchange = "this watch belongs to this user's browser." That's exactly the proof Ed25519 can preserve forever, off-LAN.
- **WebRTC is the delivery mechanism.** Once trust is established, the watch shouldn't have to be on the same WiFi as the Companion. WebRTC's NAT traversal + TURN fallback cover every network topology. STUN works for ~80% of LAN→LAN, TURN catches the rest.
- **The hub is dumb on purpose.** It only knows "two peers want to find each other in room X." The actual message is opaque. Lose the hub for an hour, peers drop offline. Win the hub keys, you still can't read traffic. This matches the same trust model TJ already prefers (see `user_statistics_based_trust.md` — minimize the parties who need to be honest).
- **Bluetooth survives as the recovery channel.** If a watch's flash gets wiped or the browser's localStorage is cleared, the user can re-pair over BLE in 10 seconds. No "lost device" support flow needed.

## Pairing flow (over BLE, today + tomorrow)

Today's BLE service has 9 characteristics. Phase 7 adds 2 more under the same primary service:

- `PairingPubKeyUuid` — read+notify. Watch's Ed25519 public key (32 bytes) + a 16-byte device-id derived from the chip MAC. Notified once on first read.
- `PairingHandshakeUuid` — write+notify. The pairing handshake. Companion writes its public key + a proposed room id; watch responds with a signed acknowledgement.

The wire format for `PairingHandshake` is purposely small to fit in a single ATT MTU (default 23 bytes is too tight; bump to 244 via MTU exchange):

```
Companion → Watch (write):
  [companion.pubkey:32][proposed_room_key:20][signature_of(prev 52 bytes):64]
  = 116 bytes
  signature is over the previous 52 bytes, signed with companion.privkey
  proposed_room_key matches SpawnDev.RTC's RoomKey (20 bytes, WebTorrent
  info_hash compatible).

Watch → Companion (notify in response):
  [watch.signature_of(companion.pubkey || room_key || watch.pubkey):64]
  = 64 bytes

Both sides verify the other's signature. If valid, both persist:
  - the OTHER's Ed25519 public key
  - the agreed room key (20-byte RoomKey)
  - the date paired (for forensics if a key is later compromised)
```

A user can re-pair to revoke an old key — the watch overwrites the stored companion pubkey with the new one. The old companion's signed messages stop verifying.

## WebRTC connection flow (Phase 7 implementation)

SpawnDev.RTC already ships every primitive we need:
- `ISignalingClient` / `TrackerSignalingClient` (WebTorrent-tracker-compatible signaling)
- `RoomKey` (20-byte room identifier, info_hash-shaped)
- `SpawnDev.RTC.Server` (STUN/TURN/tracker bundled as `IHostedService`; runs at `wss://hub.spawndev.com` per TJ)
- Tracker-gated ephemeral TURN creds (only currently-announced peers in a room get TURN allocations)
- `RtcPeerConnectionRoomHandler` (peer-connection lifecycle wired to the signaling layer)
- `IRTCPeerConnection` / `IRTCDataChannel` cross-platform abstraction

```
1. Both peers construct an ISignalingClient bound to wss://hub.spawndev.com.
   They Subscribe(ourRoomKey, handler) and AnnounceAsync(ourRoomKey, ...).
   Hub-side, the SpawnDev.RTC.Server tracker matches them up.

2. RtcPeerConnectionRoomHandler runs the SDP offer/answer + ICE dance.
   STUN works for ~80% of LAN→LAN; TurnServer supplies relay candidates
   when needed, gated by ephemeral creds the tracker hands out only to
   announced peers in our room.

3. When the WebRTC data channel opens, BOTH peers immediately send a
   challenge:
       [random_nonce:32]
   The other peer signs it with its Ed25519 privkey and returns:
       [random_nonce:32][signature:64]
   Each side verifies with the OTHER's stored pubkey via
   SpawnDev.BlazorJS.Cryptography (same crypto API works on browser
   AND desktop). If verification fails on either side, both peers tear
   the connection down before any app payload flows.

4. After mutual verification, the data channel transports the same
   TransportMessage stream the BLE transport carries today. Channel-id
   plus payload bytes — same channel ids, same wire formats, no app-layer
   changes on either side. WebRtcTransport just becomes another
   ITransport implementation.

5. Optional WebRTC media tracks for audio (microphone capture for AI
   Assistant flagship app) and video (screen mirror replacing the
   HTTP-pulled /screenshot.bin path entirely).
```

The Ed25519 verification is layered ON TOP of WebRTC's DTLS-SRTP. DTLS
handles wire encryption; Ed25519 handles peer identity. A compromised
hub can drop / delay traffic but can never impersonate either side or
read what flows.

## Bridge surface (today vs tomorrow)

Today's `BridgeClient` only knows BLE. Phase 7 keeps the same public surface but adds the pairing + WebRTC promotion underneath:

```csharp
// Same as today
client.BatteryChanged   += ...
client.WifiStatusChanged += ...
await client.SetWifiAsync("MySSID", "password");

// New in Phase 7
await client.PairAsync();                            // BLE handshake; one-time per device
await client.PromoteToWebRtcAsync();                 // moves the active transport from BLE to WebRTC
client.IsRemote                                       // true once WebRTC is live and BLE is dropped

// Once paired, no BLE needed:
var newClient = new BridgeClient(/* loads stored pubkey + room id */);
await newClient.ReconnectAsync();                    // hub.spawndev.com → WebRTC → ready
```

Reconnect (step 5 of Phase 7's `ReconnectAsync`) is the everyday path — no Bluetooth permission prompt, no being-on-the-same-WiFi requirement. The PWA pulls the cached pairing material from localStorage; the watch pulls its from flash.

## Storage

- **Companion (browser)**: `localStorage["spawnwear.pair.<watchId>"]` = JSON `{ ourPub, ourPriv, theirPub, roomId, pairedAt }`. One entry per paired watch. If the user clears site data, all pairings are gone — re-pair via BLE.
- **Watch (firmware)**: a small TLV blob in non-volatile storage (literalfs or a dedicated NVS namespace). Holds `{ ourPub, ourPriv, theirPub, roomId, pairedAt }`. Survives reboot + firmware update.

The watch's privkey is generated once on first boot and never leaves. Companion's privkey is generated per browser-context and never leaves the WebCrypto API surface.

## Why hub.spawndev.com (not a public STUN/TURN)

- TJ owns it. Single party in the trust path beyond the user.
- Custom protocol on top: room-claim signed by Ed25519 means the hub is gated on cryptography, not on shared secrets / OAuth.
- Same hub is reusable for every SpawnWear watch the user owns and every other SpawnWear-shaped device that needs a peer relay (future glasses, future headphones, etc.).
- A second / third hub for redundancy is trivial: peers can try `hub.spawndev.com` first, fall back to `hub-eu.spawndev.com` etc. Same Ed25519 verification works regardless.

## What this is NOT

- **Not a chat / social system.** Rooms are 1-to-1 between a paired watch and a paired Companion. No "discover other users" surface.
- **Not E2EE messaging crypto.** Ed25519 signatures + the WebRTC data channel's DTLS layer carry the security. No double-ratchet, no Diffie-Hellman session keys above DTLS — DTLS is enough for the threat model.
- **Not a hard hub dependency.** If the hub is unreachable, peers fall back to BLE if they're nearby. Worst case: the user has to be in Bluetooth range to interact with the watch.

## Scope check

Phase 7 is a multi-week build, not a single-session push. Today's 2026-05-05 ship gives Phase 7 a clean foundation: every BLE channel decoded + 36 tests + WatchHttp on top + WebRtcTransport stub + SpawnDev.RTC referenced. The first concrete Phase 7 commit will be the firmware-side `PairingService` + matching Bridge characteristic handlers.

## Open questions (for design review before any code lands)

1. **MTU bump strategy.** Default ATT MTU is 23 bytes; we need ~116 for the pairing handshake. Web Bluetooth doesn't expose MTU control. Either (a) chunk the handshake into multiple writes on a single characteristic, or (b) split the handshake across two sequential writes.
2. **Room key derivation.** `RoomKey.Random()` (simple) vs `RoomKey.FromString(SHA256(companionPub || watchPub))` (deterministic; lets a re-pairing client recover the room key from just the pubkeys; lower coordination risk).
3. **Watch-side Ed25519 implementation.** `SpawnDev.BlazorJS.Cryptography` is the cross-platform answer on .NET/Blazor — does it run on nanoFramework? If not, options: (a) port a managed Ed25519 to nanoFramework, (b) use BouncyCastle if it'll fit on the watch's deploy ceiling, (c) bake an Ed25519 native intrinsic into the LostBeard nf-interpreter fork.
4. **Hub URL discovery.** Hard-code `wss://hub.spawndev.com`, or let the Companion configure it (multi-hub failover for redundancy)? Watch-side: same question - configurable via BLE before pairing, or hard-coded?
5. **WebRTC video track for screen mirror.** Replace today's HTTP-pulled `/screenshot.bin` once WebRTC is up, or keep both for fallback when WebRTC is unreachable?
6. **TurnServer credentials.** The `SpawnDev.RTC.Server` model is "ephemeral creds gated by who's announced in the room." That means the hub's tracker needs to know our pubkey BEFORE we announce, so it can verify our room-claim signature. Bootstrap order matters: pubkey-registration ↔ first-announce.

These get answered in the next dedicated Phase 7 session, not in autonomous Editor's-choice work.
