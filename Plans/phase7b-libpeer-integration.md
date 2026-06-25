# Phase 7b - Watch WebRTC via libpeer (integration plan)

> **SHIPPED 2026-06-24 — all milestones complete, DTLS blocker resolved.** The DTLS `handshake_failure(40)` was root-caused to the answerer DTLS role (two DTLS clients): fixed in the SipSorcery fork so the answerer mirrors the offer's setup role per RFC 4145/5763. Shipped to nuget.org (SipSorcery 10.0.7 / RTC 1.1.11), hardware-verified (console connected to a physical watch). libpeer↔SipSorcery interop is now PROVEN. The watch-side WebRTC transport is LIVE (mutual Ed25519, multiplexed channel bus). **As-built reference: `Docs/transport.md`.** The "last blocker" framing below is HISTORICAL.

## CURRENT STATE (2026-06-23) - milestones 1-5 REACHED; DTLS handshake is the last blocker
The pipeline works end-to-end: libpeer builds+links into nf, a `PeerConnection` constructs, the
watch self-produces an offer with candidates, the hand-rolled tracker-WS transport connects through
the REAL hub.spawndev.com, and the watch reaches the **early interop test (milestone 5)** against a
`SpawnWear.Bridge.Desktop answerroom` SipSorcery peer. **ICE connects both directions (~235ms) and
the DTLS handshake is bidirectional.** It then fails: the watch's mbedTLS sends `handshake_failure(40)`
/ returns -0x6E00 at a negotiation step. TLS1.2 cipher + curve + sig-alg are all instrumented and
none fire (so those PASS); both ends are ECDSA P-256. The DTLS crash and the watch-freeze are FIXED.
**Full pick-up state, the `g_sw_dtls_cp` diagnostic codes, and the next test (TLS 1.3 now genuinely
forced off - it had been silently overridden by a duplicate Kconfig line) are in memory
`project-spawnwear-2026-06-23-dtls-handshake-failure-narrowed` and the patch doc
`nf-interpreter/Patches/libpeer-dtls-srtp-recv-timeout.md`.** The plan below is the original
2026-06-22 integration research, kept for reference.

Supersedes the libdatachannel assumptions in `phase7-firmware-stub.md` §"WebRTC peer integration". Decision (2026-06-22, TJ): **`sepfy/libpeer`** as the watch-side WebRTC stack, integrated into the LostBeard nf-interpreter fork. Background: [[project_phase7b_libdatachannel_research_finding_2026_05_05]]. Stage 1 (browser/.NET WebRTC over the hub) is PROVEN ([[spawnwear-2026-06-22-phase7-webrtc-stage1a-proven]]); this is the watch's half.

## What libpeer gives us (researched 2026-06-22, sourced)
- **MIT.** ESP-IDF **5.2+** (our ~5.5.x fine). ESP32-S3 + **octal PSRAM** supported (the example `sdkconfig.defaults` sets `CONFIG_SPIRAM_MODE_OCT=y`). Browser-interop proven (Espressif's `esp_peer` fork + LiveKit's ESP32 SDK ship it).
- **SCTP is libpeer's OWN** (`-DCONFIG_USE_USRSCTP=0 -DCONFIG_USE_LWIP=1`) - NO usrsctp to vendor (the big libdatachannel blocker is gone).
- **Deps:** `mbedtls` (ESP-IDF's), `srtp` (libsrtp2, ESP-IDF managed component), `json` (cJSON, managed), `esp_netif`. Vendored-inside-libpeer coreHTTP/coreMQTT exist ONLY for its built-in WHIP/MQTT signaling - **we drop them**.
- **Data-channel-only = runtime config** (no build toggle): `PeerConfiguration{ audio_codec=CODEC_NONE, video_codec=CODEC_NONE, datachannel=DATA_CHANNEL_BINARY }`. Media simply never engages. DTLS is still mandatory (datachannel rides DTLS→SCTP).
- **Signaling is separable** - drive `PeerConnection` directly from our WebTorrent-tracker WS. Full-SDP exchange (candidates embedded by create_offer/answer's bounded gather) is the simple non-trickle path; trickle optional via the on-candidate callback.
- **Threading: caller-driven `peer_connection_loop(pc)` poll; libpeer spawns NO task.** We own the pump (one FreeRTOS task + mutex). Callbacks (onmessage/onopen/onclose, onicecandidate, oniceconnectionstatechange) fire synchronously from inside the loop.
- **Memory:** sourced figure is esp_peer's "< 60KB RAM" (the "<100KB" was unsourced folklore). DTLS handshake transiently needs more → `MBEDTLS_EXTERNAL_MEM_ALLOC=y` routes it to octal PSRAM. `CONFIG_DATA_BUFFER_SIZE` (example 100KB) is a tunable datachannel buffer - shrink for the watch.

## API surface to wrap (src/peer_connection.h)
`peer_connection_create(PeerConfiguration*)` / `_destroy` / `_close` / `_get_state` / `_loop`;
`_create_offer` / `_create_answer` (return local SDP) / `_set_remote_description(pc, sdp, SDP_TYPE_ANSWER|OFFER)`;
`_add_ice_candidate`; `_create_datachannel(pc, type, prio, reliability, label, protocol)` / `_datachannel_send(pc, buf, len)`;
callbacks: `_onicecandidate`, `_oniceconnectionstatechange`, `_ondatachannel(onmessage(msg,len,ud,sid), onopen, onclose)`.

## Integration mechanism - RESOLVED (Route A works)
nf's ESP32 build = `idf_build_process(s3 COMPONENTS ${IDF_COMPONENTS_TO_ADD})` with a CURATED component list (`CMake/binutils.ESP32.cmake`). It has a built-in helper **`nf_install_idf_component_from_registry(name object_id)`** (binutils.ESP32.cmake:572) that downloads a component from the ESP Component Registry to `$IDF_PATH/components/<name>` and strips its `idf_component.yml` (nf curates deps manually). Precedent: littlefs, tinyusb, esp_wifi_remote. **So Route A is real - no component-manager-enable needed.**
RECIPE APPLIED (2026-06-22, uncommitted on `feature/qspi-display-driver`):
- `nf_install_idf_component_from_registry(libpeer dfec0c2b-6788-4bca-8ccb-51a6083eb4b0)` (v0.0.3) next to the littlefs call.
- Added `libpeer` to `IDF_COMPONENTS_TO_ADD` + `idf::libpeer` to `IDF_LIBRARIES_TO_ADD`.
- sdkconfig (`sdkconfig.default_octal_ble_qspi.esp32s3`): DTLS already on (line 121); ADDED `CONFIG_MBEDTLS_EXTERNAL_MEM_ALLOC=y` + `CONFIG_PTHREAD_TASK_STACK_SIZE_DEFAULT=8192`. SPIRAM_MODE_OCT already set.
- **RESOLVED (build iteration 1):** libpeer's `REQUIRES mbedtls srtp json esp_netif`. mbedtls+json+esp_netif present; **`srtp` was missing** -> libpeer's manifest declares `sepfy/srtp ^2.0.4` (NOT espressif/libsrtp; component name `srtp`). usrsctp is a manifest dep but NOT in the ESP REQUIRES (ESP uses CONFIG_USE_USRSCTP=0), so not needed. ADDED `nf_install_idf_component_from_registry(srtp 27bc5f1c-b9da-441f-a3b0-3e7d6541d914)` (sepfy/srtp v2.3.0) + `srtp`/`idf::srtp` to the lists. **CMake CONFIGURE then PASSED** (`Configuring done`) - libpeer+srtp fully resolve into the nf ESP-IDF 5.5.4 build. Compile/link phase next.
- NOTE: added libpeer UNCONDITIONALLY (all ESP32 targets) for the experiment - gate to S3/BLE_QSPI before committing so other targets don't pull WebRTC. Also v0.0.3 registry tarball lacks the Sep-2025 binary-RX/srtp-init fixes; pin a newer `main` commit before relying on binary datachannel RX.

## sdkconfig deltas (to `sdkconfig.default_octal_ble_qspi.esp32s3`)
Already set on this board: `CONFIG_SPIRAM=y`, `CONFIG_SPIRAM_MODE_OCT=y`. ADD:
```
CONFIG_MBEDTLS_SSL_PROTO_DTLS=y
CONFIG_MBEDTLS_EXTERNAL_MEM_ALLOC=y
CONFIG_PTHREAD_TASK_STACK_SIZE_DEFAULT=8192
```
(DTLS-SRTP only needed for media - skip. SCTP is libpeer's own - no config.) GOTCHA: delete `nf-interpreter/sdkconfig` after editing the default or the change is ignored (per build workflow). Verify with `grep` post-build.

## Managed surface + interop assembly
- New InteropAssembly `SpawnDev.WebRTC` (mirror the `SpawnDev.Crypto` recipe: managed nfproj + `<NFMDP_GENERATE_STUBS>` → Stubs → implement NF_*.cpp → register via InteropAssemblies + the FindINTEROP cmake). See [[reference-nanoframework-native-interop-workflow]].
- Native binding wraps the peer_connection_* API; a dedicated FreeRTOS task pumps `peer_connection_loop` + a mutex; the `onmessage` callback marshals bytes up to managed (event/queue). Managed `Send`/`SetRemoteSdp`/`AddIceCandidate`/`CreateOffer` take the mutex + call directly.
- Managed wrapper mirrors a MINIMAL `IRTCPeerConnection`/`IRTCDataChannel` shape so `WatchWebRtcTransport` (Stage 3) reads like the Companion's `WebRtcTransport`.

## Bring-up milestones (each its own deploy)
1. **Component builds + links** (Route A or B) - firmware still boots, no managed WebRTC yet. Confirms libpeer + mbedtls-DTLS + libsrtp + the SCTP path compile into the nf ESP-IDF build at our sdkconfig.
2. **Construct + destroy** a `PeerConnection` + datachannel from managed without crashing (interop assembly minimal). Watch boots green.
3. **Offer/answer self-produce** - `create_offer` returns an SDP with candidates (log it; same `icediag` idea as Stage 1).
4. **WatchWebRtcTransport (Stage 3)** - tracker-signaling WS client (~200 lines managed, `wss://hub:44365/announce`) + the SAME `WebRtcChallenge` (Monocypher Ed25519, already shipped) + `WebRtcDataFraming` over libpeer's datachannel.
5. **EARLY INTEROP TEST (do ASAP, de-risks everything):** real watch (libpeer) ↔ a `SpawnWear.Bridge.Desktop -- companion` peer (SipSorcery) in the same hub room. Proves libpeer↔SipSorcery (standards-expected but UNVERIFIED). Then watch ↔ browser.

## Risks / unknowns to settle
- **nf component-manager support** (Route A vs B) - resolve in milestone 1.
- **libpeer↔SipSorcery interop UNVERIFIED** - the Stage 1 `Bridge.Desktop` peer is the test target (milestone 5); do it before building higher.
- **DTLS handshake OOM** - mitigated by `MBEDTLS_EXTERNAL_MEM_ALLOC` + 8192 pthread stack; do not omit.
- **Flash budget** - libpeer+mbedtls+libsrtp add code; watch the managed-app deploy ceiling (separate from firmware flash). Drop unused coreHTTP/coreMQTT.
- **libpeer version pin** - use a post-Sep-2025 `main` commit (binary-RX + srtp-init fixes), not registry `0.0.3`.

## First action
Milestone 1, Route A: add a minimal `idf_component.yml` with `sepfy/libpeer` and do a firmware build to learn whether nf's ESP-IDF flow downloads + compiles a managed component. If it doesn't, fall back to Route B (manual vendor). Either way the answer is a single build experiment.
