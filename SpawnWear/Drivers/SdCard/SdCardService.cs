using System;
using System.Diagnostics;
using nanoFramework.Hardware.Esp32;
using nanoFramework.System.IO.FileSystem;

namespace SpawnWear.Drivers.SdCard
{
    /// <summary>
    /// Mounts the watch's microSD slot. The Waveshare 2.06 watch wires the slot
    /// to the ESP32-S3's SDMMC peripheral (1-bit MMC mode), NOT SPI. Earlier
    /// scaffold used SDCardSpiParameters and never matched the hardware, which
    /// is why every boot logged CLR_E_VOLUME_NOT_FOUND with a card inserted.
    ///
    /// Pin assignments (vendor pin_config.h `07_LVGL_SD_Test`):
    ///   SDMMC_CLK  = GPIO 2  -> SDMMC1_CLOCK
    ///   SDMMC_CMD  = GPIO 1  -> SDMMC1_COMMAND
    ///   SDMMC_DATA = GPIO 3  -> SDMMC1_D0
    ///   (GPIO 17 was the SPI CS in the old config; unused in MMC mode.)
    ///
    /// Vendor demo uses `SD_MMC.setPins(SDMMC_CLK, SDMMC_CMD, SDMMC_DATA)` +
    /// `SD_MMC.begin("/sdcard", true)` - the `true` arg is "1-bit mode", which
    /// matches `SDCardMmcParameters.dataWidth = SDCard.SDDataWidth._1_bit`.
    ///
    /// On successful mount, slot 0 surfaces at `D:\` (per nanoFramework's
    /// SDCardMmcParameters.slotIndex doc: "Slot 0 will mount as drive D:\,
    /// slot 1 = E:\ etc").
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
                Configuration.SetPinFunction(2, DeviceFunction.SDMMC1_CLOCK);
                Configuration.SetPinFunction(1, DeviceFunction.SDMMC1_COMMAND);
                Configuration.SetPinFunction(3, DeviceFunction.SDMMC1_D0);

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
