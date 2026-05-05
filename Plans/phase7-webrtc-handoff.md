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
- `SpawnDev.RTC.Server` (STUN/TURN/tracker bundled as `IHostedService`; runs at `wss://hub.spawndev.com:44365/announce` per TJ — port 44365, not the default 443)
- Tracker-gated ephemeral TURN creds (only peers currently announced to the signaling tracker can allocate relay sockets — RFC 8489 §9.2 + the tracker-gating layer)
- `EphemeralTurnCredentials.Generate(sharedSecret, userId, lifetime)` — credential minter. The Companion mints with `userId = peerIdHex` so the gating server knows the cred is bound to an active tracker session under the matching peer id.
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

## Crypto stratification

Phase 7 actually needs TWO crypto stacks running side by side, each with different requirements per device:

**Stack A — Phase 7 pairing-trust layer (our addition).** Used during BLE pairing and again on every WebRTC reconnect to verify peer identity. Small surface:

| Primitive | Purpose |
|---|---|
| Ed25519 sign / verify | Pairing handshake + reconnect challenge. RFC 8032. |
| Cryptographic random | Generate keypairs, generate the room key, generate the per-reconnect nonce. |
| SHA-256 (optional) | If we go with deterministic room-key derivation `RoomKey.FromString(SHA256(companionPub || watchPub))`. |

**Stack B — WebRTC wire encryption (delivered by the underlying WebRTC stack, NOT something we implement).** This is the standard WebRTC TLS/DTLS-SRTP suite that browsers, Sip­Sorcery, libdatachannel, etc. implement. We don't build it; we pick a stack that does. Surface:

| Primitive | Purpose |
|---|---|
| DTLS 1.2 (1.3 ideal) | Handshake before any data flows. Browsers require ECDSA-P256 certs by default; some accept RSA. |
| ECDSA P-256 sign / verify | Peer certificate generation + DTLS handshake auth. |
| AES-GCM-128 / AES-GCM-256 | SRTP profile — payload encryption + integrity. WebRTC standard. |
| AEAD-AES-128-GCM (alternative) | Equivalent SRTP profile. |
| SHA-256 / SHA-384 / SHA-512 | DTLS handshake hashing + HKDF-Expand. |
| HMAC-SHA1 (legacy) | Older SRTP profile (`AES_CM_HMAC_SHA1_80`). Browsers still negotiate it; mandated for SipSorcery's fork. |
| HKDF | Deriving SRTP session keys from the DTLS master secret. |

### Per-device matrix

| Device | Pairing-trust (Stack A) | WebRTC wire (Stack B) |
|---|---|---|
| **Companion (Blazor WASM PWA)** | `SpawnDev.BlazorJS.Cryptography` (already shipped). Browser WebCrypto for Ed25519 with `Ed25519Managed.cs` fallback. | Browser native WebRTC. Zero work — the browser does it all. |
| **Future SpawnWear.Bridge.Desktop (.NET)** | `SpawnDev.BlazorJS.Cryptography` (`DotNetCrypto`, same library, runs on desktop). | `SpawnDev.RTC` (already shipped). Bundles the SipSorcery fork with its proven BouncyCastle DTLS stack + ECDSA-P256 certs + AES-GCM SRTP profiles. |
| **Watch (firmware, nanoFramework on ESP32-S3)** | OPEN — see open question #3. | OPEN — see open question #7. |

The watch is the genuinely hard problem on both axes. ESP-IDF (which the nf-interpreter native build sits on top of) ships **mbedtls** with full DTLS, AES-GCM, ECDSA P-256, and SHA-2/3 support. So the C primitives are present; the work is exposing them through nanoCLR as managed primitives + writing or porting a WebRTC client that uses them.

### Watch-side options for Stack B (WebRTC wire)

**CHOSEN (2026-05-05, TJ): Option 1 — libdatachannel + mbedtls.** Roll-our-own (option 2) is more fun but blocks Phase 7 indefinitely on cross-implementation interop testing; hub-mediated relay (option 3) loses the "hub never sees data" property that justifies the trust model. Documented for posterity:

1. **libdatachannel + mbedtls** ← chosen. [libdatachannel](https://github.com/paullouisageneau/libdatachannel) is a well-maintained C++ WebRTC stack used in IoT contexts. Compile into the LostBeard nf-interpreter fork as a native module; expose data-channel primitives through a managed wrapper. Largest dependency to import but most complete + actively maintained + already validated against Chrome/Firefox/SipSorcery (the cross-implementation interop test surface we'd otherwise have to build ourselves).
2. **Custom thin WebRTC.** Write only the data-channel slice we need on top of mbedtls + libsrtp directly. Smaller code but reinventing the WebRTC client; defer indefinitely.
3. **Watch is NOT a real WebRTC peer.** Hub-mediated relay. Trades elegance for simplicity; loses the "hub never sees data" property; defer indefinitely.

See [`Plans/phase7-firmware-stub.md`](phase7-firmware-stub.md) §"WebRTC peer integration" for the concrete libdatachannel + nf-interpreter integration plan.

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

**Status update 2026-05-04 (Riker test coverage day):**

- Companion-side Phase 7a is functionally complete — `PairingFlow` shipped, `LocalStoragePairingStore` shipped, BLE pairing handshake wire format finalized, watch-side `PairingService.cs` deployed with `I:\spawnwear-pair.bin` keypair persistence (verified across reboot + redeploy).
- **Phase 7a / 7b interlock test-locked.** New `PairingWebRtcIntegrationTests.cs` (10 tests) exercises the complete Phase 7a → 7b production path: real `PairingFlow.PairAsync` produces a real `PairingRecord`, then real `WebRtcChallenge` primitives use that record's stored keys for mutual auth. Catches: imposter watches, tampered nonces / signatures, multi-watch cross-talk, re-pair revocation. Mutation-tested with 2 production-code breaks → predicted tests fail. This means whichever Stack B path wins (libdatachannel or libpeer), the byte layouts AND key-storage contract are pre-locked — the watch firmware just has to honor the same `WebRtcChallenge.SignedDomain(nonce)` bytes the Companion produces.
- Bridge.Tests now 113/113 passing (31 added 2026-05-04: log-buffer + send-path + integration). See `SpawnWear.Bridge/CHANGELOG.md` [Unreleased] section.
- Open question 7 (watch-side WebRTC stack) flipped back to "REVISIT" pending TJ review of the libpeer research (memory `project_phase7b_libdatachannel_research_finding_2026_05_05.md`).

## Open questions (for design review before any code lands)

1. **MTU bump strategy.** Default ATT MTU is 23 bytes; we need ~116 for the pairing handshake. Web Bluetooth doesn't expose MTU control. Either (a) chunk the handshake into multiple writes on a single characteristic, or (b) split the handshake across two sequential writes.
2. **Room key derivation.** `RoomKey.Random()` (simple) vs `RoomKey.FromString(SHA256(companionPub || watchPub))` (deterministic; lets a re-pairing client recover the room key from just the pubkeys; lower coordination risk).
3. **Watch-side Ed25519 implementation.** Confirmed `SpawnDev.BlazorJS.Cryptography` ships Ed25519 sign/verify on Browser (Web Crypto), BrowserWASM (Web Crypto), AND DotNet (`Ed25519Managed.cs`, pure managed C#). That covers the Companion side both as a Blazor PWA and as a future SpawnWear.Bridge.Desktop crate. The watch is harder: `Ed25519Managed.cs` is built on `System.Numerics.BigInteger` + `System.Security.Cryptography.SHA512`, and nanoFramework's mscorlib doesn't expose either. Real options: (a) port `Ed25519Managed.cs` to nanoFramework, which means first porting/finding a BigInteger replacement and a SHA-512 implementation that compile against nanoFramework's mscorlib (non-trivial; both are bigint-heavy bit-bang code), (b) use a nanoFramework-targeting BouncyCastle if one exists / fits the deploy ceiling, (c) bake an Ed25519 intrinsic into the LostBeard nf-interpreter fork using a small C library (mbedtls / libsodium) compiled into the nanoCLR native build. Option (c) keeps managed code thin and the per-signature CPU cost reasonable for the ESP32-S3 watch; option (a) is more "everything in C#" but we'd be building primitives. Resolve before any pairing-on-watch code lands.

   Wire-format note: BlazorJS.Cryptography's Ed25519 import/export uses SPKI / PKCS8 envelopes (44-byte pubkey, 48-byte privkey including a 12-byte ASN.1 header). The BLE pairing handshake carries the RAW 32-byte key bytes for compactness; Bridge code wraps/unwraps the SPKI prefix at call sites where it interfaces with `IPortableCrypto`.
4. **Hub URL discovery.** Hard-code `wss://hub.spawndev.com`, or let the Companion configure it (multi-hub failover for redundancy)? Watch-side: same question - configurable via BLE before pairing, or hard-coded?
5. **WebRTC video track for screen mirror.** Replace today's HTTP-pulled `/screenshot.bin` once WebRTC is up, or keep both for fallback when WebRTC is unreachable?
6. **TurnServer credentials.** The `SpawnDev.RTC.Server` model is "ephemeral creds gated by who's currently announced on the tracker". The Companion (and the watch, when it can speak WebRTC) mints credentials via `EphemeralTurnCredentials.Generate(sharedSecret, userId=peerIdHex, lifetime)` immediately before constructing the peer connection. The TURN server validates the credential AND checks that `userId` matches an actively-announced tracker peer. **Open**: how does each side get the `sharedSecret`? Three patterns: (a) hardcoded in the Companion + watch firmware (simple, but a leaked firmware leaks the secret), (b) per-pair derived secret from `roomKey` (hub never sees it; works for self-hosted hubs that own per-room state), (c) hub mints + delivers a per-session TURN credential in its announce response (cleanest for clients but requires extending the tracker wire protocol). TJ's call before any cred-minting code lands.
7. **Watch-side WebRTC stack (Stack B).** ⚠️ **REVISIT — 2026-05-04 research found libdatachannel has zero ESP-IDF integrations in the wild and the upstream author redirects embedded askers to libpeer.** Earlier resolution (2026-05-05: libdatachannel + mbedtls) was made before this research. Every commercial / open ESP32 WebRTC SDK (LiveKit, GetStream, Espressif's own `esp-webrtc-solution`) ships **`sepfy/libpeer`** or its closed-source Espressif fork `esp_peer`, not libdatachannel. libpeer is MIT, sub-100 KB RAM, on the ESP Component Registry, validated against Chrome / Firefox / SipSorcery via LiveKit's production preview. Companion-side `WebRtcChallenge` + `WebRtcDataFraming` byte layouts are stack-agnostic — same wire works either way. The libdatachannel path requires vendoring usrsctp + libjuice + plog + nlohmann/json into nf-interpreter and patching DTLS-SRTP + `NO_MEDIA + mbedtls` integration that's been broken in libdatachannel as recently as 2024 (issue #1283). Decision pending TJ review before any Phase 7b implementation work begins. See [`Plans/phase7-firmware-stub.md`](phase7-firmware-stub.md) §"WebRTC peer integration" for the original libdatachannel plan + memory `project_phase7b_libdatachannel_research_finding_2026_05_05.md` for the libpeer alternative writeup.
8. **Managed-side wrapping for watch crypto primitives.** Whichever Stack B path we take, the watch needs to expose enough crypto through nanoCLR for Stack A (Ed25519). Three sub-questions: (a) do we add Ed25519 as a separate intrinsic alongside whatever Stack B brings in, (b) does Stack B's underlying library (mbedtls) already implement Ed25519 we can re-expose, (c) what's the managed surface — extend `IPortableCrypto` to nanoFramework, or roll a SpawnWear-specific minimal interface?

These get answered in the next dedicated Phase 7 session, not in autonomous Editor's-choice work.
