using System;
using System.Diagnostics;
using nanoFramework.Hardware.Esp32;
using nanoFramework.System.IO.FileSystem;

namespace SpawnWear.Drivers.SdCard
{
    /// <summary>
    /// Mounts the watch's microSD slot. The Waveshare 2.06 watch wiki documents
    /// the slot as SPI-mode (interface table on
    /// https://www.waveshare.com/wiki/ESP32-S3-Touch-AMOLED-2.06):
    ///
    ///   CS   (SS)   = GPIO 17  -> chipSelectPin
    ///   DI   (MOSI) = GPIO 1   -> SPI2_MOSI
    ///   DO   (MISO) = GPIO 3   -> SPI2_MISO
    ///   SCK  (SCLK) = GPIO 2   -> SPI2_CLOCK
    ///
    /// (The vendor's `07_LVGL_SD_Test` Arduino demo uses SD_MMC because the
    /// ESP32-S3 SDMMC peripheral can address the same pins, but the wiki's
    /// SPI mapping is the documented contract.)
    ///
    /// On successful mount the volume surfaces at `D:\` per nanoFramework's
    /// SDCardSpiParameters.slotIndex doc.
    ///
    /// Note 2026-05-05: small (1GB) cards typically ship pre-formatted FAT16,
    /// which the runtime's FATFS rejects with CLR_E_VOLUME_NOT_FOUND. Reformat
    /// to FAT32 (Windows: `format X: /FS:FAT32 /Q`) before expecting Mount()
    /// to succeed. In-watch reformat via DriveInfo.Format isn't viable on this
    /// runtime - the SD slot's mount failure prevents drive registration, so
    /// there's no DriveInfo for the format to operate on. The /sdformat HTTP
    /// endpoint is wired but currently 500s for the same reason.
    /// </summary>
    public class SdCardService
    {
        SDCard _card;
        public bool IsMounted { get; private set; }
        public string MountPath => "D:\\";

        public bool Initialize()
        {
            try
            {
                // 2026-05-04: SD card mount FAILS at runtime image level on
                // ESP32_S3_BLE-1.16.0.563 - independent of:
                //   - bus mode tried (SPI mode AND 1-bit MMC mode both fail)
                //   - SPI bus selection (SPI2_HOST and SPI3_HOST both fail)
                //   - card filesystem format (FAT16 and FAT32 both fail)
                //   - pin map correctness (verified via Configuration.GetFunctionPin
                //     readback - confirmed pins are routed correctly)
                //
                // All attempts return CLR_E_VOLUME_NOT_FOUND from MountNative,
                // which means Storage_MountSpi/MountMMC's underlying call to
                // esp_vfs_fat_sdspi_mount/esp_vfs_fat_sdmmc_mount returned an
                // ESP-IDF error (logged via ESP_LOGE in
                // Target_System_IO_FileSystem.c::LogMountResult).
                //
                // ESP_LOGE output goes to USB-CDC raw text stream, not the
                // wire-protocol-multiplexed Debug.WriteLine channel that
                // nf-deploy.cs / VS Output captures. The `Logging` class in
                // nanoFramework.Hardware.Esp32 1.6.37 is `internal` so we
                // can't reach the ESP_LOG channel from managed code either.
                //
                // Path forward: rebuild the nanoCLR runtime image from the
                // LostBeard nf-interpreter fork with explicit Debug.WriteLine
                // calls added to Storage_MountSpi/MountMMC error paths so the
                // actual ESP-IDF error code (ESP_FAIL / ESP_ERR_*) surfaces in
                // the wire-protocol log. Then we know whether the failure is
                // bus init, card init, or FATFS mount, and we can target a fix.
                //
                // Until that rebuild lands, SD card is not usable from managed
                // code on this watch. Internal flash at I:\ remains the
                // working storage path (~1MB LittleFS partition). PairingService
                // already uses it for keypair persistence.
                //
                // Vendor Arduino demo (07_LVGL_SD_Test.ino) does work via
                // SD_MMC.setPins + SD_MMC.begin - so the hardware is fine,
                // the issue is purely in the nanoFramework runtime stack.
                //
                // Currently configured for 1-bit MMC mode with verified-correct
                // pin routing as the closest match to what the vendor demo does
                // - so once the runtime fix lands, nothing in this file should
                // need to change.
                Configuration.SetPinFunction(2, DeviceFunction.SDMMC1_CLOCK);
                Configuration.SetPinFunction(1, DeviceFunction.SDMMC1_COMMAND);
                Configuration.SetPinFunction(3, DeviceFunction.SDMMC1_D0);

                int rbClk  = Configuration.GetFunctionPin(DeviceFunction.SDMMC1_CLOCK);
                int rbCmd  = Configuration.GetFunctionPin(DeviceFunction.SDMMC1_COMMAND);
                int rbD0   = Configuration.GetFunctionPin(DeviceFunction.SDMMC1_D0);
                Debug.WriteLine("[SdCard] pin map readback SDMMC1_*: clk=" + rbClk + " cmd=" + rbCmd + " d0=" + rbD0);

                var parameters = new SDCardMmcParameters
                {
                    slotIndex = 0,
                    dataWidth = SDCard.SDDataWidth._1_bit,
                };

                _card = new SDCard(parameters, new CardDetectParameters());
                return TryMount();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SdCard] init failed: " + ex.GetType().Name + ": " + ex.Message);
                IsMounted = false;
                return false;
            }
        }

        /// <summary>
        /// Tries to mount the card. Separated from Initialize so a TryFormat
        /// success can re-mount without re-running pin / ctor setup. Safe to
        /// call multiple times; throws are caught.
        /// </summary>
        public bool TryMount()
        {
            if (_card == null) return false;
            if (IsMounted) return true;
            try
            {
                _card.Mount();
                IsMounted = true;
                Debug.WriteLine("[SdCard] mounted at " + MountPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SdCard] mount failed: " + ex.GetType().Name + ": " + ex.Message);
                IsMounted = false;
                return false;
            }
        }

        /// <summary>
        /// Formats the SD card via nanoFramework's DriveInfo.Format. DESTRUCTIVE —
        /// erases the entire card. Triggered by the explicit POST /sdformat HTTP
        /// route so a misbehaving caller can't auto-wipe a card on a transient
        /// mount glitch. Common reason to need this: 1GB-class cards ship
        /// pre-formatted FAT16 / FAT12, which our runtime's FATFS rejects with
        /// CLR_E_VOLUME_NOT_FOUND - reformatting to FAT32 in-place is faster
        /// than pulling the card out for Windows.
        /// </summary>
        public bool TryFormat(string fileSystem)
        {
            // Diagnostic dump before any action so the failure mode is visible.
            try
            {
                var beforeDrives = System.IO.DriveInfo.GetDrives();
                Debug.WriteLine("[SdCard] Format pre-state: " + beforeDrives.Length + " drive(s) registered");
                foreach (var d in beforeDrives)
                {
                    Debug.WriteLine("[SdCard]   drive: " + d.Name + " type=" + d.DriveType);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SdCard] GetDrives EX: " + ex.GetType().Name + ": " + ex.Message);
            }

            // MountRemovableVolumes asks the runtime to enumerate removables that
            // weren't mounted at boot - in our case the SD card whose existing
            // FAT16 volume FATFS rejected. If this surfaces the card, Format has
            // a DriveInfo to operate on. The doc warns this isn't supported on
            // every target; we catch and continue.
            try
            {
                Debug.WriteLine("[SdCard] DriveInfo.MountRemovableVolumes()");
                System.IO.DriveInfo.MountRemovableVolumes();
                var afterDrives = System.IO.DriveInfo.GetDrives();
                Debug.WriteLine("[SdCard] post-MountRemovable: " + afterDrives.Length + " drive(s) registered");
                foreach (var d in afterDrives)
                {
                    Debug.WriteLine("[SdCard]   drive: " + d.Name + " type=" + d.DriveType);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SdCard] MountRemovableVolumes EX: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                Debug.WriteLine("[SdCard] Format starting (filesystem=" + fileSystem + ", path=" + MountPath + ")");
                var drive = new System.IO.DriveInfo(MountPath);
                drive.Format(fileSystem, 0);
                Debug.WriteLine("[SdCard] Format(" + fileSystem + ") succeeded");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SdCard] Format failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        public void Unmount()
        {
            if (_card == null) return;
            try { _card.Unmount(); }
            catch (Exception ex) { Debug.WriteLine("[SdCard] unmount EX " + ex.Message); }
            IsMounted = false;
        }
    }
}
