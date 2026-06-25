# nf-interpreter ESP32-S3 Deploy Ceiling

> **RESOLVED 2026-06-25.** There is NO permanent deploy ceiling. The original corruption was fixed by a firmware rebuild (2026-05-05); the full **2.94 MB** deploy partition is usable, and a **387 KB** managed deploy went clean on 2026-06-25. The 2026-06-21 "Bug A 358,896-byte reset" and "Bug B CryptoSelfTest boot hang" are both closed. The `DeployCeilingBytes` / `~290 KB` / `~235 KB` figures below are HISTORICAL — kept as a record of the investigation, not as a current constraint. Read the rest of this file as a debugging journal, not a live limit.

When the total wire-protocol deploy size approaches **~290 KB**, the LostBeard nf-interpreter fork on ESP32-S3 silently corrupts the on-flash assembly table. nf-deploy reports `100% complete` and `Done.`, but `nf-attach` then shows garbled assembly names starting at the consuming app's `.pe` and continuing through every later entry. The corrupted runtime keeps running, so the network stack stays alive (the watch still pings), but the application never reaches `Main()` and TCP connect succeeds while HTTP request handlers never fire.

Discovered 2026-05-04 during SpawnWear bring-up.

## Reproduction

Configuration A — works:
- 16 active references (no `nanoFramework.Device.Bluetooth`)
- Total `.pe` sum: 242,068 bytes
- Wire-protocol deploy total: ~242,824 bytes
- Result: deploys cleanly, app boots, all 16 assemblies show correct names in `nf-attach`

Configuration B — corrupts:
- 16 active references (same set), but ~1 KB more SpawnWear.pe code
- Total `.pe` sum: 243,168 bytes
- Wire-protocol deploy total: 243,008 bytes (re-measured 2026-05-05)
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

## 2026-06-21 UPDATE — ceiling hit again, cache hypothesis TESTED + REJECTED

Hit this again building the SpawnWear UI library (total .pe crossed it). On the current `feature/qspi-display-driver` firmware the ceiling is **~358,648 bytes** total `.pe`. Precise symptom this time (sharper than 2026-05): the deploy is NOT a silent corruption — it **hard-fails** mid-commit:
```
Deploying 358648/358896 bytes.
Deploying 358896/358896 bytes.
Error writing 248 bytes to device @ 0x003678F8.   <- 0x310000 + 358648
*** ERROR deploying assemblies to the device ***
```
The device writes EVERY chunk below offset 358,648 fine, then dies on the final 248-byte chunk **at the fixed flash address 0x3678F8** and stops replying (the partial deploy then bricks the runtime until power-cycle; the CLR ignores the uncommitted deploy and boots blank). The failing build was exactly 358,896 bytes — `358,896 − 358,648 = 248` = one wire chunk over.

**The cache-invalidation hypothesis (the 2026-05 lead) was implemented and is WRONG / insufficient.** Added `esp_cache_msync(mmap_addr, n, ESP_CACHE_MSYNC_FLAG_DIR_M2C | ESP_CACHE_MSYNC_FLAG_UNALIGNED)` after `esp_partition_write` in `Esp32FlashDriver_Write` (forward-declared, since esp_mm/include isn't on that file's path), rebuilt, reflashed → **identical failure at the same byte 0x3678F8.** So missing DBus-cache invalidation is NOT the root cause (or esp_cache_msync is a no-op on a flash-mmap vaddr). That change is currently UNCOMMITTED in the nf-interpreter working tree — revert it or refine it (try the ROM `Cache_Invalidate_Addr`) next session.

**REPARTITION TEST — RULES OUT BAD SECTOR (2026-06-21 PM).** Moved the deploy region from `0x310000` to **`0x370000`** (factory enlarged 0x300000→0x360000, ends at 0x370000) to put 0x3678F8 in factory's never-written tail, rebuilt+reflashed (erase_flash), and redeployed the SAME 358,896-byte build. Result: **the failure MOVED to `0x3C78F8` = `0x370000 + 358,648` — the exact same OFFSET (0x578F8 = 358,648) into the relocated region.** A bad flash sector would have stayed at the absolute address 0x3678F8; it tracked the OFFSET instead. **So it is NOT flash hardware.** It is a **deterministic software limit at ~358,648 bytes into the deployment**, in the nanoCLR deploy WRITE/commit path. Also kills the 2026-05 "layout variance / UB" theory — across 3 of my rebuilds the limit stayed at the identical offset 358,648 (a UB/layout bug would jitter).

What's ruled OUT now: partition size (2.5-2.9 MB), managed heap (PSRAM, MBs), bad sector, layout variance. What it IS: a fixed ~358 KB software ceiling in `Monitor_DeploymentExecute` / `AccessMemory_Write` / the deployment-storage write path. Note the data-bus MMU has ~512 64KB entries (~32 MB) so a too-small flash-mmap window is unlikely the cause — more likely an internal buffer / accumulation / commit-resolve limit hit at ~358 KB.

**NEXT-SESSION ROOT-CAUSE (no more guessing — instrument):** add `CLR_Debug::Printf` / `ESP_LOGI` logging to `Esp32FlashDriver_Write` (already logs "Writting %dB @ %d" — capture it), `Esp32FlashDriver_InitializeDevice` (log the `esp_partition_mmap` return + mapped size), and the CLR deploy-commit (`src/CLR/Debugger/Debugger.cpp` AccessMemory_Write ~line 831 + Monitor_DeploymentExecute) at offsets near 358,648. One deploy of the 358,896 build then shows EXACTLY whether the last `esp_partition_write` returns ESP_OK (→ crash is in the commit/resolve) or never returns (→ crash is the write itself), and whether any read-back past 358 KB happens. Then fix that specific spot. The repartition (deploy@0x370000) is currently FLASHED on the watch + the CSV change is UNCOMMITTED in nf-interpreter (revert or keep — it's a harmless valid layout, just doesn't fix the ceiling).

**Workaround that WORKS:** keep total `.pe` under ~358,000 (removed boot `CryptoSelfTest` → UI-kit shipped at 358,084, hardware-verified). Recovery if a deploy bricks the runtime: `tools/nf-recover-py313.bat COM6` (erase_flash + reflash) then redeploy a sub-358k build.

## 2026-06-21 EVENING UPDATE (Riker) — the "CEILING" IS ILLUSORY; two separate bugs

Everything above framed this as a monotonic ~358 KB deploy ceiling. **That framing is WRONG.** Hardware testing this evening proved a **LARGER build deploys fine**:

- 358,084 bytes → deploys + runs ✅
- **358,896 bytes → fails to deploy** ❌ (the "ceiling" build)
- **360,904 + 361,048 bytes → deploy cleanly ✅** ← bigger, but fine

So there is **no size ceiling**. There are two distinct bugs:

**Bug A — the 358,896 deploy failure (narrow, size-specific).** The device **RESETS (reboots) on the final write** — it is NOT flash corruption, NOT a crash/panic/hang:
- `nf-attach` immediately after the failure → device ALIVE + responsive (a blocked task would leave it dead; `Monitor_WriteMemory` always replies even on error, so "No reply" + alive ⇒ reset mid-command).
- NO coredump despite full coredump-to-flash + INT_WDT + TASK_WDT(panic) instrumentation ⇒ a plain reset, not a panic/WDT.
- `reboot=false` deploy still fails ⇒ device-side, not the host's reboot signal.
- Reset reason reads `ESP_RST_USB`, but a plain power-cycle also reads USB on this board ⇒ generic/inconclusive.
- **Workaround: pad the build ~2 KB** to dodge the bad size. Real fix = why ~358,896 specifically resets in nanoFramework's device-side deploy path (deep, low priority).

**Bug B — the REAL blocker: boot `CryptoSelfTest()` HANGS the watch (black screen).** Isolated + confirmed:
- 361,048 pad-only build (NO crypto) → home screen ✅; 360,904 crypto+pad build → BLACK ❌ (same size/deploy/firmware, only crypto differs).
- `CryptoSelfTest()` runs before `EnablePowerRails()` in `Program.cs`, so a hang there ⇒ display never powers ⇒ black/no-touch.
- It's a HANG not an exception (wrapped in try/catch) ⇒ a native Monocypher call enters and never returns. Likely the first call `Ed25519.GenerateKeyPair` blocking on RNG/entropy at early boot (UNVERIFIED).
- **Never seen before because Bug A always failed the deploy first** — the boot self-test never ran on hardware until we padded past Bug A.
- NEXT: bisect which of the 5 native crypto calls hangs (re-apply `Plans/crypto-selftest-boot.patch`). Full handoff in agent memory `project-spawnwear-EOD-2026-06-21-riker-deploy-ceiling-is-illusory-crypto-selftest-hangs`.

The old `check-deploy-size.cs` / `DeployCeilingBytes` guards are based on the disproven ceiling model — treat them as "avoid the 358,896 bad-spot," not a hard limit.
