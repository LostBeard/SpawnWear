# Phase 7 — Watch firmware pairing stub

The Companion side of Phase 7 is functionally complete (see [`Plans/phase7-webrtc-handoff.md`](phase7-webrtc-handoff.md) for the full design). To make the "Establish trust (Phase 7)" button on the Companion's Home page actually succeed, the watch firmware needs to ship two new BLE characteristics + a small in-firmware PairingService. This doc sketches what that looks like in terms of nanoFramework code so the next firmware session has a clear starting point.

This is the **pairing-only** subset. Watch-side WebRTC peer is a separate, bigger problem ([`Plans/phase7-webrtc-handoff.md`](phase7-webrtc-handoff.md) §"Watch-side options for Stack B").

## What the firmware already has

- BLE GATT service at `BleUuids.WifiServiceUuid` advertised on boot.
- 9 existing characteristics (battery / IMU / RTC / button / wifi-status / wifi-scan / wifi-creds / wifi-cmd / debug-log / debug-cmd) attached to that single primary service.
- Two reserved-but-unused UUIDs already in `SpawnWear/BleUuids.cs`:
  - `PairingPubKeyUuid`     = `a0e4f2c1-0001-00a0-...`
  - `PairingHandshakeUuid`  = `a0e4f2c1-0001-00a1-...`
- The 12 `WifiCmdConnect` / `ButtonBoot` / etc byte constants (already mirrored to Bridge's `BleUuids` via the parity test).

## What needs to be added

### 1. Watch-side Ed25519 keypair

Generated once on first boot, persisted in NVS (or a flat file in the SpawnWear app's spiffs partition), reused forever after. Pubkey is exposed via BLE; privkey never leaves the chip.

**Open question** (from the parent design doc): nanoFramework's mscorlib doesn't ship `BigInteger` or `SHA512`, both of which `Ed25519Managed.cs` depends on. Three resolutions, in order of likely effort:

a. **Native intrinsic via nf-interpreter fork.** Bake mbedtls's Ed25519 into the LostBeard fork as a managed primitive (32-byte keypair generation + sign + verify). Ed25519 implementation lives in C; the managed surface is ~3 P/Invoke-style methods. Mbedtls is already in ESP-IDF; the work is the nanoCLR managed-stub plumbing.
b. **Nuget-shipped pure-managed BigInteger + SHA-512 implementations**, then port `Ed25519Managed.cs` on top. Larger heap + flash footprint; per-signature CPU cost may hurt watch responsiveness.
c. **Hub-mediated trust** — skip Ed25519 on the watch entirely; have the hub run a "watch-by-pubkey" identity service that the Companion talks to instead. Loses the "hub never sees data" property; not a path we want to take.

Option (a) is the right end-state. Until it lands, the watch can't actually verify a Companion's signed write — but it can already EXPOSE its pubkey + persist what the Companion sends, which gets the bring-up + UI flow tested without crypto on the path.

### 2. `PairingService.cs`

Sits alongside `WatchProfileService.cs` + `WifiConfigService.cs`. Initialized from `Program.cs::Main` once the GATT service provider is up.

```csharp
namespace SpawnWear.Services
{
    public class PairingService
    {
        const string KEY_PUB  = "pair.watch.pub";   // 32 raw bytes
        const string KEY_PRIV = "pair.watch.priv";  // 32 raw bytes (never leaves chip)
        const string KEY_PEER = "pair.peer.pub";    // 32 raw bytes (the Companion's pubkey)
        const string KEY_ROOM = "pair.room.key";    // 20 raw bytes

        GattLocalCharacteristic _pubKeyChar;
        GattLocalCharacteristic _handshakeChar;

        byte[] _ourPubKey;   // Loaded from NVS or generated on first boot
        byte[] _ourPrivKey;  // Loaded from NVS or generated on first boot
        byte[] _peerPubKey;  // null until paired
        byte[] _roomKey;     // null until paired

        public bool Initialize(GattLocalService service)
        {
            EnsureKeyPair();   // Generates + persists if missing

            // Pubkey: read-only, returns _ourPubKey
            var pubParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                UserDescription = "Pairing PubKey",
            };
            var pubResult = service.CreateCharacteristic(BleUuids.PairingPubKeyUuid, pubParams);
            if (pubResult.Error != BluetoothError.Success) return false;
            _pubKeyChar = pubResult.Characteristic;
            _pubKeyChar.ReadRequested += (sender, args) =>
            {
                var req = args.GetRequest();
                var w = new DataWriter();
                w.WriteBytes(_ourPubKey);
                req.RespondWithValue(w.DetachBuffer());
            };

            // Handshake: write+notify, parses + verifies + responds
            var hsParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write
                                         | GattCharacteristicProperties.Notify,
                UserDescription = "Pairing Handshake",
            };
            var hsResult = service.CreateCharacteristic(BleUuids.PairingHandshakeUuid, hsParams);
            if (hsResult.Error != BluetoothError.Success) return false;
            _handshakeChar = hsResult.Characteristic;
            _handshakeChar.WriteRequested += OnHandshakeWrite;
            return true;
        }

        void OnHandshakeWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var req = args.GetRequest();
            var bytes = ReadAllBytes(req.Value);   // expect 116
            if (bytes.Length != 116) { req.RespondWithProtocolError(0x0D); return; }

            // Parse the 116-byte ParseCompanionWrite layout.
            var companionPub = SubArray(bytes, 0, 32);
            var roomKey      = SubArray(bytes, 32, 20);
            var companionSig = SubArray(bytes, 52, 64);

            // Verify companion's signature against (companionPub || roomKey).
            var signedDomain = Concat(companionPub, roomKey);
            if (!Ed25519.Verify(companionPub, signedDomain, companionSig))
            {
                req.RespondWithProtocolError(0x0F);
                return;
            }

            // Persist the new pairing.
            _peerPubKey = companionPub;
            _roomKey    = roomKey;
            NvsSave(KEY_PEER, _peerPubKey);
            NvsSave(KEY_ROOM, _roomKey);

            req.Respond();   // ack the write

            // Build + sign + notify the response domain.
            // SignedDomainWatchToCompanion = companionPub || roomKey || ourPubKey
            var watchSignedDomain = Concat(Concat(companionPub, roomKey), _ourPubKey);
            var watchSig = Ed25519.Sign(_ourPrivKey, watchSignedDomain);
            var w = new DataWriter();
            w.WriteBytes(watchSig);
            _handshakeChar.NotifyValue(w.DetachBuffer());
        }

        void EnsureKeyPair()
        {
            _ourPubKey  = NvsLoad(KEY_PUB);
            _ourPrivKey = NvsLoad(KEY_PRIV);
            if (_ourPubKey is null || _ourPrivKey is null)
            {
                Ed25519.GenerateKeyPair(out _ourPubKey, out _ourPrivKey);
                NvsSave(KEY_PUB,  _ourPubKey);
                NvsSave(KEY_PRIV, _ourPrivKey);
            }
        }
    }
}
```

`Ed25519.Verify` / `.Sign` / `.GenerateKeyPair` is the seam that resolves to one of the three options above. Until option (a) lands, those calls return placeholder values that won't pass the Companion's verification — but the rest of the plumbing is testable.

### 3. Wire-up in `Program.cs`

```csharp
var pairing = new SpawnWear.Services.PairingService();
if (!pairing.Initialize(wifi.ServiceProvider.Service))
{
    Debug.WriteLine("[Pair] Failed to attach pairing characteristics");
}
```

Done. The Companion's Home page can now (with a Phase 7 firmware) read the watch's pubkey, send a 116-byte handshake, and receive a 64-byte response — exactly what `PairingFlow` in `SpawnWear.Bridge` already does.

## Verification plan (without watch-side Ed25519 yet)

Even without working signatures, we can test the BLE plumbing today:

1. Wire `PairingService` with stub Ed25519 that returns predictable garbage signatures.
2. Companion clicks "Establish trust" → sends 116-byte handshake → watch persists peer key + room key → notifies a 64-byte response.
3. Companion's `PairingFlow` will reject the response signature (correctly!), but the BLE round-trip works.
4. Compose a unit test on the watch side (`PairingServiceTest`?) that feeds a known 116-byte payload and asserts the persisted state matches.
5. Replace the stub Ed25519 with the real one (option a/b/c above) and the same test starts producing valid signatures the Companion accepts.

That's the order: prove the BLE wiring, then drop in real crypto. Each step independently testable, no mock test (every byte is the real wire format).

## NVS surface

nanoFramework's `nanoFramework.Hardware.Esp32.NonVolatileStorage` is the canonical key-value store for ESP32. Persists across reboots + firmware updates that don't `--erase-flash`. ~30KB free per partition by default; our four keys (32+32+32+20 = 116 bytes) fit trivially.

If NVS isn't a fit (e.g. we want to nuke the pairing on a deliberate "factory reset" while keeping WiFi creds), the SD card path from Phase 8 is also available — `IDisplayBuffer`-side persistence is already wired.

## WebRTC peer integration (Stack B)

**Chosen path (TJ 2026-05-05):** [libdatachannel](https://github.com/paullouisageneau/libdatachannel) + ESP-IDF mbedtls, integrated into the LostBeard nf-interpreter fork as a native component, exposed to managed code through nanoCLR primitives.

### Why libdatachannel

- C++ WebRTC stack with minimal external deps (only mbedtls for DTLS + a usrsctp build for SCTP), purpose-built for embedded / server / non-browser use.
- Actively maintained; cross-implementation interop with Chrome / Firefox / SipSorcery is regression-tested in the project's own CI.
- Data-channel-only mode is a first-class API surface — we don't have to wade through media-track machinery we won't use on the watch (audio/video tracks may come later via the same library).
- ESP32-S3 is a stated supported target on the project's docs (`README.md` mentions ESP-IDF compatibility).
- Permissive license (MPL-2.0 via `master`; the `embedded` submodule is BSD-style).

### Integration shape (nf-interpreter fork)

1. **Submodule under `targets/ESP32/_IDF_v5.5.4/components/libdatachannel/`.** Mirror the pattern the fork already uses for QSPI display: native C++ component compiled via the ESP-IDF CMake build, no managed code yet.
2. **Native primitives via nanoCLR.** Add a `nanoFramework.WebRTC` sub-namespace in `nanoCLR_Native_Bindings.cpp` with thin wrappers over the libdatachannel C API:
   - `RtcPeer.Create(IceServerConfig[]) -> handle`
   - `RtcPeer.SetLocalDescription(handle) -> sdp`
   - `RtcPeer.SetRemoteDescription(handle, sdp)`
   - `RtcPeer.AddIceCandidate(handle, candidate)`
   - `RtcDataChannel.Create(peer, label) -> dc_handle`
   - `RtcDataChannel.Send(dc_handle, byte[])`
   - `RtcDataChannel.OnMessage` event (managed callback signature)
   - `RtcPeer.Dispose(handle)` / `RtcDataChannel.Dispose(dc_handle)`
3. **Managed wrapper package `nanoFramework.WebRTC`.** Pure-managed surface that mirrors the `IRTCPeerConnection` / `IRTCDataChannel` shape from `SpawnDev.RTC`. Publishes to the local feed at `D:\users\SpawnDevPackages` like the other watch packages. SpawnWear firmware references it.
4. **WatchWebRtcTransport** (firmware-side, this repo). The watch-side equivalent of the Companion's `WebRtcTransport` — opens the data channel, runs the same `WebRtcChallenge` mutual verification (writes the same byte layouts the Bridge tests already lock), then routes `TransportMessage`s in / out via `WebRtcDataFraming`. Same channel-id stream the BLE transport carries.
5. **Signaling on the watch side.** libdatachannel doesn't ship its own signaling client — that's deliberately decoupled. Our firmware-side signaling client would use the same WebTorrent-tracker WebSocket protocol the Companion uses (`wss://hub.spawndev.com:44365/announce`). nanoFramework has a WebSocket client (`System.Net.WebSockets.WebSocket`) we can build on; a thin wrapper that speaks the bittorrent-tracker JSON wire format gets us there. ~200 lines of managed code.

### Build-side concerns

- **Flash budget.** libdatachannel + mbedtls + usrsctp adds estimated ~400-700 KB of code + rodata. Fits the 32 MB flash but pushes us further toward the deploy ceiling for the managed app (the documented ~290 KB budget — see `Research/nf-interpreter-deploy-ceiling.md`). The deploy ceiling is per-managed-deploy, not total firmware, so this is OK.
- **PSRAM usage.** WebRTC peer state + DTLS handshake buffers + per-channel SCTP state = ~50-100 KB per active connection. PSRAM (8 MB) handles this easily. Watch out for fragmentation if multiple peers connect simultaneously (rare but possible: laptop + phone Companions both online).
- **CPU.** DTLS handshake is the spike — 100s of milliseconds on ESP32-S3 with mbedtls's ECDSA-P256. Once the channel is open, SRTP per-packet overhead is small (AES-GCM in hardware via `mbedtls_aes_setkey_enc` + ESP32-S3's hardware AES accelerator). Audio / video tracks would change this calculus; data-channel-only is comfortable.
- **Power.** WebRTC peer connection idle is ~0 mA — DTLS keepalive is per-minute, SCTP HEARTBEAT is per-30s. Active throughput pushes the WiFi radio at full duty, similar power profile to a sustained HTTP fetch. The AXP2101 already handles WiFi-active power; no new rail issues expected.

### Phasing

1. **Phase 7a — pairing only** (this doc above). Ship the BLE handshake + Ed25519 verify with stub crypto first, then real crypto. No WebRTC yet. Companion's "Establish trust" button works end-to-end.
2. **Phase 7b — libdatachannel landing.** Submodule in nf-interpreter fork; native primitives wired to nanoCLR; minimal managed wrapper package; firmware can `new RtcPeerConnection()` without crashing.
3. **Phase 7c — WatchWebRtcTransport.** Firmware-side ITransport-equivalent that mirrors the Companion's `WebRtcTransport`. Implements the `WebRtcChallenge` mutual-verify flow (byte layouts already locked) + `WebRtcDataFraming` (already locked) over libdatachannel's data channel.
4. **Phase 7d — production hub.** TJ's `hub.spawndev.com:44365` already runs the tracker + STUN. The TURN cred-minting flow (open question #6 in [`phase7-webrfc-handoff.md`](phase7-webrtc-handoff.md)) gets settled here; the watch's `WatchWebRtcTransport` mints TURN creds via whichever pattern wins (probably tracker-gated ephemeral with a per-pair derived secret).

Each phase independently verifiable; each builds on what's locked before. The Bridge-side wire formats (BLE pairing handshake, WebRTC challenge, WebRTC data framing) are already locked by 26 tests in `SpawnWear.Bridge.Tests` — when phases 7b/7c land, they hit the same byte layouts the Companion already speaks fluently.

## Pairing UI

Optional but TJ-friendly: a watch-side LauncherScreen tile labeled "PAIR" that, when tapped:
- Renders a QR code (or 6-digit pin) of the watch's pubkey hex prefix.
- Spins for 30 seconds while listening for a successful handshake write.
- Flashes "PAIRED with <Companion friendly name>" on success.

This isn't strictly necessary — the Companion can pair as long as the watch is advertising — but a "PAIR" tile gives the user explicit control over when the watch accepts new pairings, which matches the BLE-as-trust-anchor security model. Park this as Phase 7-polish; not blocking.
