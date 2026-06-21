using System;
using System.Diagnostics;
using System.Drawing;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.UI;
using nanoFramework.UI.GraphicDrivers;
using SpawnWear.Drivers;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Rtc;
using SpawnWear.Drivers.SdCard;
using SpawnWear.Drivers.Touch;
using SpawnWear.Drivers.Wifi;
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
        static ScreenNavigator _nav;
        static Axp2101Driver _axp;
        static Pcf85063Driver _rtc;
        static WifiService _wifi;
        static SdCardService _sd;
        static bool _sdIsolationTest = false;
        static HttpServer _http;
        static Bitmap _fb; // shared framebuffer reference for screenshots
        static int _bootButtonClickPending; // set by ISR, drained by main loop
        static bool _fingerDown;
        static long _lastTouchUtcTicks;

        // Tap-gesture detection state. A "tap" = finger goes down, stays within
        // a small radius for under TapMaxMs, then lifts. Anything longer is a
        // long-press (Phase 2 dispatch); anything that moves beyond the radius
        // is a swipe (also Phase 2). For V1 we treat any short single-finger
        // touch as a tap and let the navigator cycle screens.
        const int TapMaxMs = 350;
        const int TapMaxMoveSquared = 30 * 30;
        // Long-press = finger held in roughly the same place for >= 800 ms.
        // Triggers ScreenNavigator.GoHome() so the user can always get back to
        // the watch face regardless of how deep into the screen rotation they
        // are - useful as a "back to home" gesture before we have a real
        // navigation stack with a back button.
        const int LongPressMinMs = 800;
        static long _fingerDownUtcTicks;
        static int _fingerDownX;
        static int _fingerDownY;
        static int _fingerLastX;
        static int _fingerLastY;
        // Phone-style wake-tap consumption: when the panel is asleep and the
        // user touches it, the touch wakes the screen but the UP event MUST NOT
        // dispatch as a UI tap - otherwise the tap that woke the watch also
        // triggers whatever row was last under the finger and the user gets
        // "tap turns on, immediately turns back off" behavior. We capture the
        // ScreenState at finger-DOWN; only Active-state taps reach the navigator.
        static ScreenState _stateAtFingerDown;

        // Power-state machine driven by time-since-last-touch. Mirrors waveshare-watch-rs
        // main.rs:613-620 multi-tier tick budget.
        enum ScreenState { Active, Dim, Sleep }
        static ScreenState _screenState = ScreenState.Active;

        // Idle thresholds. Tunable - 15 s / 30 s gives a snappy demo without burning power
        // on a stationary face. For production these will move into a Settings page.
        const long DimAfterSeconds = 15;
        const long SleepAfterSeconds = 30;
        const byte BrightnessActive = 0xFF;
        const byte BrightnessDim = 0x40;

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
            // 2026-06-19 SD isolation test: boot ONLY power + SD (no RTC/touch/WiFi/
            // display/BLE) to check if another subsystem disrupts SDMMC. Set false to restore.
            if (_sdIsolationTest)
            {
                StartSdCard();
                Debug.WriteLine("[SD-TEST] isolation complete (power + SD only)");
                return;
            }
            // 2026-06-20: mount the SD card BEFORE any radio init. Actively starting WiFi
            // (PHY/modem power-up) before the SDMMC mount disrupts SD card init on this
            // watch (ESP_ERR_TIMEOUT) - the bare-ESP-IDF test mounts fine with the radio
            // LINKED but not STARTED, and nf fails only once StartWifi has run. Mount SD
            // first (rails are already up from EnablePowerRails), then bring up radios.
            StartSdCard();
            StartRtc();
            StartTouchProbe();
            StartBootButton();
            StartWifi();
            // BLE stripped - see using comment above.
            // StartDisplay must run BEFORE BLE - the graphics heap allocates the
            // LARGEST free PSRAM block at init time. NimBLE consumes hundreds of KB
            // when it starts; if BLE wins the race for PSRAM the graphics heap gets
            // whatever scraps remain (~100KB observed) and FullScreen Bitmap OOMs.
            // Order: power -> touch -> display (claims PSRAM) -> BLE -> watchface.
            Bitmap fb = StartDisplay();
            StartBleAdvertising();

            if (fb != null)
            {
                _fb = fb;
                // Start the HTTP server now that we have a framebuffer to serve from.
                // Will be a no-op if WiFi failed to connect.
                if (_wifi != null && _wifi.IsConnected)
                {
                    _http = new HttpServer(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, port: 8080);
                    if (_sd != null) _http.AttachSdCard(_sd);
                    try
                    {
                        _http.Start();
                        Debug.WriteLine("[SpawnWear] HTTP at http://" + _wifi.IpAddress + ":8080/");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[SpawnWear] HTTP start failed: " + ex.Message);
                        _http = null;
                    }
                }
                var statusBar = new StatusBar(fb, BoardPins.LcdWidth, _axp, _rtc);
                // WiFi state -> status bar. We don't have RSSI on this build so
                // signal strength is reported as full bars (4) when connected
                // and -1 (hidden) when not. Phase 2 will read RSSI from the
                // adapter and map it to 1-4 bars.
                statusBar.SetWifiBars(_wifi != null && _wifi.IsConnected ? 4 : -1);
                statusBar.SetBleAdvertising(true);

                // Service host - the single point through which screens consume
                // system services via the AppContracts interfaces. Phase 8
                // SD-card-loadable apps will receive this same instance.
                var services = new ServiceHost(_axp, _rtc, _wifi);

                var watchface = new Watchface(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, _axp, _rtc);
                var about = new AboutScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, services);
                var wifiScreen = new WifiScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, services);
                var stats = new StatsScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, _axp);
                var settings = new SettingsScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, ForceSleepFromUi);
                var loadedApp = new LoadedAppScreen(services, fb, BoardPins.LcdWidth, BoardPins.LcdHeight);
                services.AttachDisplay(fb, BoardPins.LcdWidth, BoardPins.LcdHeight);

                // Launcher tiles map directly to the per-app screen indices in the
                // navigator below. Phase 2.5 will let SD-card-loaded apps register
                // their own tiles dynamically; for now the three built-in apps are
                // hard-wired.
                var launcherTiles = new LauncherScreen.Tile[]
                {
                    // Row 1: built-in core surfaces.
                    new LauncherScreen.Tile { Label = "CLOCK",    TargetScreenIndex = 1, Icon = LauncherScreen.IconKind.Clock,    Background = Color.FromArgb(40, 40, 80) },
                    new LauncherScreen.Tile { Label = "STATS",    TargetScreenIndex = 2, Icon = LauncherScreen.IconKind.Stats,    Background = Color.FromArgb(20, 60, 40), BadgeCount = 3 },
                    new LauncherScreen.Tile { Label = "SETTINGS", TargetScreenIndex = 3, Icon = LauncherScreen.IconKind.Settings, Background = Color.FromArgb(60, 40, 20), BadgeCount = 1 },
                    // Row 2: app surfaces.
                    new LauncherScreen.Tile { Label = "ABOUT",    TargetScreenIndex = 4, Icon = LauncherScreen.IconKind.Settings, Background = Color.FromArgb(50, 30, 60) },
                    new LauncherScreen.Tile { Label = "WIFI",     TargetScreenIndex = 5, Icon = LauncherScreen.IconKind.Wifi,     Background = Color.FromArgb(20, 60, 90) },
                    new LauncherScreen.Tile { Label = "APP",      TargetScreenIndex = 6, Icon = LauncherScreen.IconKind.Empty,    Background = Color.FromArgb(60, 30, 70) },
                    // Row 3: planned apps.
                    new LauncherScreen.Tile { Label = "MUSIC",    TargetScreenIndex = -1, Icon = LauncherScreen.IconKind.Music },
                    new LauncherScreen.Tile { Label = "VIDEO",    TargetScreenIndex = -1, Icon = LauncherScreen.IconKind.Music },
                    new LauncherScreen.Tile { Label = "GALLERY",  TargetScreenIndex = -1, Icon = LauncherScreen.IconKind.Gallery },
                };
                var launcher = new LauncherScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, launcherTiles,
                    targetIndex => { _nav.GoTo(targetIndex); });

                _nav = new ScreenNavigator(new IScreen[] { launcher, watchface, stats, settings, about, wifiScreen, loadedApp });
                _http?.AttachAppLoader(loadedApp);
                // Wire page-dot indices + the shared status bar into each screen.
                launcher.SetPageDots(0, 7);
                watchface.SetPageDots(1, 7);
                stats.SetPageDots(2, 7);
                settings.SetPageDots(3, 7);
                about.SetPageDots(4, 7);
                wifiScreen.SetPageDots(5, 7);
                loadedApp.SetPageDots(6, 7);
                launcher.SetStatusBar(statusBar);
                watchface.SetStatusBar(statusBar);
                stats.SetStatusBar(statusBar);
                settings.SetStatusBar(statusBar);
                about.SetStatusBar(statusBar);
                wifiScreen.SetStatusBar(statusBar);
                loadedApp.SetStatusBar(statusBar);
                // Seed last-touch with boot time so the idle countdown to Dim / Sleep
                // starts NOW. Without this, the first OnTick computes idle as
                // "nowTicks since DateTime epoch" (huge), and the state machine snaps
                // straight to Sleep on the first iteration.
                _lastTouchUtcTicks = DateTime.UtcNow.Ticks;
                // Paint the active (boot) screen once before the event loop starts.
                try { _nav.Current.OnResume(); }
                catch (Exception ex) { Debug.WriteLine("[Boot] initial OnResume EX " + ex.Message); }
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
        /// Called by EventLoop on every wake. Drives the Active / Dim / Sleep state machine
        /// based on time-since-last-touch, repaints the watch face when visible, and
        /// returns the desired next-tick timeout. Tick budget:
        ///   * Finger held       = 16 ms   (smooth 60 Hz - matches Rust port main.rs:612)
        ///   * Active watchface  = 1000 ms (only seconds digit changes per tick)
        ///   * Dim watchface     = 1000 ms (still ticking; just dimmer)
        ///   * Asleep            = 30000 ms (housekeeping only - touch INT wakes early)
        ///
        /// Power model:
        ///   * Active:  AMOLED black bg = ~0 mA per off pixel + partial flush ~25 KB/s
        ///   * Dim:     same as Active but brightness drops to 0x40 (~1/4 of full)
        ///   * Asleep:  CO5300 SLPIN + DISPOFF -> panel ~uA, no flushes, CPU
        ///              tickless-idle for the full 30 s
        /// </summary>
        static int OnTick(EventLoop.WakeReason reason)
        {
            // BOOT-button screenshot capture lived here - removed in favor of the
            // HTTP server's /screenshot.bin endpoint. The boot-button click pending
            // flag is harmless if set; just drained without action.
            if (_bootButtonClickPending > 0)
            {
                _bootButtonClickPending = 0;
            }

            try
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                long idleSeconds = (nowTicks - _lastTouchUtcTicks) / TimeSpan.TicksPerSecond;

                ScreenState desired;
                if (_fingerDown || idleSeconds < DimAfterSeconds) desired = ScreenState.Active;
                else if (idleSeconds < SleepAfterSeconds) desired = ScreenState.Dim;
                else desired = ScreenState.Sleep;

                if (desired != _screenState)
                {
                    TransitionTo(desired);
                }

                if (_screenState != ScreenState.Sleep)
                {
                    _nav.Current.Tick();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tick] EX " + ex.GetType().Name + ": " + ex.Message);
            }

            if (_fingerDown) return 16;
            switch (_screenState)
            {
                case ScreenState.Sleep: return 30000;
                default: return 1000;
            }
        }

        /// <summary>
        /// Settings-screen "SLEEP" row callback - rewinds the idle clock so the
        /// next OnTick state-machine pass transitions to ScreenState.Sleep, same
        /// path the BOOT button uses.
        /// </summary>
        static void ForceSleepFromUi()
        {
            _lastTouchUtcTicks = DateTime.UtcNow.Ticks - (SleepAfterSeconds + 1) * TimeSpan.TicksPerSecond;
            _fingerDown = false;
            if (_eventLoop != null) _eventLoop.Wake();
        }

        static void TransitionTo(ScreenState desired)
        {
            ScreenState prev = _screenState;
            Debug.WriteLine("[Screen] " + prev + " -> " + desired);
            _screenState = desired;

            switch (desired)
            {
                case ScreenState.Active:
                    if (prev == ScreenState.Sleep)
                    {
                        DisplayControl.Wake();
                        _nav.Current.Invalidate();
                    }
                    DisplayControl.SetBrightness(BrightnessActive);
                    break;
                case ScreenState.Dim:
                    DisplayControl.SetBrightness(BrightnessDim);
                    break;
                case ScreenState.Sleep:
                    DisplayControl.Sleep();
                    break;
            }
        }

        static void StartSdCard()
        {
            // One-shot diagnostic: list every drive the runtime knows about
            // BEFORE we try to mount the SD card. If the SD slot has been
            // auto-mounted by the runtime image, it'll show up here. Total
            // size distinguishes SD (~1GB) vs an internal flash partition
            // (typically a few MB).
            try
            {
                var pre = System.IO.DriveInfo.GetDrives();
                Debug.WriteLine("[SD] pre-mount drives: " + pre.Length);
                foreach (var d in pre)
                {
                    long total = -1;
                    try { total = d.TotalSize; } catch { }
                    Debug.WriteLine("[Drive] " + d.Name + " type=" + d.DriveType + " size=" + total);

                    // List the drive's root - if it's the SD card auto-mounted
                    // by the runtime, we'll see TJ's existing files. If it's an
                    // internal flash partition we'll only see what SpawnWear
                    // wrote (spawnwear-pair.bin from PairingService).
                    try
                    {
                        var dirs = System.IO.Directory.GetDirectories(d.Name);
                        var files = System.IO.Directory.GetFiles(d.Name);
                        Debug.WriteLine("[Drive]   " + d.Name + " has " + dirs.Length + " dirs + " + files.Length + " files");
                        foreach (var f in files) Debug.WriteLine("[Drive]   FILE " + f);
                        foreach (var dd in dirs) Debug.WriteLine("[Drive]   DIR  " + dd);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[Drive]   " + d.Name + " enum EX: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SD] pre-mount enum EX: " + ex.Message);
            }

            try
            {
                Debug.WriteLine("[SD] mounting...");
                _sd = new SdCardService();
                if (_sd.Initialize())
                {
                    Debug.WriteLine("[SD] mounted at " + _sd.MountPath);
                    // Probe: list /D:\ root if accessible
                    try
                    {
                        var dirs = System.IO.Directory.GetDirectories(_sd.MountPath);
                        var files = System.IO.Directory.GetFiles(_sd.MountPath);
                        Debug.WriteLine("[SD] root has " + dirs.Length + " dirs + " + files.Length + " files");
                        foreach (var d in dirs) Debug.WriteLine("[SD] DIR  " + d);
                        foreach (var f in files) Debug.WriteLine("[SD] FILE " + f);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.WriteLine("[SD] enumerate EX: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SD] init EX: " + ex.GetType().Name + ": " + ex.Message);
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

                Debug.WriteLine("[SpawnWear] BLE-4 - Constructing helper services");
                var debugSvc = new DebugConsoleService();
                var profile = new WatchProfileService();
                var pairing = new PairingService(debugSvc);
                var wifi = new WifiConfigService(debugSvc, profile, pairing);
                Debug.WriteLine("[SpawnWear] BLE-5 - Helper services constructed");

                Debug.WriteLine("[SpawnWear] BLE-6 - Calling wifi.Initialize()");
                if (!wifi.Initialize())
                {
                    Debug.WriteLine("[SpawnWear] BLE-7-fail - wifi.Initialize returned false");
                    return;
                }
                Debug.WriteLine("[SpawnWear] BLE-7 - wifi.Initialize OK");

                var serviceDataWriter = new DataWriter();
                serviceDataWriter.WriteByte(0x01);

                Debug.WriteLine("[SpawnWear] BLE-8 - Calling StartAdvertising");
                wifi.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsConnectable = true,
                    IsDiscoverable = true,
                    ServiceData = serviceDataWriter.DetachBuffer()
                });
                Debug.WriteLine("[SpawnWear] BLE-9 - Advertising as '" + name + "'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SpawnWear] BLE-EX " + ex.GetType().Name + ": " + ex.Message);
                Debug.WriteLine("[SpawnWear] BLE-EX stack: " + ex.StackTrace);
            }
        }

        static void EnablePowerRails()
        {
            try
            {
                Debug.WriteLine("[Power] P1 - Opening AXP2101 I2C device @ 0x" + BoardPins.AxpI2cAddress.ToString("X2"));
                var axpI2c = BoardSetup.OpenI2cDevice(BoardPins.AxpI2cAddress);
                _axp = new Axp2101Driver(axpI2c);
                Debug.WriteLine("[Power] P2 - Rail enable (DC1 + ALDO1+2+3 for the AMOLED panel)");
                _axp.EnableDisplayRails();
                Debug.WriteLine("[Power] P2b - AXP LDO 0x90 readback = 0x" + _axp.ReadReg(0x90).ToString("X2") + " (expect 0x07)");
                Debug.WriteLine("[Power] P3 - Enabling ADC channels");
                _axp.EnableAdc();
                int batPct = _axp.ReadBatteryPercent();
                int batMv = _axp.ReadBatteryMillivolts();
                Debug.WriteLine("[Power] P4 - bat=" + batPct + "% " + batMv + "mV vbus=" + (_axp.IsVbusPresent() ? "in" : "out"));
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
                    long nowTicks = DateTime.UtcNow.Ticks;
                    int snapX = snapshot.X1;
                    int snapY = snapshot.Y1;

                    if (_fingerDown)
                    {
                        _fingerLastX = snapX;
                        _fingerLastY = snapY;
                        _lastTouchUtcTicks = nowTicks;
                        if (!wasDown)
                        {
                            _fingerDownUtcTicks = nowTicks;
                            _fingerDownX = snapshot.X1;
                            _fingerDownY = snapshot.Y1;
                            _stateAtFingerDown = _screenState;
                            Debug.WriteLine("[Touch] DOWN at (" + snapshot.X1 + "," + snapshot.Y1 + ") state=" + _stateAtFingerDown);
                        }
                    }
                    else if (wasDown)
                    {
                        // Finger lifted. Classify as tap, long-press, or drag.
                        long elapsedMs = (nowTicks - _fingerDownUtcTicks) / TimeSpan.TicksPerMillisecond;
                        int dx = _fingerLastX - _fingerDownX;
                        int dy = _fingerLastY - _fingerDownY;
                        bool stayedPut = (dx * dx + dy * dy) < TapMaxMoveSquared;
                        bool isTap = elapsedMs < TapMaxMs && stayedPut;
                        bool isLongPress = elapsedMs >= LongPressMinMs && stayedPut;
                        Debug.WriteLine("[Touch] UP elapsed=" + elapsedMs + "ms dxdy=(" + dx + "," + dy + ") tap=" + isTap + " long=" + isLongPress);
                        // Wake-tap consumption: any gesture whose finger-DOWN happened while
                        // the screen was asleep is consumed by the wake itself, not dispatched
                        // to the UI.
                        if (_nav != null && _stateAtFingerDown == ScreenState.Active)
                        {
                            if (isLongPress) _nav.GoHome();
                            else if (isTap) _nav.HandleTap(_fingerLastX, _fingerLastY);
                        }
                    }

                    // Wake the main loop so it picks up the new finger state and applies
                    // the appropriate tick budget (16 ms while held, 1 s when idle).
                    if (_eventLoop != null) _eventLoop.Wake();
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

        static void StartWifi()
        {
            try
            {
                Debug.WriteLine("[WiFi] Starting...");
                _wifi = new WifiService();
                bool ok = _wifi.Connect(timeoutMs: 20000);
                Debug.WriteLine("[WiFi] " + (ok ? "connected ip=" + _wifi.IpAddress : "FAILED"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WiFi] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void StartRtc()
        {
            try
            {
                Debug.WriteLine("[Rtc] R1 - Opening PCF85063 I2C device @ 0x" + BoardPins.RtcI2cAddress.ToString("X2"));
                var rtcI2c = BoardSetup.OpenI2cDevice(BoardPins.RtcI2cAddress);
                _rtc = new Pcf85063Driver(rtcI2c);
                _rtc.Initialize();
                bool valid = _rtc.TryRead(out var t);
                Debug.WriteLine("[Rtc] R2 - " + (valid ? "valid" : "OS-flag-set") +
                    " " + t.Year + "-" + t.Month + "-" + t.Day +
                    " " + t.Hour.ToString("D2") + ":" + t.Minute.ToString("D2") + ":" + t.Second.ToString("D2"));

                // Seed a default time when the chip reports oscillator-stopped (no
                // coin-cell battery installed, or first power-on). Picks the build
                // date as a reasonable starting point - any sync from BLE/NTP later
                // can override.
                if (!valid)
                {
                    var seed = new Pcf85063Driver.RtcTime
                    {
                        Year = 2026, Month = 5, Day = 3,
                        Hour = 12, Minute = 0, Second = 0, Weekday = 0
                    };
                    _rtc.Set(seed);
                    Debug.WriteLine("[Rtc] R3 - Seeded default 2026-05-03 12:00:00");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Rtc] EX " + ex.GetType().Name + ": " + ex.Message);
                _rtc = null;
            }
        }

        /// <summary>
        /// Wires the BOOT button on GPIO0 as an event source for the main loop. Phase 1
        /// roadmap item from the README. The button is pulled-up internally (active LOW),
        /// so a press triggers a falling edge.
        ///
        /// V2 dispatch (dev-mode): a single press triggers a screenshot capture - the
        /// main loop drains a pending-flag and emits the framebuffer thumbnail as
        /// base64 chunks over Debug.WriteLine that the host-side
        /// `tools/nf-screenshot.cs` reassembles into a PNG. Force-sleep moves to the
        /// SETTINGS app's "SLEEP" row.
        /// </summary>
        static void StartBootButton()
        {
            try
            {
                Debug.WriteLine("[Boot] B1 - Opening GPIO" + BoardPins.BootButton);
                var pin = BoardSetup.GpioController.OpenPin(BoardPins.BootButton);
                pin.SetPinMode(System.Device.Gpio.PinMode.InputPullUp);
                pin.ValueChanged += (sender, args) =>
                {
                    if (args.ChangeType != System.Device.Gpio.PinEventTypes.Falling) return;
                    Debug.WriteLine("[Boot] PRESS - queue screenshot");
                    _bootButtonClickPending = 1;
                    if (_eventLoop != null) _eventLoop.Wake();
                };
                Debug.WriteLine("[Boot] B2 - Falling-edge handler attached");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Boot] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // BLE startup stripped 2026-05-04. Restore once the firmware deploy-commit
        // memory budget is lifted; the source above is preserved in git history
        // (commit 767015a and earlier). The watch is HTTP-only for now.
    }
}
