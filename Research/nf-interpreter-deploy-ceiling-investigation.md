# nf-interpreter Deploy Ceiling — Live Investigation 2026-05-05

> **RESOLVED 2026-06-25.** This 2026-05-05 investigation concluded "rebuild fixes it" and raised the guards to 2 MB — correct, but it predates a 2026-06-21 re-occurrence (see the sibling `nf-interpreter-deploy-ceiling.md`, which then proved the "ceiling" was two unrelated size-specific bugs, not an architectural limit). Final state: NO deploy ceiling, full 2.94 MB partition usable, 387 KB deployed clean on 2026-06-25. Treat this file and its sibling together as the closed record.

Companion to `nf-interpreter-deploy-ceiling.md`. That file documented the symptom; this file is the live investigation log as we trace the actual cause.

## Hypothesis

Reading the source identified the most plausible candidate as missing mmap cache invalidation in `targets/ESP32/_common/Target_BlockStorage_ESP32FlashDriver.c::Esp32FlashDriver_Write`. But that hypothesis doesn't fully explain the *sharp* threshold — cache staleness should produce probabilistic failures, not a deterministic cliff at exactly ~242 KB.

Alternative hypotheses worth ruling out:
1. **Cache invalidation race**: writes succeed at flash level, but subsequent reads via the mmap'd `esp32_flash_start_ptr` see stale data. Boot-time reads might miss the new content if the cache is stuck on pre-erase 0xFFs or pre-write old data.
2. **`esp_partition_write` returns failure silently past a boundary**: maybe the call returns non-OK at offset >= some threshold, but we don't check the return value. **Ruled out** by code reading: the existing implementation DOES check `== ESP_OK` and returns false on failure. But wire-protocol level might still report 100% if the failure isn't propagated up the stack correctly.
3. **Wire-protocol RX buffer overflow**: a fixed-size receive buffer somewhere in the WP stack that fills up at ~242 KB.
4. **Power / brown-out**: deploy chunks late in the stream cause power dips that corrupt the flash write.
5. **Flash sector boundary issue**: a specific 4 KB sector at offset ~242000 in the deploy partition fails writes for hardware reasons (worn cell?).

## Diagnostic plan

Added ESP_LOGE diagnostics to `Esp32FlashDriver_Write` and `Esp32FlashDriver_EraseBlock` (commit on the LostBeard nf-interpreter fork's `feature/qspi-display-driver` branch, build dir at `D:\users\tj\Projects\nf-interpreter\nf-interpreter\build`):

```c
ESP_LOGE(TAG, "[deploy-write] %u bytes @ offset %u  cum=%u  OK", numBytes, offsetAddress, s_cumWriteBytes);
ESP_LOGE(TAG, "[deploy-write] %u bytes @ offset %u  cum=%u  FAILED err=0x%x", ..., (unsigned)err);
ESP_LOGE(TAG, "[deploy-erase] partition erased size=%u err=0x%x", ...);
```

ESP_LOGE is at ERROR level so it's visible in the default IDF log filter (ESP_LOGI gets filtered to ERROR-only at runtime).

## Test workflow

1. Flash diagnostic firmware (one-time, takes ~30 s):
   ```
   tools/nf-flash-full.bat COM10
   ```
   Watch must be in bootloader mode (hold BOOT, tap RESET, release BOOT). Bootloader-mode COM port differs from runtime COM port (typically COM10 vs COM9).

2. Watch reboots into runtime mode on COM9. Verify new firmware runs:
   ```
   dotnet run tools/nf-attach.cs COM9
   ```

3. **Capture the IDF console output** (ESP_LOGE goes here, not the wire-protocol channel that `nf-deploy.cs`'s logger captures). On ESP32-S3 with native USB, the IDF console appears as a separate USB-Serial-JTAG CDC interface — list ports while the watch is in runtime mode to find the right one. Connect via `Get-Process` or `pyserial-miniterm` at 115200 baud.

4. Deploy a build under the cliff (240 KB) - confirm ESP_LOGE diagnostics show `[deploy-write] N bytes @ offset M cum=K OK` for every write.

5. Deploy a build over the cliff (>= 243 KB - re-add the BLE reference to the SpawnWear .nfproj, that adds 54 KB of nanoFramework.Device.Bluetooth.pe to the deploy):
   - If `[deploy-write] ... FAILED` lines appear: the write itself is failing past a threshold. Diagnose esp_partition_write internals.
   - If all writes show `OK` but `nf-attach` still shows corrupted assembly table: the writes succeeded at the flash level but reads via mmap return stale data. Test fix: add `esp_cache_msync` after each write.
   - If neither pattern matches: review the captured log for clues about where the data went wrong.

## Findings (2026-05-05 11:00)

After flashing the freshly-built firmware (commit `f06eded8` on the LostBeard `feature/qspi-display-driver` branch) and deploying a 295 KB build (BLE reference restored to push past the prior corruption threshold), **the deploy succeeded cleanly with no corruption**. All 17 assemblies showed correct names + versions in `nf-attach`. SpawnWear.pe loaded properly. BLE.pe loaded properly. Watch functions normally.

Per-write diagnostic confirmed every `esp_partition_write` call returned `ESP_OK` for the entire 295,660-byte deploy:
```
[runtime] [deploy-erase] partition erased size=2031616 err=0x0
[runtime] [deploy-write] 1016 bytes @ offset 0  cum=1016  OK
... [292 writes follow, all OK] ...
[runtime] [deploy-write] 4 bytes @ offset 295656  cum=295660  OK
```

## Resolution

The previously-flashed firmware on the watch was OLDER than the current source. Whatever fix landed between then and now resolved the corruption. The flashed firmware before today was likely from a build dated 2026-04-29 to 2026-05-03 area, and the current source has at least these later commits:
- `89a4a947` Bitmap CO5300 alignment (2026-05-03 20:27)
- `c925835a` DisplayControl Sleep / Wake / SetBrightness (2026-05-03 19:04)
- `d239323d` QSPI display NativeInit fix (2026-05-03 18:03)

**2026-05-05 11:30 update — bisected to confirm.** Three controlled rebuilds tested: (1) v5.5.4 + diag instrumentation, (2) v5.4.1 + diag, (3) v5.5.4 with diag REVERTED to byte-identical match of yesterday's flashed source. **All three are clean at 295 KB.** That rules out ESP-IDF version, source state, and the diagnostic itself. The most plausible remaining cause is **build-artifact variance triggering undefined behavior** somewhere in the codebase — different linker output → different addresses → bug doesn't fire. The actual UB hasn't been pinpointed and would require memory sanitizers + a way to reproduce yesterday's binary bit-for-bit (which we can't, since the original .bin is overwritten). Practical takeaway for anyone hitting nanoFramework ESP32-S3 deploy ceilings: **rebuild before treating as architectural**. Any rebuild seems to fix it.

**Deploy ceiling guards in `tools/nf-deploy.cs` and `tools/check-deploy-size.cs` raised from 242 KB to 2 MB** — generous sanity bound, far above any realistic deploy size. The 2.94 MB deploy partition itself is the hardware ceiling.

## Lesson saved

The "ceiling" diagnosis from 2026-05-04 was tied to a specific firmware version, not a permanent architectural limit. **Always re-test such "ceilings" against a fresh build before treating them as permanent constraints.** Note saved as feedback memory.

## Diagnostic instrumentation

The per-write `[deploy-write]` / `[deploy-erase]` logs added to `Esp32FlashDriver_Write` and `Esp32FlashDriver_EraseBlock` are useful research instrumentation. They live on the local `feature/qspi-display-driver` branch as commit `f06eded8` (NOT pushed upstream). For production firmware these should be reverted; we leave them in for now while the local working copy is the canonical artifact.
