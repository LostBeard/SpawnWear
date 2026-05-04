# nf-interpreter ESP32-S3 Deploy Ceiling

When the total wire-protocol deploy size approaches **~290 KB**, the LostBeard nf-interpreter fork on ESP32-S3 silently corrupts the on-flash assembly table. nf-deploy reports `100% complete` and `Done.`, but `nf-attach` then shows garbled assembly names starting at the consuming app's `.pe` and continuing through every later entry. The corrupted runtime keeps running, so the network stack stays alive (the watch still pings), but the application never reaches `Main()` and TCP connect succeeds while HTTP request handlers never fire.

Discovered 2026-05-04 during SpawnWear bring-up.

## Reproduction

Configuration A — works:
- 16 active references (no `nanoFramework.Device.Bluetooth`)
- Total `.pe` sum: 234,572 bytes
- Wire-protocol deploy total: ~235,316 bytes
- Result: deploys cleanly, app boots, all 16 assemblies show correct names in `nf-attach`

Configuration B — corrupts:
- 17 active references (BLE reference enabled + 4 BLE service `.cs` files in `<Compile>` group)
- Total `.pe` sum: 297,872 bytes
- Wire-protocol deploy total: 297,872 bytes
- Result: nf-deploy reports 100% / `Done.`, but `nf-attach` shows:
  ```
  [first ~10 entries clean]
  ???RE? v0.1.0.0                                       <- SpawnWear.pe corrupted
  /?i(>?r?.(??(|?o??o?o??&?io??&* v32845.45170.4386.28420
  ??       v4363.11275.7.4886
  ... [last 7 entries garbled]
  ```

Watch is reachable on TCP (ping + connection establishment work) but HTTP server never replies. Recovery: deploy a smaller (sub-285 KB) configuration; the redeploy clears the corruption.

## What's NOT the cause

- **Not a partition-size limit.** The deploy partition is 2.94 MB on the ESP32-S3 16 MB partition layout. Plenty of room.
- **Not a managed-heap limit.** PSRAM is 8 MB; managed heap is sized dynamically from `spiramMaxSize - ESP32_SPIRAM_FOR_IDF_ALLOCATION` per `targets/ESP32/_nanoCLR/Memory.cpp`. Even with 411 KB framebuffer + WiFi stack + SpawnWear, free heap is in megabytes.
- **Not `c_MaxAssemblies`.** That's 64 (`src/CLR/Include/nanoCLR_Runtime.h:1769`); we deploy 17.
- **Not `WP_PACKET_SIZE`.** That's 1024 bytes per packet, set in `src/CLR/Include/WireProtocol.h:33`. Deploys send hundreds of those packets sequentially without issue.

## Likely root cause: missing mmap cache invalidation

The most plausible candidate from reading the source is in `targets/ESP32/_common/Target_BlockStorage_ESP32FlashDriver.c`:

- **Line 178** `Esp32FlashDriver_Write` calls `esp_partition_write` and returns. It does NOT explicitly invalidate the cache.
- **Line 149-176** `Esp32FlashDriver_Read` reads through `esp32_flash_start_ptr + readAddress`, which is the `ESP_PARTITION_MMAP_DATA` mapping set up at boot in `Esp32FlashDriver_InitializeDevice` (line 70).
- **No `esp_cache_msync`, `Cache_Flush`, or `spi_flash_mmap_invalidate` calls anywhere in the file.**

If the deploy commit phase reads back any region it just wrote (CRC check, manifest verification, etc.), it would read stale data through the mmap'd region for sectors the cache had already loaded. ESP32-S3 has separate IBus / DBus caches; `ESP_PARTITION_MMAP_DATA` only invalidates one of them on write.

Why would this only manifest at >= 290 KB? Speculation: the cache window is some fixed size (typically 64 KB or 128 KB on ESP32 series). Below the threshold, all writes fit inside whatever cache window had already been invalidated; above the threshold, later writes start hitting cache lines populated during the readback phase.

This needs to be verified by adding cache-invalidate calls and rebuilding the firmware.

## How to investigate further

1. Add `esp_cache_msync(buffer_addr, numBytes, ESP_CACHE_MSYNC_FLAG_DIR_M2C)` (or the older `Cache_Flush`) to `Esp32FlashDriver_Write` after the `esp_partition_write` call.
2. Rebuild the ESP32-S3 firmware via the standard CMake + ESP-IDF flow (see `Notes/build-environment.md`).
3. Flash via `nf-flash-full.bat` or equivalent.
4. Deploy a >=300 KB SpawnWear configuration and verify `nf-attach` shows clean assembly names.

If cache invalidation is NOT the fix, instrument the wire-protocol commit path in `src/CLR/Debugger/Debugger.cpp` (the `AccessMemory_Write` case around line 831) with a per-write `ESP_LOGI` showing the offset and size; deploy a known-good 234 KB config and a known-bad 298 KB config side by side; diff the logs.

## Workaround until the fix lands

`tools/nf-deploy.cs` has a hard pre-flight guard:

```csharp
const int DeployCeilingBytes = 290000;
```

Deploys above this threshold bail with a loud error before any flash bytes are written, preventing accidental brick. Once the nf-interpreter fix is verified, raise the constant.

A standalone `tools/check-deploy-size.cs` does the same check without going through `nf-deploy.cs` — useful for CI / pre-commit checks.

## Bug that masked the ceiling for hours (also fixed)

Originally we believed the ceiling was much closer to 235 KB because every "stripped" build (BLE reference commented out) was actually still shipping the BLE assembly. The cause: `tools/nf-deploy.cs` had a regex that matched `<Reference Include="...">` tags inside XML comments, so `<!-- <Reference ... > -->` STILL added the assembly to the allow-list and pulled the `.pe` from `packages/`.

Fixed 2026-05-04 commit `958dd47` — the regex now strips `<!-- ... -->` blocks before matching. The "real" ceiling above (~290 KB wire) was only visible after this regex bug was closed; before the fix, it looked like even tiny changes pushed deploys over the limit.
