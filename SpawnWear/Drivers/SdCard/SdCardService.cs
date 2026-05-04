using System;
using System.Diagnostics;
using nanoFramework.Hardware.Esp32;
using nanoFramework.System.IO.FileSystem;

namespace SpawnWear.Drivers.SdCard
{
    /// <summary>
    /// Mounts the watch's microSD slot via SPI mode (the slot is wired for
    /// SPI, not 4-bit MMC, per the rust-watch reference + the schematic).
    ///
    /// Pin assignments (from BoardPins / Notes/hardware.md):
    ///   CLK  = GPIO2  -> SPI2_CLOCK
    ///   CMD  = GPIO1  -> SPI2_MOSI
    ///   DATA = GPIO3  -> SPI2_MISO
    ///   CS   = GPIO17 (driven by SDCard driver via passive GPIO output)
    ///
    /// On successful mount, files appear under D:\ - so app payloads at
    /// /sd/apps/&lt;name&gt;/app.pe are reachable as D:\apps\&lt;name&gt;\app.pe.
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
