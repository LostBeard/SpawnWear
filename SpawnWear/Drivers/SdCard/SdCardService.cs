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
                Configuration.SetPinFunction(2, DeviceFunction.SPI2_CLOCK);
                Configuration.SetPinFunction(1, DeviceFunction.SPI2_MOSI);
                Configuration.SetPinFunction(3, DeviceFunction.SPI2_MISO);

                var parameters = new SDCardSpiParameters
                {
                    slotIndex = 0,
                    spiBus = 2,
                    chipSelectPin = 17,
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
