using System;
using System.Diagnostics;
using System.Drawing;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.UI;
using nanoFramework.UI.GraphicDrivers;
using SpawnWear.Drivers;
using SpawnWear.Drivers.Imu;
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
        // Pairing service (built during BLE setup) + the Companion sub-page, created
        // lazily the first time the user opens Settings > Companion (by then the
        // pairing service exists). See OpenCompanionPage.
        static PairingService _pairing;
        static CompanionScreen _companionScreen;
        static UiKitDemoScreen _uiDemoScreen;
        static Axp2101Driver _axp;
        static Pcf85063Driver _rtc;
        static WifiService _wifi;
        static SdCardService _sd;
        static AppRepositoryService _appRepo;
        static LoadedAppScreen _loadedApp;
        // loadedApp is the last entry in the navigator's screens array.
        const int AppSlotIndex = 6;
        static Qmi8658Driver _imu;
        static LoggerService _logger;
        static WifiConfigService _bleConfig;   // BLE provider handle, so Settings can toggle advertising
        static bool _bleAdvertising = true;
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
            Debug.WriteLine("[SpawnWear] M0 - Main reached (AppContracts assembly split)");

            // Phase 3 Logger system service - created first so every subsystem can log
            // through it; its BLE sink is wired once the debug-log characteristic exists.
            _logger = new LoggerService();

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
            StartImu();
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

                // Bug A (narrow size-specific deploy reset) workaround: REFERENCE _DeployPad so the
                // linker keeps it, shifting the total deploy size off the failing band to a known-
                // good ~361 KB. Remove this line + _DeployPad.cs once Bug A is root-caused.
                if (DeployPad.Len() < 0) { _fb = null; }

                // Native Ed25519/X25519 (Monocypher) interop boot self-test with ON-SCREEN
                // visual bisection of the boot hang first seen 2026-06-21. Runs AFTER the
                // display is live (no boot console exists) so each native call flashes a
                // distinct full-screen color; a frozen panel color names the hanging call.
                // Remove once the crypto boot hang is root-caused.
                CryptoSelfTest(fb);

                // Native libpeer WebRTC interop boot smoke test (Phase 7b milestone 2):
                // construct a PeerConnection + data channel and generate an offer on-device.
                // NAVY->GREEN = the watch produced a WebRTC offer SDP. Dev diagnostic; remove
                // before ship. (Runs early; ICE uses host candidates if WiFi isn't up yet.)
                WebRtcSelfTest(fb);

                // Phase 7b milestone 3: NO automatic WebRTC connect at boot (libpeer's blocking
                // DTLS recv destabilizes the watch once it reaches DTLS). The offer SDP is instead
                // generated ON DEMAND via GET /webrtc-offer (Create->offer->Close only - never
                // reaches DTLS, so it's the proven-safe path) for off-watch ICE-candidate diagnosis.

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
                var services = new ServiceHost(_axp, _rtc, _wifi, _logger);

                var watchface = new Watchface(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, _axp, _rtc);
                var about = new AboutScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, services);
                var wifiScreen = new WifiScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, services);
                var stats = new StatsScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, _axp);
                var settings = new SettingsScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight, ForceSleepFromUi, _imu,
                    ToggleBleFromUi, _bleAdvertising, ToggleWifiFromUi, _wifi != null && _wifi.IsConnected, OpenCompanionPage,
                    OpenUiKitPage);
                var loadedApp = new LoadedAppScreen(services, fb, BoardPins.LcdWidth, BoardPins.LcdHeight);
                _loadedApp = loadedApp;
                services.AttachDisplay(fb, BoardPins.LcdWidth, BoardPins.LcdHeight);

                // SD-backed app library (D:\apps). Apps installed via the Companion
                // app-manager (/apps/install) persist here and survive a reboot;
                // /apps/launch reads them back. Not-ready (no SD) just means "no
                // installed apps" - the watch's built-in screens are unaffected.
                _appRepo = new AppRepositoryService(_sd);
                _appRepo.Initialize();

                // The launcher renders built-in system tiles PLUS a live tile per
                // installed app (BuildLauncherTiles), refreshed every time it comes
                // to the foreground - so installing an app from the Companion makes
                // it appear on the watch home screen with no reboot. Tapping a tile
                // either navigates (built-in) or loads + launches the app
                // (ActivateLauncherTile).
                var launcher = new LauncherScreen(fb, BoardPins.LcdWidth, BoardPins.LcdHeight,
                    BuildLauncherTiles, ActivateLauncherTile);

                _nav = new ScreenNavigator(new IScreen[] { launcher, watchface, stats, settings, about, wifiScreen, loadedApp });
                // Full app-manager wiring: loaded-app slot + navigator + slot index
                // (so /apps/launch can switch to the app) + the SD app library.
                // Also gives the navigator to /touch so the Mirror remote works.
                _http?.AttachAppLoader(loadedApp, _nav, AppSlotIndex, _appRepo);
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
                // Re-activate the app the user last launched (persisted on the SD
                // card across reboots). We load it into the slot but DON'T navigate
                // to it - the user still boots to the watchface; the app is waiting
                // on its launcher tile / navigator slot.
                if (_appRepo != null && _appRepo.IsReady)
                {
                    string lastApp = _appRepo.LastApp;
                    if (lastApp != null)
                    {
                        byte[] peBytes = _appRepo.Read(lastApp);
                        if (peBytes != null)
                        {
                            string status;
                            bool ok = loadedApp.LoadPe(peBytes, out status);
                            Debug.WriteLine("[Boot] reload last app '" + lastApp + "': " + status);
                        }
                        else
                        {
                            Debug.WriteLine("[Boot] last app '" + lastApp + "' no longer on card");
                        }
                    }
                }

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

        /// <summary>Builds the launcher tile set: built-in system shortcuts plus
        /// one tile per app installed in the SD library. The launcher calls this
        /// at boot and on every resume, so installs / uninstalls appear live.</summary>
        static LauncherScreen.Tile[] BuildLauncherTiles()
        {
            var builtins = new LauncherScreen.Tile[]
            {
                new LauncherScreen.Tile { Label = "CLOCK",    TargetScreenIndex = 1, Icon = LauncherScreen.IconKind.Clock,    Background = Color.FromArgb(40, 40, 80) },
                new LauncherScreen.Tile { Label = "STATS",    TargetScreenIndex = 2, Icon = LauncherScreen.IconKind.Stats,    Background = Color.FromArgb(20, 60, 40) },
                new LauncherScreen.Tile { Label = "SETTINGS", TargetScreenIndex = 3, Icon = LauncherScreen.IconKind.Settings, Background = Color.FromArgb(60, 40, 20) },
                new LauncherScreen.Tile { Label = "WIFI",     TargetScreenIndex = 5, Icon = LauncherScreen.IconKind.Wifi,     Background = Color.FromArgb(20, 60, 90) },
            };

            AppInfo[] apps = _appRepo != null ? _appRepo.ListInfo() : new AppInfo[0];
            var tiles = new LauncherScreen.Tile[builtins.Length + apps.Length];
            for (int i = 0; i < builtins.Length; i++) tiles[i] = builtins[i];
            for (int i = 0; i < apps.Length; i++)
            {
                string name = apps[i].Name;
                tiles[builtins.Length + i] = new LauncherScreen.Tile
                {
                    Label = name,
                    AppName = name,                 // marks this as an app tile
                    TargetScreenIndex = -1,
                    Icon = LauncherScreen.IconKind.App,
                    Background = AppTileColor(name),
                };
            }
            return tiles;
        }

        /// <summary>Launcher tile tap handler: built-in tiles navigate; app tiles
        /// load + launch the installed app.</summary>
        static void ActivateLauncherTile(LauncherScreen.Tile tile)
        {
            if (tile.AppName != null) LaunchInstalledApp(tile.AppName);
            else if (tile.TargetScreenIndex >= 0 && _nav != null) _nav.GoTo(tile.TargetScreenIndex);
        }

        // Loads an installed app off the SD library into the app slot, records it
        // as the last app (so it re-activates next boot), and switches to it. A
        // load failure shows the actionable reason on the slot screen instead.
        static void LaunchInstalledApp(string name)
        {
            if (_appRepo == null || _loadedApp == null || _nav == null) return;
            byte[] bytes = _appRepo.Read(name);
            if (bytes == null)
            {
                _loadedApp.ShowMessage("App not found on SD card: " + name);
                _nav.GoTo(AppSlotIndex);
                return;
            }
            string status;
            bool ok = _loadedApp.LoadPe(bytes, out status);
            if (ok) _appRepo.LastApp = name;
            else _loadedApp.ShowMessage(status);
            _nav.GoTo(AppSlotIndex);
            Debug.WriteLine("[Launcher] launch '" + name + "': " + status);
        }

        // Stable dark tint derived from the app name, so each app tile has its
        // own colour without per-app config.
        static Color AppTileColor(string name)
        {
            int h = 17;
            for (int i = 0; i < name.Length; i++) h = (h * 31 + name[i]) & 0x7FFFFFFF;
            int r = 25 + (h % 45);
            int g = 25 + ((h / 45) % 45);
            int b = 35 + ((h / 2025) % 45);
            return Color.FromArgb(r, g, b);
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

        // Settings -> Companion: push the pairing sub-page onto the navigator. Built
        // lazily on first open (by then the pairing service + framebuffer exist).
        static void OpenCompanionPage()
        {
            if (_nav == null || _fb == null || _pairing == null)
            {
                Debug.WriteLine("[Companion] not ready (nav/fb/pairing null)");
                return;
            }
            if (_companionScreen == null)
            {
                _companionScreen = new CompanionScreen(_fb, BoardPins.LcdWidth, BoardPins.LcdHeight, _pairing);
            }
            _nav.Push(_companionScreen);
        }

        // Settings -> UI KIT: push the UI-library demo (proves the GameUI-mirrored
        // widget tree renders on the watch via IUiSurface/WatchSurface).
        static void OpenUiKitPage()
        {
            if (_nav == null || _fb == null) return;
            if (_uiDemoScreen == null)
            {
                _uiDemoScreen = new UiKitDemoScreen(_fb, BoardPins.LcdWidth, BoardPins.LcdHeight);
            }
            _nav.Push(_uiDemoScreen);
        }

        // Settings BLE toggle: start/stop GATT advertising. Returns the resulting state.
        static bool ToggleBleFromUi(bool desiredOn)
        {
            try
            {
                if (_bleConfig != null && _bleConfig.ServiceProvider != null)
                {
                    if (desiredOn)
                    {
                        var w = new DataWriter();
                        w.WriteByte(0x01);
                        _bleConfig.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                        {
                            IsConnectable = true,
                            IsDiscoverable = true,
                            ServiceData = w.DetachBuffer()
                        });
                    }
                    else
                    {
                        _bleConfig.ServiceProvider.StopAdvertising();
                    }
                    _bleAdvertising = desiredOn;
                    if (_logger != null) _logger.Info("[Settings] BLE advertising " + (desiredOn ? "ON" : "OFF"));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Settings] BLE toggle EX " + ex.GetType().Name + ": " + ex.Message);
            }
            return _bleAdvertising;
        }

        // Settings WiFi toggle: connect/disconnect the station + start/stop the HTTP server.
        // The HTTP server binds IPAddress.Any so a clean Stop/Start rides over the IP change.
        // Returns the resulting connected state.
        static bool ToggleWifiFromUi(bool desiredOn)
        {
            try
            {
                if (desiredOn)
                {
                    // Reconnect (not Connect/ConnectDhcp - that one-shot fails on a 2nd call).
                    bool ok = _wifi != null && _wifi.Reconnect();
                    if (ok) { try { _http?.Start(); } catch { } }
                    if (_logger != null) _logger.Info("[Settings] WiFi ON connected=" + ok);
                }
                else
                {
                    try { _http?.Stop(); } catch { }
                    if (_wifi != null) _wifi.Disconnect();
                    if (_logger != null) _logger.Info("[Settings] WiFi OFF");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Settings] WiFi toggle EX " + ex.GetType().Name + ": " + ex.Message);
            }
            return _wifi != null && _wifi.IsConnected;
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
                // Route Logger output to the BLE debug-log channel (notify-only, so it
                // does not double-print to the wire console which the Logger already does).
                if (_logger != null) _logger.Sink = debugSvc.Notify;
                var profile = new WatchProfileService();
                var pairing = new PairingService(debugSvc);
                _pairing = pairing; // expose to the Companion sub-page (OpenCompanionPage)
                var wifi = new WifiConfigService(debugSvc, profile, pairing);
                _bleConfig = wifi; // handle for the Settings BLE-advertising toggle
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

        // Full-screen color flush used as a boot-time progress marker (no console exists).
        static void CryptoMark(Bitmap fb, Color c)
        {
            fb.FillRectangle(0, 0, BoardPins.LcdWidth, BoardPins.LcdHeight, c);
            fb.Flush();
        }

        // Native crypto boot self-test with ON-SCREEN visual bisection. Each native
        // Monocypher call is preceded by a full-screen color flush. If a native call
        // HANGS, the last color left on the panel names the offending call:
        //   RED     -> X25519.GeneratePrivateKey  (pure esp_fill_random / HW RNG, in isolation)
        //   ORANGE  -> X25519.GetPublicKey        (crypto_x25519_public_key, field math)
        //   YELLOW  -> X25519.SharedSecret        (crypto_x25519 ECDH)
        //   CYAN    -> Ed25519.GenerateKeyPair    (esp_fill_random + crypto_ed25519_key_pair / SHA-512)
        //   BLUE    -> Ed25519.Sign               (SHA-512)
        //   MAGENTA -> Ed25519.Verify             (SHA-512) + tamper rejection
        // The cheapest + most-suspect primitive (the HW RNG) runs FIRST and ALONE, so a
        // "RED forever" pins the hang on esp_fill_random vs the heavier SHA-512 paths.
        // Full success flashes GREEN (held ~1s) then boot continues; a computed-result
        // mismatch (not a hang) flashes WHITE; a managed throw flashes dim GRAY.
        static void CryptoSelfTest(Bitmap fb)
        {
            try
            {
                // 1) Pure hardware RNG in isolation (esp_fill_random, nothing else).
                CryptoMark(fb, Color.FromArgb(255, 0, 0));        // RED
                byte[] aPriv = new byte[32], aPub = new byte[32];
                byte[] bPriv = new byte[32], bPub = new byte[32];
                SpawnDev.Crypto.X25519.GeneratePrivateKey(aPriv);

                // 2) Curve25519 base-point multiply (field arithmetic, no SHA, no RNG).
                CryptoMark(fb, Color.FromArgb(255, 128, 0));      // ORANGE
                SpawnDev.Crypto.X25519.GetPublicKey(aPub, aPriv);
                SpawnDev.Crypto.X25519.GeneratePrivateKey(bPriv);
                SpawnDev.Crypto.X25519.GetPublicKey(bPub, bPriv);

                // 3) X25519 ECDH agreement (both sides derive the same shared secret).
                CryptoMark(fb, Color.FromArgb(255, 255, 0));      // YELLOW
                byte[] sa = new byte[32], sb = new byte[32];
                SpawnDev.Crypto.X25519.SharedSecret(sa, aPriv, bPub);
                SpawnDev.Crypto.X25519.SharedSecret(sb, bPriv, aPub);
                bool agree = true;
                for (int i = 0; i < 32; i++) if (sa[i] != sb[i]) agree = false;

                // 4) Ed25519 keypair (esp_fill_random seed + crypto_ed25519_key_pair / SHA-512).
                CryptoMark(fb, Color.FromArgb(0, 220, 255));      // CYAN
                byte[] pub = new byte[32];
                byte[] priv = new byte[64];
                SpawnDev.Crypto.Ed25519.GenerateKeyPair(pub, priv);

                // 5) Ed25519 sign (SHA-512).
                CryptoMark(fb, Color.FromArgb(0, 0, 255));        // BLUE
                byte[] msg = new byte[] { 0x53, 0x70, 0x61, 0x77, 0x6E, 0x57, 0x65, 0x61, 0x72 }; // "SpawnWear"
                byte[] sig = new byte[64];
                SpawnDev.Crypto.Ed25519.Sign(sig, priv, msg);

                // 6) Ed25519 verify (SHA-512) + tamper rejection.
                CryptoMark(fb, Color.FromArgb(255, 0, 255));      // MAGENTA
                bool verified = SpawnDev.Crypto.Ed25519.Verify(sig, pub, msg);
                msg[0] ^= 0xFF; // tamper
                bool tamperRejected = !SpawnDev.Crypto.Ed25519.Verify(sig, pub, msg);

                // 7) RFC 8032 known-answer test (white marker): derive + sign the PUBLISHED
                // Ed25519 TEST 2 vector and require a byte-exact public key + signature, plus
                // acceptance on verify. Byte-exact RFC 8032 == guaranteed interop with the
                // Companion's WebCrypto / Ed25519Managed (also RFC 8032) - this is what makes
                // real BLE pairing work, proven without a device on hand.
                CryptoMark(fb, Color.FromArgb(255, 255, 255));    // WHITE = running the KAT
                bool kat = KnownAnswerTestRfc8032();

                bool pass = agree && verified && tamperRejected && kat;
                if (pass)
                {
                    CryptoMark(fb, Color.FromArgb(0, 255, 0));    // GREEN = all good (incl. RFC 8032)
                    System.Threading.Thread.Sleep(1200);
                }
                else
                {
                    // A white STROBE (not a frozen marker color, not green) = the crypto ran
                    // but computed a WRONG result - an RFC 8032 / interop fault to chase, NOT
                    // a hang. Distinct on sight from both the rainbow and the green pass.
                    for (int b = 0; b < 5; b++)
                    {
                        CryptoMark(fb, Color.FromArgb(255, 255, 255));
                        System.Threading.Thread.Sleep(180);
                        CryptoMark(fb, Color.FromArgb(0, 0, 0));
                        System.Threading.Thread.Sleep(180);
                    }
                }
            }
            catch (Exception)
            {
                // A managed throw (e.g. CLR_E_INVALID_PARAMETER) is NOT the boot hang we
                // are hunting; dim gray distinguishes it from a frozen marker color.
                CryptoMark(fb, Color.FromArgb(40, 40, 40));
                System.Threading.Thread.Sleep(1000);
            }
        }

        // RFC 8032 Section 7.1 Ed25519 TEST 2 (1-byte message 0x72). A byte-exact match of
        // the seed-derived public key + the signature (and acceptance on verify) proves the
        // watch's Monocypher Ed25519 is standard RFC 8032 - hence interoperable with the
        // Companion's WebCrypto / Ed25519Managed. Vectors are the published constants.
        static readonly byte[] KatSeed = new byte[] {
            0x4c,0xcd,0x08,0x9b,0x28,0xff,0x96,0xda,0x9d,0xb6,0xc3,0x46,0xec,0x11,0x4e,0x0f,
            0x5b,0x8a,0x31,0x9f,0x35,0xab,0xa6,0x24,0xda,0x8c,0xf6,0xed,0x4f,0xb8,0xa6,0xfb };
        static readonly byte[] KatPub = new byte[] {
            0x3d,0x40,0x17,0xc3,0xe8,0x43,0x89,0x5a,0x92,0xb7,0x0a,0xa7,0x4d,0x1b,0x7e,0xbc,
            0x9c,0x98,0x2c,0xcf,0x2e,0xc4,0x96,0x8c,0xc0,0xcd,0x55,0xf1,0x2a,0xf4,0x66,0x0c };
        static readonly byte[] KatMsg = new byte[] { 0x72 };
        static readonly byte[] KatSig = new byte[] {
            0x92,0xa0,0x09,0xa9,0xf0,0xd4,0xca,0xb8,0x72,0x0e,0x82,0x0b,0x5f,0x64,0x25,0x40,
            0xa2,0xb2,0x7b,0x54,0x16,0x50,0x3f,0x8f,0xb3,0x76,0x22,0x23,0xeb,0xdb,0x69,0xda,
            0x08,0x5a,0xc1,0xe4,0x3e,0x15,0x99,0x6e,0x45,0x8f,0x36,0x13,0xd0,0xf1,0x1d,0x8c,
            0x38,0x7b,0x2e,0xae,0xb4,0x30,0x2a,0xee,0xb0,0x0d,0x29,0x16,0x12,0xbb,0x0c,0x00 };

        static bool KnownAnswerTestRfc8032()
        {
            byte[] pub = new byte[32];
            byte[] priv = new byte[64];
            SpawnDev.Crypto.Ed25519.KeyPairFromSeed(KatSeed, pub, priv);
            if (!BytesEqual(pub, KatPub)) return false;

            byte[] sig = new byte[64];
            SpawnDev.Crypto.Ed25519.Sign(sig, priv, KatMsg);
            if (!BytesEqual(sig, KatSig)) return false;

            // The published signature must verify against the published public key.
            return SpawnDev.Crypto.Ed25519.Verify(KatSig, KatPub, KatMsg);
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // On-screen smoke test of the native libpeer WebRTC interop (Phase 7b milestone 2).
        // Proves the watch can construct a PeerConnection, open a data channel, and GENERATE a
        // WebRTC offer (ICE-gathered via STUN over WiFi) - the whole managed->native->libpeer
        // path. NAVY = starting; GREEN = offer SDP produced; ORANGE = created but no SDP (ICE
        // gather failed - check WiFi/STUN); RED = Create failed; WHITE strobe = exception.
        // Dev diagnostic; remove before ship. Run AFTER WiFi is up (needs STUN reachability).
        static void WebRtcSelfTest(Bitmap fb)
        {
            try
            {
                CryptoMark(fb, Color.FromArgb(0, 0, 160));   // NAVY = WebRTC interop test start
                int h = SpawnDev.WebRTC.PeerConnection.Create();
                if (h < 0)
                {
                    CryptoMark(fb, Color.FromArgb(160, 0, 0)); // RED = Create failed
                    System.Threading.Thread.Sleep(1500);
                    return;
                }
                SpawnDev.WebRTC.PeerConnection.CreateDataChannel(h, "data");
                SpawnDev.WebRTC.PeerConnection.CreateOffer(h);

                int len = 0;
                for (int i = 0; i < 120 && len == 0; i++) // up to ~6s for ICE gathering
                {
                    System.Threading.Thread.Sleep(50);
                    len = SpawnDev.WebRTC.PeerConnection.GetLocalSdpLength(h);
                }

                CryptoMark(fb, len > 0 ? Color.FromArgb(0, 255, 0)        // GREEN = offer SDP generated
                                       : Color.FromArgb(255, 128, 0));    // ORANGE = no SDP (ICE gather failed)
                System.Threading.Thread.Sleep(1800);
                SpawnDev.WebRTC.PeerConnection.Close(h);
            }
            catch (Exception)
            {
                for (int b = 0; b < 4; b++)
                {
                    CryptoMark(fb, Color.FromArgb(255, 255, 255));
                    System.Threading.Thread.Sleep(160);
                    CryptoMark(fb, Color.FromArgb(0, 0, 0));
                    System.Threading.Thread.Sleep(160);
                }
            }
        }

        // On-demand libpeer offer generator for HTTP diagnosis (Phase 7b milestone 3):
        // Create -> data channel -> offer -> read SDP -> Close. Never sets a remote description,
        // so it never reaches libpeer's blocking DTLS recv - this is the proven-safe path
        // (same as the boot WebRtcSelfTest). Returns the offer SDP (with ICE candidates) so we
        // can inspect exactly what the watch advertises. Called from GET /webrtc-offer.
        public static string GenerateOfferSdp()
        {
            int h = -1;
            try
            {
                h = SpawnDev.WebRTC.PeerConnection.Create();
                if (h < 0) return "ERROR: Create failed";
                SpawnDev.WebRTC.PeerConnection.CreateDataChannel(h, "data");
                SpawnDev.WebRTC.PeerConnection.CreateOffer(h);
                int len = 0;
                for (int i = 0; i < 120 && len == 0; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    len = SpawnDev.WebRTC.PeerConnection.GetLocalSdpLength(h);
                }
                if (len == 0) return "ERROR: no offer SDP generated";
                byte[] sbuf = new byte[len];
                SpawnDev.WebRTC.PeerConnection.GetLocalSdp(h, sbuf);
                return new string(System.Text.Encoding.UTF8.GetChars(sbuf));
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
            finally
            {
                try { if (h >= 0) SpawnDev.WebRTC.PeerConnection.Close(h); } catch { }
            }
        }

        // HTTP-triggered full WebRTC connect attempt (Phase 7b milestone 3), on a background
        // thread so it never blocks the UI/HTTP. With the libpeer DTLS recv-timeout fix this no
        // longer freezes - it progresses or times out. Trigger: GET /webrtc-connect.
        // Read progress: GET /webrtc-status. Dev diagnostic.
        public static string ConnectStatus = "idle";

        public static void StartWebRtcConnect()
        {
            // Run SYNCHRONOUSLY on the caller (HTTP) thread. A nanoFramework `new Thread()`
            // crashed during the libpeer interop (likely thread-stack too small for the native
            // call chain); the HTTP thread runs the same offer path fine (/webrtc-offer).
            ConnectStatus = "starting";
            WebRtcConnectRun();
        }

        // Crash-survival + TIMELINE log: mirror each connect stage to SD (mounted at I:\) with an
        // elapsed-ms prefix so GET /webrtc-log shows WHERE the ~minutes go (ICE vs DTLS vs which
        // phase). nanoFramework File has no AppendAllText, so accumulate in RAM and rewrite the
        // (tiny) file each call - last write still survives a reboot for crash-stage diagnosis.
        static DateTime _connectStart;
        static string _logBuf = "";
        static void LogSdReset()
        {
            _connectStart = DateTime.UtcNow;
            _logBuf = "t=0 connect-start\n";
            try { System.IO.File.WriteAllText("I:\\webrtc.log", _logBuf); } catch { }
        }
        static void LogSd(string s)
        {
            int ms = (int)(DateTime.UtcNow - _connectStart).TotalMilliseconds;
            _logBuf += ms + "ms " + s + "\n";
            try { System.IO.File.WriteAllText("I:\\webrtc.log", _logBuf); } catch { }
        }

        public static string ReadWebRtcLog()
        {
            try { return System.IO.File.ReadAllText("I:\\webrtc.log"); }
            catch (Exception ex) { return "no log (" + ex.Message + ")"; }
        }

        // Phase 7c: embedded WebRtcSelfTestPairing keys for the milestone-3 WebRTC challenge test
        // (the watch hasn't BLE-paired with the Bridge.Desktop answerroom; production uses the real
        // PairingService identity instead). TestWatchSeed = the 32-byte Ed25519 seed extracted from
        // WebRtcSelfTestPairing.WatchPrivB64 (PKCS8 -> last 32 bytes); KeyPairFromSeed(seed) derives
        // WatchPub (starts 0x25). TestCompanionPub = WebRtcSelfTestPairing.CompanionPubB64 raw bytes.
        static readonly byte[] TestWatchSeed = new byte[] {
            0x44,0x3A,0x30,0x41,0x8F,0xDE,0x47,0x2C,0x68,0x38,0xB5,0x96,0x27,0xFD,0x69,0xA3,
            0x3F,0x7B,0xE0,0x20,0x1E,0xCB,0x35,0x48,0xD7,0xBD,0xF7,0x0D,0xB2,0x88,0x01,0xE2 };
        static readonly byte[] TestCompanionPub = new byte[] {
            0x1D,0x1B,0x98,0xD3,0x27,0x57,0xC4,0xBA,0x6F,0x2D,0xBF,0xAE,0xE3,0x6F,0xA2,0xA7,
            0x8B,0xC8,0x28,0xBC,0xAD,0xDE,0x13,0x7E,0xEF,0x8D,0x90,0xBD,0xEC,0x9D,0xC0,0x6A };

        static void WebRtcConnectRun()
        {
            SwTrackerSignaling tr = null;
            int h = -1;
            try
            {
                LogSdReset();
                h = SpawnDev.WebRTC.PeerConnection.Create();
                if (h < 0) { ConnectStatus = "create-failed"; return; }
                // Phase 7b: do NOT open the data channel here - libpeer's create_datachannel needs
                // SCTP connected (it's a no-op otherwise) so this sent no DCEP OPEN. The m=application
                // line in the offer comes from the peer-connection config, not this call. We open the
                // channel AFTER StateCompleted below (where SCTP is up), so the DCEP OPEN gets sent.
                SpawnDev.WebRTC.PeerConnection.CreateOffer(h);
                int len = 0;
                for (int i = 0; i < 120 && len == 0; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    len = SpawnDev.WebRTC.PeerConnection.GetLocalSdpLength(h);
                }
                if (len == 0) { ConnectStatus = "no-offer-sdp"; return; }
                byte[] sbuf = new byte[len];
                SpawnDev.WebRTC.PeerConnection.GetLocalSdp(h, sbuf);
                string offer = new string(System.Text.Encoding.UTF8.GetChars(sbuf));

                // Phase 7c: when paired, meet the Companion in the shared room from BLE pairing (the
                // 20-byte room key); otherwise use the fixed milestone-3 test room. Per-attempt-ish
                // offer-id avoids stale cached offers polluting the answerer.
                bool usePairing = _pairing != null && _pairing.IsPaired;
                byte[] room = usePairing
                    ? _pairing.PairedRoomKey
                    : System.Text.Encoding.UTF8.GetBytes("SWclean0623pmRoom01x");
                LogSd(usePairing ? "PAIRED - using real companion identity + room" : "UNPAIRED - using embedded test pairing");
                byte[] pid = System.Text.Encoding.UTF8.GetBytes("-SW0001-watchTESTpid");
                byte[] oid = System.Text.Encoding.UTF8.GetBytes("wOffer" + (DateTime.UtcNow.Ticks % 100000000));
                LogSd("offer-ready len=" + len);
                tr = new SwTrackerSignaling();
                if (!tr.Connect()) { ConnectStatus = "ws-connect-failed"; return; }
                tr.AnnounceOffer(room, pid, oid, offer);
                ConnectStatus = "announced";
                LogSd("announced");

                string answer = tr.WaitForAnswer(oid, 20000);
                if (answer == null) { ConnectStatus = "no-answer"; return; }
                ConnectStatus = "answered len=" + answer.Length;

                // Free the WebSocket + its TLS (mbedtls) BEFORE the DTLS handshake (also mbedtls)
                // to cut peak memory - a likely cause of the reboot during DTLS. We already have
                // the answer; the signaling socket isn't needed for this non-trickle connection.
                try { tr.Dispose(); } catch { }
                tr = null;
                LogSd("answered, ws-freed, about to SetRemoteDescription");

                SpawnDev.WebRTC.PeerConnection.SetRemoteDescription(h, answer, SpawnDev.WebRTC.PeerConnection.SdpTypeAnswer);
                LogSd("remote-set ok, dtls handshake starting (poll)");
                // Wait for StateCompleted (4 = DTLS + SCTP done, data channel OPEN). State 3
                // (Connected) is only ICE - don't stop there or we'd Close before DTLS finishes.
                bool connected = false;
                int lastSt = -999;
                for (int i = 0; i < 300; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    int st = SpawnDev.WebRTC.PeerConnection.GetState(h);
                    ConnectStatus = "state=" + st + " iter=" + i;
                    if (st != lastSt) { LogSd("state=" + st + " iter=" + i); lastSt = st; }
                    if (st == SpawnDev.WebRTC.PeerConnection.StateCompleted)
                    {
                        connected = true;
                        break;
                    }
                }
                if (connected)
                {
                    // Phase 7b: SCTP is connected now - open the DCEP data channel. This sends a
                    // DATA_CHANNEL_OPEN on our DTLS-server odd stream; SipSorcery (DTLS client) then
                    // fires ondatachannel + ACKs, establishing the channel. Give it a moment to land.
                    SpawnDev.WebRTC.PeerConnection.CreateDataChannel(h, "data");
                    LogSd("opened DCEP data channel (post-SCTP)");
                    System.Threading.Thread.Sleep(500);

                    // Phase 7c: Ed25519 MUTUAL CHALLENGE over the data channel - mirrors
                    // SpawnWear.Bridge.WebRtc.WebRtcChallenge. Frames are distinguished by length:
                    // 32 = a challenge nonce; 96 = [nonce:32][sig:64] response. Each peer sends its
                    // own nonce and answers the other's; each verifies the other's sig with the peer's
                    // Ed25519 pubkey. Real RFC 8032 crypto via SpawnDev.Crypto.Ed25519 (Monocypher).
                    // Identity for the challenge: real PairingService keys when paired, else test keys.
                    byte[] watchPriv64;
                    byte[] peerPub;
                    if (usePairing)
                    {
                        watchPriv64 = _pairing.SigningKey;   // 64-byte seed-derived Ed25519 signing key
                        peerPub = _pairing.PeerPubKey;        // paired companion's 32-byte pubkey
                        LogSd("challenge identity: REAL pairing (companion pub[0]=" + peerPub[0] + ")");
                    }
                    else
                    {
                        byte[] tpub = new byte[32];
                        watchPriv64 = new byte[64];
                        SpawnDev.Crypto.Ed25519.KeyPairFromSeed(TestWatchSeed, tpub, watchPriv64);
                        peerPub = TestCompanionPub;
                        LogSd("challenge identity: TEST pub[0]=" + tpub[0] + " (expect 37)");
                    }

                    byte[] ourNonce = new byte[32];
                    SpawnDev.Crypto.X25519.GeneratePrivateKey(ourNonce); // ESP32 HW RNG fills 32 bytes
                    SpawnDev.WebRTC.PeerConnection.Send(h, ourNonce, 32);
                    LogSd("sent our challenge nonce");

                    bool peerAnswered = false; // we signed + returned the companion's nonce
                    bool ourVerified = false;  // we verified the companion's response to our nonce
                    byte[] rx = new byte[256];
                    for (int i = 0; i < 40 && !(peerAnswered && ourVerified); i++)
                    {
                        System.Threading.Thread.Sleep(50);
                        int n = SpawnDev.WebRTC.PeerConnection.TryReceive(h, rx);
                        if (n == 32)
                        {
                            // Companion's challenge -> sign the nonce, return [nonce:32][sig:64].
                            byte[] dom = new byte[32];
                            Array.Copy(rx, 0, dom, 0, 32);
                            byte[] sig = new byte[64];
                            SpawnDev.Crypto.Ed25519.Sign(sig, watchPriv64, dom);
                            byte[] resp = new byte[96];
                            Array.Copy(dom, 0, resp, 0, 32);
                            Array.Copy(sig, 0, resp, 32, 64);
                            SpawnDev.WebRTC.PeerConnection.Send(h, resp, 96);
                            peerAnswered = true;
                            LogSd("answered companion challenge");
                        }
                        else if (n == 96)
                        {
                            // Companion's response to OUR nonce -> echoed nonce must match + sig verify.
                            bool nonceOk = true;
                            for (int k = 0; k < 32; k++) { if (rx[k] != ourNonce[k]) { nonceOk = false; break; } }
                            byte[] sig = new byte[64];
                            Array.Copy(rx, 32, sig, 0, 64);
                            if (nonceOk && SpawnDev.Crypto.Ed25519.Verify(sig, peerPub, ourNonce))
                            {
                                ourVerified = true;
                                LogSd("verified companion response");
                            }
                            else { LogSd("companion response BAD (nonceOk=" + nonceOk + ")"); }
                        }
                    }
                    ConnectStatus = (peerAnswered && ourVerified)
                        ? "VERIFIED - mutual Ed25519 challenge OK"
                        : "challenge incomplete (answered=" + peerAnswered + " verified=" + ourVerified + ")";
                    LogSd(ConnectStatus);
                }
                else
                {
                    ConnectStatus = "not-connected (" + ConnectStatus + ")";
                }
                LogSd("done: " + ConnectStatus);
            }
            catch (Exception ex)
            {
                ConnectStatus = "EX " + ex.Message;
            }
            finally
            {
                try { tr?.Dispose(); } catch { }
                try { if (h >= 0) SpawnDev.WebRTC.PeerConnection.Close(h); } catch { }
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
        /// Brings up the QMI8658 6-axis IMU on the shared I2C bus (0x6B): probe WHO_AM_I,
        /// configure accel (+/-8 g) + gyro (+/-1024 dps), and emit one sample through the
        /// Logger so the read path is verified at boot. Phase 3 sensor item.
        /// </summary>
        static void StartImu()
        {
            try
            {
                Debug.WriteLine("[Imu] I1 - Opening QMI8658 I2C device @ 0x" + BoardPins.ImuI2cAddress.ToString("X2"));
                var imuI2c = BoardSetup.OpenI2cDevice(BoardPins.ImuI2cAddress);
                _imu = new Qmi8658Driver(imuI2c);

                bool present = _imu.Probe();
                Debug.WriteLine("[Imu] I2 - WHO_AM_I present=" + present + " (expect device id 0x05)");
                if (!present)
                {
                    _imu = null;
                    return;
                }

                _imu.Initialize();
                System.Threading.Thread.Sleep(20); // let the first sample land at 500 Hz ODR

                if (_imu.TryRead(out var s))
                {
                    // Integer milli-units (mg / mdps) + deci-degC: avoids float-to-string
                    // formatting on the constrained runtime, and exercises the Logger.
                    if (_logger != null)
                    {
                        _logger.Info("[Imu] accel mg(" +
                            (int)(s.AccelX * 1000) + "," + (int)(s.AccelY * 1000) + "," + (int)(s.AccelZ * 1000) +
                            ") gyro mdps(" +
                            (int)(s.GyroX * 1000) + "," + (int)(s.GyroY * 1000) + "," + (int)(s.GyroZ * 1000) +
                            ") temp dC " + (int)(s.TempC * 10));
                    }
                }
                else
                {
                    Debug.WriteLine("[Imu] I3 - TryRead: no data ready yet");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Imu] EX " + ex.GetType().Name + ": " + ex.Message);
                _imu = null;
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
