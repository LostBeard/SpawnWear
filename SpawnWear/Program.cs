using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.Presentation.Media;
using nanoFramework.UI;
using nanoFramework.UI.GraphicDrivers;
using SpawnWear.Drivers;
using SpawnWear.Drivers.Touch;

namespace SpawnWear
{
    public class Program
    {
        public static void Main()
        {
            Debug.WriteLine("[SpawnWear] Boot - Waveshare ESP32-S3-Touch-AMOLED-2.06 watch firmware");

            StartDisplay();
            StartTouchProbe();
            StartBleAdvertising();

            Thread.Sleep(Timeout.Infinite);
        }

        // -------------------------------------------------------------------
        // Display (CO5300 over hybrid QSPI). Initialized via DisplayControl with
        // the Co5300 GraphicDriver descriptor. Pin numbers for SCLK + 4 data lines
        // + CS come from the runtime's target-local target_qspi_display_config.h
        // (Waveshare 2.06 watch defaults baked in for now); only Reset comes
        // through the SpiConfiguration.
        // -------------------------------------------------------------------
        static void StartDisplay()
        {
            try
            {
                var spi = new SpiConfiguration(
                    spiBus: 0,                   // ignored on QSPI variant - target-local QSPI_DISPLAY_HOST wins
                    chipselect: BoardPins.LcdCs, // ignored on QSPI variant - target-local QSPI_DISPLAY_CS wins
                    dataCommand: -1,             // QSPI has no DC pin - encoded in cmd byte instead
                    reset: BoardPins.LcdReset,   // ignored on QSPI variant - target-local QSPI_DISPLAY_RST wins
                    backLight: -1);              // CO5300 brightness via panel register 0x51

                var screen = new ScreenConfiguration(
                    x: BoardPins.LcdColumnOffset,  // CO5300 410 panel sits inside a wider RAM region
                    y: 0,
                    width: BoardPins.LcdWidth,
                    height: BoardPins.LcdHeight,
                    graphicDriver: Co5300.GraphicDriver);

                uint maxBuffer = DisplayControl.Initialize(spi, screen);
                Debug.WriteLine("[Display] Initialized. Max buffer: " + maxBuffer + " bytes");

                // First-pixels test: solid red full-screen, then "Hello SpawnWear" centered.
                Bitmap fb = DisplayControl.FullScreen;
                if (fb != null)
                {
                    fb.Clear();
                    fb.DrawRectangle(new Pen(Color.Red), 0, 0, BoardPins.LcdWidth, BoardPins.LcdHeight);
                    fb.Flush();
                    Debug.WriteLine("[Display] First flush: red border");
                }
                else
                {
                    Debug.WriteLine("[Display] FullScreen bitmap unavailable (insufficient memory?)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Display] init failed: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // Touch (FT3168) - probe at boot, log every touch event for now.
        // Will be lifted into a system service once the display + UI shell exist.
        // -------------------------------------------------------------------
        static void StartTouchProbe()
        {
            try
            {
                var touchI2c = BoardSetup.OpenI2cDevice(BoardPins.TouchI2cAddress);
                var resetPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchReset);
                var intPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchInt);

                var touch = new Ft3168Driver(touchI2c, resetPin, intPin);
                touch.Initialize();

                byte id = touch.ReadDeviceId();
                Debug.WriteLine("[SpawnWear] FT3168 device id = 0x" + id.ToString("X2") + (id == 0x03 ? " (OK)" : " (UNEXPECTED)"));

                touch.TouchEvent += (sender, snapshot) =>
                {
                    Debug.WriteLine("[Touch] fingers=" + snapshot.FingerCount +
                                    " p1=(" + snapshot.X1 + "," + snapshot.Y1 + ")" +
                                    (snapshot.FingerCount > 1 ? " p2=(" + snapshot.X2 + "," + snapshot.Y2 + ")" : ""));
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] Touch init failed: " + ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // BLE - same scaffold as before; still useful for headless debugging
        // until the on-device Settings shell exists.
        // -------------------------------------------------------------------
        static void StartBleAdvertising()
        {
            try
            {
                BluetoothLEServer server = BluetoothLEServer.Instance;
                server.DeviceName = "SpawnWear";

                var debug = new DebugConsoleService();
                var profile = new WatchProfileService();
                var wifi = new WifiConfigService(debug, profile);

                if (!wifi.Initialize())
                {
                    Debug.WriteLine("[SpawnWear] BLE initialization failed");
                    return;
                }

                var serviceDataWriter = new DataWriter();
                serviceDataWriter.WriteByte(0x01);

                wifi.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                    ServiceData = serviceDataWriter.DetachBuffer()
                });

                debug.Log("[SpawnWear] Advertising as 'SpawnWear'.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] BLE start failed: " + ex.Message);
            }
        }
    }
}
