using System;
using System.Diagnostics;
using nanoFramework.Hardware.Esp32;
using nanoFramework.System.IO.FileSystem;

namespace SpawnWear.Drivers.SdCard
{
    /// <summary>
    /// Mounts the watch's microSD slot via SDSPI (SD card in SPI mode). The volume
    /// surfaces at `D:\`.
    ///
    /// WHY SPI, not SDMMC: this watch's dedicated SDMMC controller is dead under the
    /// nanoFramework runtime - proven exhaustively 2026-06-20: the controller accepts a
    /// command (start_cmd clears) but never clocks it out (no command-done, no
    /// response-timeout), with every SDMMC/clock register byte-identical to a bare
    /// ESP-IDF app that mounts the same card. Root cause unlocated; SDSPI bypasses it.
    /// SD-over-SPI on the SPI peripheral (which works in nf - it drives the display)
    /// initializes the card and reads real data. Same lesson as the QSPI display: on
    /// this board, reach for the SPI bus when a dedicated controller fights.
    /// See SpawnWear/sd-card-nanoframework-dead-clock-investigation.md.
    ///
    /// Bus map: the CO5300 display owns SPI2_HOST (QSPI), so the SD uses SPI bus 2 ->
    /// native SPI3_HOST (busIndex 1). Pins SCLK=2 / MOSI(CMD)=1 / MISO(D0)=3, CS=GPIO17.
    /// AXP2101 DC1 + ALDO1 (Axp2101Driver) still powers the card rail.
    ///
    /// Note: small (1GB) cards often ship FAT16/FAT12; the runtime FATFS rejects those
    /// with CLR_E_VOLUME_NOT_FOUND - reformat to FAT32 (or enable exFAT).
    /// </summary>
    public class SdCardService
    {
        SDCard _card;
        public bool IsMounted { get; private set; }
        public string MountPath => "D:\\";

        // A few managed mount retries on top of the runtime's own retry loop, to ride
        // out a transient marginal contact at boot. A persistently bad contact won't
        // recover here (it needs the card reseated).
        const int MountRetries = 3;

        public bool Initialize()
        {
            try
            {
                // SDSPI (SD card in SPI mode). The watch's dedicated SDMMC controller is dead
                // under nanoFramework (it accepts a command but never clocks it out; proven
                // 2026-06-20). SD-over-SPI on the SPI peripheral works (card_init=0x0, real
                // reads). The display owns SPI2_HOST (QSPI), so the SD uses SPI bus 2 ->
                // native SPI3_HOST. Pins: SCLK=2, MOSI=CMD=1, MISO=D0=3, CS=17.
                Configuration.SetPinFunction(BoardPins.SdClk, DeviceFunction.SPI2_CLOCK);
                Configuration.SetPinFunction(BoardPins.SdCmd, DeviceFunction.SPI2_MOSI);
                Configuration.SetPinFunction(BoardPins.SdData, DeviceFunction.SPI2_MISO);

                var parameters = new SDCardSpiParameters
                {
                    spiBus = 2,                     // managed bus 2 -> busIndex 1 -> SPI3_HOST
                    chipSelectPin = BoardPins.SdCs, // GPIO17
                };

                _card = new SDCard(parameters, new CardDetectParameters { enableCardDetectPin = false });

                System.Threading.Thread.Sleep(200); // let the card power rail settle
                for (int attempt = 1; attempt <= MountRetries; attempt++)
                {
                    if (TryMount())
                    {
                        Debug.WriteLine("[SdCard] mounted on attempt " + attempt);
                        return true;
                    }
                    System.Threading.Thread.Sleep(150);
                }
                Debug.WriteLine("[SdCard] mount failed after " + MountRetries + " attempts (check card seating)");
                return false;
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
