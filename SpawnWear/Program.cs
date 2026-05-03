using System;
using System.Diagnostics;
using System.Drawing;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.UI;
using nanoFramework.UI.GraphicDrivers;
using SpawnWear.Drivers;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Touch;
using SpawnWear.Services;
using SpawnWear.UI;

namespace SpawnWear
{
    public class Program
    {
        // Boot status markers encoded into the BLE device name. Pattern: 'SW-<displayStatus>-<touchStatus>'.
        static string _displayStatus = "?";
        static string _touchStatus = "?";

        // V1 watch-face state. Owned by Main, accessed from the touch callback to wake the loop.
        // _fingerDown is plain bool because nanoFramework's CoreLibrary doesn't ship
        // System.Runtime.CompilerServices.IsVolatile - the AutoResetEvent.Set + WaitOne
        // pair around every read/write provides happens-before ordering anyway.
        static EventLoop _eventLoop;
        static Watchface _watchface;
        static bool _fingerDown;
        static long _lastTouchUtcTicks;

        public static void Main()
        {
            // Build #19 (2026-05-03): event-driven main loop + HH:MM:SS watchface.
            // Replaces the heartbeat polling loop with an AutoResetEvent-driven select
            // pattern modeled on waveshare-watch-rs main.rs:603. CPU sleeps in
            // FreeRTOS tickless-idle between wakes; touch INT (or 1 Hz timeout)
            // re-arms the loop. Power note: AMOLED black background = ~0 mA per
            // off pixel; partial Flush of just the digits region pushes ~25 KB/s
            // instead of 411 KB for the full panel.
            Debug.WriteLine("[SpawnWear] M0 - Main reached");

            EnablePowerRails();
            StartTouchProbe();
            // StartDisplay must run BEFORE BLE - the graphics heap allocates the
            // LARGEST free PSRAM block at init time. NimBLE consumes hundreds of KB
            // when it starts; if BLE wins the race for PSRAM the graphics heap gets
            // whatever scraps remain (~100KB observed) and FullScreen Bitmap OOMs.
            // Order: power -> touch -> display (claims PSRAM) -> BLE -> watchface.
            Bitmap fb = StartDisplay();
            StartBleAdvertising();

            if (fb != null)
            {
                _watchface = new Watchface(fb, BoardPins.LcdWidth, BoardPins.LcdHeight);
                _eventLoop = new EventLoop(OnTick);
                Debug.WriteLine("[SpawnWear] M1 - Entering EventLoop");
                _eventLoop.Run();
            }
            else
            {
                // Display init failed - keep BLE alive so the device is still discoverable
                // for diagnostics. No watch face means no event loop, so we park.
                Debug.WriteLine("[SpawnWear] M1-fallback - No framebuffer, parking on Sleep loop");
                while (true) { System.Threading.Thread.Sleep(60000); }
            }
        }

        /// <summary>
        /// Called by EventLoop on every wake. Repaints the watch face (partial flush)
        /// and returns the desired next-tick timeout. Tick budget:
        ///   * Finger held       = 16 ms  (smooth 60 Hz - matches Rust port main.rs:612)
        ///   * Idle watchface    = 1000 ms (only seconds digit changes)
        /// </summary>
        static int OnTick(EventLoop.WakeReason reason)
        {
            try
            {
                _watchface.Tick();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Watchface] EX " + ex.GetType().Name + ": " + ex.Message);
            }
            return _fingerDown ? 16 : 1000;
        }

        static void EnablePowerRails()
        {
            try
            {
                Debug.WriteLine("[Power] P1 - Opening AXP2101 I2C device @ 0x" + BoardPins.AxpI2cAddress.ToString("X2"));
                var axpI2c = BoardSetup.OpenI2cDevice(BoardPins.AxpI2cAddress);
                var axp = new Axp2101Driver(axpI2c);
                Debug.WriteLine("[Power] P2 - Defensive rail enable (DC1 + ALDO1/2/3)");
                axp.EnableDisplayRails();
                Debug.WriteLine("[Power] P3 - Display rails up");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Power] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static Bitmap StartDisplay()
        {
            try
            {
                Debug.WriteLine("[Display] D1 - Building SpiConfiguration");
                var spi = new SpiConfiguration(
                    spiBus: 0,
                    chipselect: BoardPins.LcdCs,
                    dataCommand: -1,
                    reset: BoardPins.LcdReset,
                    backLight: -1);

                Debug.WriteLine("[Display] D2 - Building ScreenConfiguration");
                var screen = new ScreenConfiguration(
                    x: BoardPins.LcdColumnOffset,
                    y: 0,
                    width: BoardPins.LcdWidth,
                    height: BoardPins.LcdHeight,
                    graphicDriver: Co5300.GraphicDriver);

                _displayStatus = "I";
                Debug.WriteLine("[Display] D3 - DisplayControl.Initialize");
                // GraphicsDriver.GetSize returns GetWidthInWords(w) * h * 4 = 410*502*2 = 411,640
                // bytes (16bpp PAL bitmap, row-aligned to 4-byte words). The DisplayControl
                // IsFullScreenBufferAvailable check uses w*h*3/8 = 77KB which is bogus - the
                // actual native Bitmap allocation needs ~412KB. Request 512KB so FullScreen
                // can allocate with headroom for fonts/glyphs.
                uint maxBuffer = DisplayControl.Initialize(spi, screen, 512 * 1024);
                Debug.WriteLine("[Display] D4 - Initialize returned, maxBuffer=" + maxBuffer);
                _displayStatus = "F";

                Bitmap fb = null;
                try
                {
                    fb = DisplayControl.FullScreen;
                }
                catch (OutOfMemoryException)
                {
                    Debug.WriteLine("[Display] D5-fail - FullScreen OOM");
                    _displayStatus = "EX:NoFB";
                    return null;
                }

                if (fb == null)
                {
                    Debug.WriteLine("[Display] D5-fail - FullScreen returned null");
                    _displayStatus = "EX:NoFB";
                    return null;
                }

                _displayStatus = "OK";
                Debug.WriteLine("[Display] D5 - Framebuffer ready (" + BoardPins.LcdWidth + "x" + BoardPins.LcdHeight + ")");
                return fb;
            }
            catch (Exception ex)
            {
                string t = ex.GetType().Name;
                if (t.Length > 12) t = t.Substring(0, 12);
                _displayStatus = "EX:" + t;
                Debug.WriteLine("[Display] EX " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        static void StartTouchProbe()
        {
            try
            {
                Debug.WriteLine("[Touch] T1 - Opening I2C device + reset/int pins");
                var touchI2c = BoardSetup.OpenI2cDevice(BoardPins.TouchI2cAddress);
                var resetPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchReset);
                var intPin = BoardSetup.GpioController.OpenPin(BoardPins.TouchInt);

                Debug.WriteLine("[Touch] T2 - Constructing FT3168 driver");
                var touch = new Ft3168Driver(touchI2c, resetPin, intPin);
                Debug.WriteLine("[Touch] T3 - Calling Initialize");
                touch.Initialize();

                Debug.WriteLine("[Touch] T4 - Reading device id");
                byte id = touch.ReadDeviceId();
                _touchStatus = id == 0x03 ? "Tok" : "T" + id.ToString("X2");
                Debug.WriteLine("[Touch] T5 - Device id=0x" + id.ToString("X2") + " status=" + _touchStatus);

                touch.TouchEvent += (sender, snapshot) =>
                {
                    bool wasDown = _fingerDown;
                    _fingerDown = snapshot.FingerCount > 0;
                    if (_fingerDown) _lastTouchUtcTicks = DateTime.UtcNow.Ticks;

                    // Wake the main loop so it picks up the new finger state and applies
                    // the appropriate tick budget (16 ms while held, 1 s when idle).
                    if (_eventLoop != null) _eventLoop.Wake();

                    if (_fingerDown && !wasDown)
                    {
                        Debug.WriteLine("[Touch] DOWN at (" + snapshot.X1 + "," + snapshot.Y1 + ")");
                    }
                    else if (!_fingerDown && wasDown)
                    {
                        Debug.WriteLine("[Touch] UP");
                    }
                };
            }
            catch (Exception ex)
            {
                string t = ex.GetType().Name;
                if (t.Length > 8) t = t.Substring(0, 8);
                _touchStatus = "Tex" + t;
                Debug.WriteLine("[Touch] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void StartBleAdvertising()
        {
            try
            {
                Debug.WriteLine("[SpawnWear] BLE-1 - Calling BluetoothLEServer.Instance");
                BluetoothLEServer server = BluetoothLEServer.Instance;
                Debug.WriteLine("[SpawnWear] BLE-2 - Got BluetoothLEServer.Instance");

                string name = "SW-" + _displayStatus + "-" + _touchStatus;
                if (name.Length > 20) name = name.Substring(0, 20);
                server.DeviceName = name;
                Debug.WriteLine("[SpawnWear] BLE-3 - DeviceName='" + name + "'");

                Debug.WriteLine("[SpawnWear] BLE-4 - GattServiceProvider.Create");
                var result = GattServiceProvider.Create(BleUuids.WifiServiceUuid);
                if (result.Error != BluetoothError.Success)
                {
                    Debug.WriteLine("[SpawnWear] BLE-5-fail - Create error=" + result.Error);
                    return;
                }
                Debug.WriteLine("[SpawnWear] BLE-5 - Service created");

                result.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                });
                Debug.WriteLine("[SpawnWear] BLE-6 - Advertising as '" + name + "'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] BLE-EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
