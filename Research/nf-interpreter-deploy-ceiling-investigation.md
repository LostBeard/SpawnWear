# nf-interpreter Deploy Ceiling — Live Investigation 2026-05-05

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

## Findings

(To be filled in once the test runs.)

## Resolution

(To be filled in once the cause is confirmed and the fix is verified.)
