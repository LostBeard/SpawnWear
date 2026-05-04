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

        public void Unmount()
        {
            if (_card == null) return;
            try { _card.Unmount(); }
            catch (Exception ex) { Debug.WriteLine("[SdCard] unmount EX " + ex.Message); }
            IsMounted = false;
        }
    }
}
