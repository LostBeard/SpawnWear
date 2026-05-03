using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.UI;
using nanoFramework.UI.GraphicDrivers;
using SpawnWear.Drivers;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Touch;

namespace SpawnWear
{
    public class Program
    {
        // Boot status markers encoded into the BLE device name. Pattern: 'SW-<displayStatus>-<touchStatus>'.
        static string _displayStatus = "?";
        static string _touchStatus = "?";

        public static void Main()
        {
            // Build #16 (2026-05-03): display + touch + advertise-only BLE.
            // Helper services + System.Net + System.Device.Wifi removed to keep deploy total
            // under the apparent 271 KB ceiling. BLE advertises with the SpawnWear UUID but
            // no GATT characteristics yet - WiFi provisioning + watch profile come back when
            // the deploy/heap budget is sorted (parked task).
            Debug.WriteLine("[SpawnWear] M0 - Main reached");

            EnablePowerRails();
            StartTouchProbe();
            // StartDisplay must run BEFORE BLE - the graphics heap allocates the
            // LARGEST free PSRAM block at init time. NimBLE consumes hundreds of KB
            // when it starts; if BLE wins the race for PSRAM the graphics heap gets
            // whatever scraps remain (~100KB observed) and FullScreen Bitmap OOMs.
            // Order: power -> touch -> display (claims PSRAM) -> BLE.
            StartDisplay();
            StartBleAdvertising();

            int beat = 0;
            while (true)
            {
                Debug.WriteLine("[SpawnWear] heartbeat #" + beat);
                beat++;
                Thread.Sleep(5000);
            }
        }

        static void EnablePowerRails()
        {
            try
            {
                Debug.WriteLine("[Power] P1 - Opening AXP2101 I2C device @ 0x" + BoardPins.AxpI2cAddress.ToString("X2"));
                var axpI2c = BoardSetup.OpenI2cDevice(BoardPins.AxpI2cAddress);
                var axp = new Axp2101Driver(axpI2c);
                Debug.WriteLine("[Power] P2 - Enabling DC1 + ALDO1 @ 3300mV (display rails)");
                axp.EnableDisplayRails();
                Debug.WriteLine("[Power] P3 - Display rails up");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Power] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void StartDisplay()
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
                    Debug.WriteLine("[Display] D5-fallback - FullScreen OOM, will use direct Write");
                }

                if (fb != null)
                {
                    _displayStatus = "P";
                    Debug.WriteLine("[Display] D5 - Painting solid WHITE via FullScreen Bitmap");
                    fb.Clear();
                    fb.FillRectangle(0, 0, BoardPins.LcdWidth, BoardPins.LcdHeight, Color.White);
                    fb.Flush();
                    _displayStatus = "OK";
                    Debug.WriteLine("[Display] D6 - Solid white flushed, status=OK");
                }
                else
                {
                    // FullScreen unavailable - graphics heap too small for the per-pixel PAL
                    // bitmap. Fall back to direct DisplayControl.Write with a small WHITE
                    // square so we still see SOMETHING on the panel and confirm the bus works.
                    // 100x100 = 20KB ushort[], small enough to fit in any managed heap.
                    Debug.WriteLine("[Display] D5b - Allocating 100x100 white pixel array");
                    ushort[] white = new ushort[100 * 100];
                    for (int i = 0; i < white.Length; i++)
                    {
                        white[i] = 0xFFFF;
                    }
                    Debug.WriteLine("[Display] D6b - DisplayControl.Write(155, 200, 100, 100, white)");
                    DisplayControl.Write(155, 200, 100, 100, white);
                    _displayStatus = "Wsq";
                    Debug.WriteLine("[Display] D7b - 100x100 white square written, status=Wsq");
                }
            }
            catch (Exception ex)
            {
                string t = ex.GetType().Name;
                if (t.Length > 12) t = t.Substring(0, 12);
                _displayStatus = "EX:" + t;
                Debug.WriteLine("[Display] EX " + ex.GetType().Name + ": " + ex.Message);
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
                    Debug.WriteLine("[Touch] fingers=" + snapshot.FingerCount +
                                    " p1=(" + snapshot.X1 + "," + snapshot.Y1 + ")" +
                                    (snapshot.FingerCount > 1 ? " p2=(" + snapshot.X2 + "," + snapshot.Y2 + ")" : ""));
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
