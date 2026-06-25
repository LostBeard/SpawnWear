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
        static HttpServer _http;            // RETIRED - kept for potential future need; no longer started
        static WebRtcTransportService _webrtc;  // autonomous WebRTC transport (replaces the HTTP trigger)
        static StatusBar _statusBar;        // shared status bar; OnTick refreshes the Companion-link icon
        static Bitmap _fb; // shared framebuffer reference for screenshots
        // FREEZE ROOT-CAUSED 2026-06-23: unlocked shared I2C bus across 3 threads (main loop StatusBar
        // AXP/RTC, WebRTC thread IMU+battery telemetry, touch interrupt). Concurrent transactions wedged
        // the bus -> a native read blocked -> cooperative CLR froze. Was masked until WebRTC's thread
        // added the 5Hz IMU reads. Fixed by BoardSetup.I2cLock around every driver transaction. Re-enabled.
        const bool EnableWebRtcTransport = false;
        // 2026-06-25: AppRepo RE-ENABLED as the first single-variable freeze test after the watch recovered.
        // The AppRepositoryService.Initialize CreateDirectory try/catch fix is in, and the SD now mounts
        // clean (D:\ with 13 dirs + 8 files incl D:\apps). If the 26s freeze does NOT return with AppRepo on
        // and WebRTC still off, the app-repo SD reads are cleared and the freeze lives on the WebRTC side.
        const bool EnableAppRepo = true;

        static int _bootButtonClickPending; // 0=none, 1=short(Back), 2=long; set by ISR, drained by loop
        static long _bootDownUtcTicks;
        static ScreenState _bootStateAtPress; // screen state when the button went down (wake-vs-act gate)
        const int BootLongPressMs = 600;
        internal delegate void BootButtonAction();
        static BootButtonAction _bootLongPressAction; // programmable long-press (AI agent hold-to-talk later)
        static bool _fingerDown;
        static long _lastTouchUtcTicks;

        // Tap-gesture detection state. A "tap" = finger goes down, stays within
        // a small radius for under TapMaxMs, then lifts. Anything longer is a
        // long-press (Phase 2 dispatch); anything that moves beyond the radius
        // is a swipe (also Phase 2). For V1 we treat any short single-finger
        // touch as a tap and let the navigator cycle screens.
        const int TapMaxMs = 350;
        // Debounce: a lifting finger can bounce a 2nd quick tap right after one that changed screens,
        // which would then act on the NEW screen (e.g. immediately closing a just-opened sub-page).
        // Ignore taps that land within this window of the previous dispatched tap.
        const int TapDebounceMs = 250;
        static long _lastTapDispatchUtcTicks;
        // Swipe: a quick, mostly-horizontal flick beyond the tap radius -> page the rotation.
        const int SwipeMinDist = 70;  // min horizontal px to count as a swipe
        const int SwipeMaxMs = 600;   // a flick, not a slow drag
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

                // HTTP server RETIRED (2026-06-23). It was dev scaffolding (GET /webrtc-connect,
                // /webrtc-status, /webrtc-offer) for bringing WebRTC up. The watch's real surface is
                // BLE (pairing) + WebRTC (transport); neither needs an HTTP server. HttpServer.cs is
                // kept in the tree so it can return if a concrete need appears.
                //
                // Instead, start the autonomous WebRTC transport service: it owns the connection on its
                // own thread (connect -> stay connected -> reconnect on drop), gated on paired + WiFi.
                // No external trigger - the watch maintains its own link to the Companion. With the HTTP
                // server gone there is no second thread touching libpeer, so no concurrency to crash on.
                _webrtc = new WebRtcTransportService(_pairing, _wifi);
                if (EnableWebRtcTransport)
                {
                    _webrtc.Start();
                    Debug.WriteLine("[SpawnWear] WebRTC transport service started");
                }
                else
                {
                    Debug.WriteLine("[SpawnWear] WebRTC transport service DISABLED (freeze diagnostic 2026-06-23)");
                }
                var statusBar = new StatusBar(fb, BoardPins.LcdWidth, _axp, _rtc);
                _statusBar = statusBar; // expose to OnTick for the live Companion-link icon
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
                if (EnableAppRepo) _appRepo.Initialize();
                else Debug.WriteLine("[SpawnWear] AppRepo DISABLED (SD-freeze diagnostic 2026-06-24)");

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
            // BOOT (GPIO0) side button: short press = Back (pop a sub-page, else go Home);
            // long press = programmable action (default Home; the AI agent can claim it for hold-to-talk).
            if (_bootButtonClickPending != 0)
            {
                int kind = _bootButtonClickPending;
                _bootButtonClickPending = 0;
                // Match a screen touch: a press that woke the screen from dim/sleep only wakes it (the
                // _lastTouchUtcTicks bump already un-dimmed it); it does not also navigate. Act only when
                // the screen was already Active at press time.
                if (_nav != null && _bootStateAtPress == ScreenState.Active)
                {
                    if (kind == 1)
                    {
                        if (_nav.StackDepth > 0) _nav.Pop();
                        else if (_nav.CurrentIndex != 0) _nav.GoHome();
                    }
                    else
                    {
                        if (_bootLongPressAction != null) _bootLongPressAction();
                        else _nav.GoHome();
                    }
                }
            }

            // Execute any app/file-management commands that arrived on the bus (sys.apps / sys.files)
            // HERE on the main thread, so SD + UI work is serialized with the launcher / app-loader -
            // same anti-contention discipline the I2C lock enforces for the I2C bus.
            ProcessSysCommands();

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
                    // Refresh the Companion-link icon from the live transport state. Cheap: the status
                    // bar only repaints when the value actually changes (change-detection cache).
                    if (_statusBar != null && _webrtc != null)
                        _statusBar.SetCompanionConnected(_webrtc.Bus.IsConnected);
                    _nav.Current.Tick();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tick] EX " + ex.GetType().Name + ": " + ex.Message);
            }

            // Keep ticking fast while a widget screen is mid press-release animation, so the pressed
            // state stays visible briefly even on a very quick tap (finger lifts before a slow tick).
            var animWs = _nav != null ? _nav.Current as SpawnDev.UI.WidgetScreen : null;
            if (animWs != null && animWs.IsAnimating) return 16;
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

        // Set true by the "sys.disconnect" graceful-bye from the Companion; the persistent loop exits
        // immediately when it sees this (vs. waiting out the ICE keepalive timeout).
        static bool _sysDisconnectRequested;
        static void OnSysDisconnect(string channelId, byte[] payload)
        {
            _sysDisconnectRequested = true;
        }

        // ===== sys.* command channels: app + file management over the bus =====
        // Handlers run on the WebRTC pump thread (bus.RouteReceived) and ONLY enqueue. The real SD + UI
        // work runs on the MAIN thread (ProcessSysCommands, from OnTick), serialized with the launcher /
        // SD-browser / app-loader - so no cross-thread SD or framebuffer contention (the same discipline
        // the I2cLock enforces for the I2C bus). Replies go back on the same channel via the bus.
        static readonly System.Collections.ArrayList _sysCmdQueue = new System.Collections.ArrayList();
        static readonly object _sysCmdLock = new object();

        static void OnSysApps(string channelId, byte[] payload) { EnqueueSysCmd(channelId, payload); }
        static void OnSysFiles(string channelId, byte[] payload) { EnqueueSysCmd(channelId, payload); }

        static void EnqueueSysCmd(string channelId, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            lock (_sysCmdLock) { _sysCmdQueue.Add(new object[] { channelId, payload }); }
            if (_eventLoop != null) _eventLoop.Wake();
        }

        static void ProcessSysCommands()
        {
            if (_webrtc == null) return;
            var bus = _webrtc.Bus;
            while (true)
            {
                object[] item = null;
                lock (_sysCmdLock)
                {
                    if (_sysCmdQueue.Count == 0) break;
                    item = (object[])_sysCmdQueue[0];
                    _sysCmdQueue.RemoveAt(0);
                }
                try
                {
                    string ch = (string)item[0];
                    byte[] p = (byte[])item[1];
                    if (ch == "sys.apps") ProcessAppsCommand(bus, p);
                    else if (ch == "sys.files") ProcessFilesCommand(bus, p);
                }
                catch (Exception ex) { Debug.WriteLine("[sys] cmd EX " + ex.Message); }
            }
        }

        // sys.apps. Request: [op:u8][...]. Reply: [op:u8][ok:u8][...].
        //   op=1 LIST      -> [1][1][count:u8] then count*{[nameLen:u8][name][size:u32 LE]}
        //   op=2 INSTALL   [nameLen:u8][name][pe bytes...]  -> text reply
        //   op=3 UNINSTALL [nameLen:u8][name]               -> text reply
        //   op=4 LAUNCH    [nameLen:u8][name]               -> text reply
        static void ProcessAppsCommand(SpawnWear.Services.TransportBus bus, byte[] p)
        {
            if (p.Length < 1 || _appRepo == null) return;
            byte op = p[0];
            if (op == 1) // LIST
            {
                AppInfo[] apps = _appRepo.ListInfo();
                int n = apps.Length > 255 ? 255 : apps.Length;
                int total = 3;
                byte[][] names = new byte[n][];
                for (int i = 0; i < n; i++)
                {
                    names[i] = System.Text.Encoding.UTF8.GetBytes(apps[i].Name);
                    total += 1 + names[i].Length + 4;
                }
                byte[] r = new byte[total];
                int o = 0;
                r[o++] = 1; r[o++] = 1; r[o++] = (byte)n;
                for (int i = 0; i < n; i++)
                {
                    r[o++] = (byte)names[i].Length;
                    for (int k = 0; k < names[i].Length; k++) r[o++] = names[i][k];
                    uint sz = (uint)apps[i].Size;
                    r[o++] = (byte)(sz & 0xFF); r[o++] = (byte)((sz >> 8) & 0xFF);
                    r[o++] = (byte)((sz >> 16) & 0xFF); r[o++] = (byte)((sz >> 24) & 0xFF);
                }
                bus.Send("sys.apps", r);
                Debug.WriteLine("[sys.apps] LIST -> " + n + " apps");
            }
            else if (op == 2 || op == 3 || op == 4)
            {
                int nameLen = p.Length > 1 ? p[1] : 0;
                byte[] nb = new byte[nameLen];
                for (int k = 0; k < nameLen; k++) nb[k] = p[2 + k];
                string name = new string(System.Text.Encoding.UTF8.GetChars(nb));
                if (op == 2) // INSTALL
                {
                    int peOff = 2 + nameLen;
                    byte[] pe = new byte[p.Length - peOff];
                    for (int k = 0; k < pe.Length; k++) pe[k] = p[peOff + k];
                    bool ok = _appRepo.Install(name, pe);
                    SysReply(bus, "sys.apps", op, ok, ok ? "installed " + name + " (" + pe.Length + "b)" : "install failed: " + name);
                }
                else if (op == 3) // UNINSTALL
                {
                    bool ok = _appRepo.Uninstall(name);
                    SysReply(bus, "sys.apps", op, ok, ok ? "uninstalled " + name : "uninstall failed: " + name);
                }
                else // op == 4 LAUNCH (safe: we are on the main thread here)
                {
                    LaunchInstalledApp(name);
                    SysReply(bus, "sys.apps", op, true, "launched " + name);
                }
            }
        }

        // Generic text reply: [op:u8][ok:u8][msgLen:u8][msg UTF-8].
        static void SysReply(SpawnWear.Services.TransportBus bus, string ch, byte op, bool ok, string msg)
        {
            byte[] m = System.Text.Encoding.UTF8.GetBytes(msg);
            int len = m.Length > 200 ? 200 : m.Length;
            byte[] r = new byte[3 + len];
            r[0] = op; r[1] = (byte)(ok ? 1 : 0); r[2] = (byte)len;
            for (int k = 0; k < len; k++) r[3 + k] = m[k];
            bus.Send(ch, r);
        }

        // sys.files - SD card (D:\) file access over WebRTC. Request [op:u8][pathLen:u8][path][...].
        // Reply [op:u8][ok:u8][...]; on error ok=0 -> [op][0][msgLen:u8][msg] (via SysReply).
        //   op=1 LISTDIR [path][startIdx:u16 LE]?          -> [1][1][more:u8][count:u16 LE] count*{[nameLen:u8][name][isDir:u8][size:u32 LE]}
        //   op=2 STAT    [path]                            -> [2][1][exists:u8][isDir:u8][size:u32 LE]
        //   op=3 READ    [path][offset:u32 LE][len:u16 LE] -> [3][1][eof:u8][dataLen:u16 LE][data]   (len<=SysFileChunk)
        //   op=4 WRITE   [path][offset:u32 LE][flags:u8][dataLen:u16 LE][data] -> [4][1][written:u32 LE]   (flags bit0=truncate)
        //   op=5 DELETE  [path]                            -> text reply (recursive for dirs)
        //   op=6 MKDIR   [path]                            -> text reply (creates parents)
        //   op=7 MOVE    [path][newLen:u8][newPath]        -> text reply (rename/move)
        // Chunked: each READ/WRITE moves <=SysFileChunk bytes to stay under the ~1024-byte inbound frame
        // limit (SW_RX_MSG_MAX); the caller drives the chunk loop. Maps 1:1 to Dokan/WebFS callbacks
        // (FindFiles/GetFileInfo/ReadFile/WriteFile/DeleteFile/CreateDirectory/MoveFile) so the Companion
        // can surface the watch's SD as a Windows drive over WebRTC.
        // Replies (watch->console) must stay under the native send clamp SW_TX_MSG_MAX (512); read chunks
        // are sized for it and LISTDIR paginates. Inbound requests may be up to ~1024 (SW_RX_MSG_MAX).
        const int SysFileChunk = 480;       // read-reply data per chunk: 5 hdr + 480 + ~12 frame < 512 (SW_TX_MSG_MAX)
        const int SysFileListBudget = 460;  // max LISTDIR entry bytes per reply (+ 4 hdr + ~12 frame < 512)

        static void ProcessFilesCommand(SpawnWear.Services.TransportBus bus, byte[] p)
        {
            byte op = p[0];
            if (_sd == null || !_sd.IsMounted) { SysReply(bus, "sys.files", op, false, "SD not mounted"); return; }
            int pathLen = p.Length > 1 ? p[1] : 0;
            if (2 + pathLen > p.Length) { SysReply(bus, "sys.files", op, false, "bad request"); return; }
            byte[] pb = new byte[pathLen];
            for (int k = 0; k < pathLen; k++) pb[k] = p[2 + k];
            string rel = new string(System.Text.Encoding.UTF8.GetChars(pb));
            string full = ResolveSdPath(rel);
            if (full == null) { SysReply(bus, "sys.files", op, false, "bad path"); return; }
            int after = 2 + pathLen;
            try
            {
                if (op == 1) FilesListDir(bus, full, p, after);
                else if (op == 2) FilesStat(bus, full);
                else if (op == 3) FilesRead(bus, full, p, after);
                else if (op == 4) FilesWrite(bus, full, p, after);
                else if (op == 5) FilesDelete(bus, full);
                else if (op == 6) FilesMkdir(bus, full);
                else if (op == 7) FilesMove(bus, full, p, after);
                else SysReply(bus, "sys.files", op, false, "bad op");
            }
            catch (Exception ex)
            {
                SysReply(bus, "sys.files", op, false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Map a request path (relative to D:\, '/' or '\' separators) to a full D:\ path. Rejects
        // parent-traversal ("..") and any non-D: absolute path. Returns null if invalid.
        static string ResolveSdPath(string rel)
        {
            if (rel == null) rel = "";
            // nanoFramework String has no Replace - normalize '/' to '\' by hand.
            char[] ch = new char[rel.Length];
            for (int i = 0; i < rel.Length; i++) { char c = rel[i]; ch[i] = (c == '/') ? '\\' : c; }
            rel = new string(ch);
            if (rel.IndexOf("..") >= 0) return null;
            if (rel.Length >= 2 && rel[1] == ':')
            {
                if (rel[0] != 'D' && rel[0] != 'd') return null;
                rel = rel.Substring(2);
            }
            while (rel.Length > 0 && rel[0] == '\\') rel = rel.Substring(1);
            while (rel.Length > 1 && rel[rel.Length - 1] == '\\') rel = rel.Substring(0, rel.Length - 1);
            return rel.Length == 0 ? "D:\\" : "D:\\" + rel;
        }

        static uint ReadU32LE(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        static void WriteU32LE(byte[] b, int o, uint v) { b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF); b[o + 2] = (byte)((v >> 16) & 0xFF); b[o + 3] = (byte)((v >> 24) & 0xFF); }

        static byte[] FileEntry(string name, bool isDir, uint size)
        {
            byte[] nb = System.Text.Encoding.UTF8.GetBytes(name);
            if (nb.Length > 255) { byte[] t = new byte[255]; Array.Copy(nb, t, 255); nb = t; }
            byte[] e = new byte[1 + nb.Length + 1 + 4];
            int o = 0;
            e[o++] = (byte)nb.Length;
            for (int k = 0; k < nb.Length; k++) e[o++] = nb[k];
            e[o++] = (byte)(isDir ? 1 : 0);
            WriteU32LE(e, o, size);
            return e;
        }

        static void FilesListDir(SpawnWear.Services.TransportBus bus, string full, byte[] p, int after)
        {
            if (!System.IO.Directory.Exists(full)) { SysReply(bus, "sys.files", 1, false, "no such dir"); return; }
            int startIdx = (after + 2 <= p.Length) ? (p[after] | (p[after + 1] << 8)) : 0;
            // nanoFramework's FATFS enumeration needs a TRAILING backslash: the drive root "D:\" lists
            // fine, but a subdir as "D:\ftest" returns nothing (and GetDirectories can throw an
            // empty-message IOException on a files-only dir). Enumerate with the trailing separator, and
            // stay defensive so one failing call doesn't blank the whole listing.
            string ep = (full.Length > 0 && full[full.Length - 1] == '\\') ? full : full + "\\";
            string[] dirs;
            try { dirs = System.IO.Directory.GetDirectories(ep); }
            catch (Exception ex) { dirs = new string[0]; Debug.WriteLine("[sys.files] GetDirectories EX " + ex.GetType().Name + " '" + ex.Message + "'"); }
            string[] files;
            try { files = System.IO.Directory.GetFiles(ep); }
            catch (Exception ex) { files = new string[0]; Debug.WriteLine("[sys.files] GetFiles EX " + ex.GetType().Name + " '" + ex.Message + "'"); }
            int total = dirs.Length + files.Length;
            // Emit entries [startIdx..) accumulating until the reply would exceed SysFileListBudget,
            // so each reply stays under the native send clamp (SW_TX_MSG_MAX). more=1 -> caller re-asks
            // with the next startIdx.
            System.Collections.ArrayList parts = new System.Collections.ArrayList();
            int body = 0;
            byte more = 0;
            for (int i = startIdx; i < total; i++)
            {
                byte[] e;
                if (i < dirs.Length) e = FileEntry(System.IO.Path.GetFileName(dirs[i]), true, 0);
                else { string f = files[i - dirs.Length]; uint sz = 0; try { sz = (uint)new System.IO.FileInfo(f).Length; } catch { } e = FileEntry(System.IO.Path.GetFileName(f), false, sz); }
                if (parts.Count > 0 && body + e.Length > SysFileListBudget) { more = 1; break; }
                parts.Add(e); body += e.Length;
            }
            int cnt = parts.Count;
            byte[] r = new byte[5 + body];
            r[0] = 1; r[1] = 1; r[2] = more; r[3] = (byte)(cnt & 0xFF); r[4] = (byte)((cnt >> 8) & 0xFF);
            int o = 5;
            for (int i = 0; i < parts.Count; i++)
            {
                byte[] e = (byte[])parts[i];
                for (int k = 0; k < e.Length; k++) r[o++] = e[k];
            }
            bus.Send("sys.files", r);
            Debug.WriteLine("[sys.files] LISTDIR " + full + " [" + startIdx + "] -> " + cnt + " more=" + more);
        }

        static void FilesStat(SpawnWear.Services.TransportBus bus, string full)
        {
            byte exists = 0, isDir = 0; uint size = 0;
            if (System.IO.Directory.Exists(full)) { exists = 1; isDir = 1; }
            else if (System.IO.File.Exists(full)) { exists = 1; try { size = (uint)new System.IO.FileInfo(full).Length; } catch { } }
            byte[] r = new byte[8];
            r[0] = 2; r[1] = 1; r[2] = exists; r[3] = isDir; WriteU32LE(r, 4, size);
            bus.Send("sys.files", r);
        }

        static void FilesRead(SpawnWear.Services.TransportBus bus, string full, byte[] p, int after)
        {
            if (after + 6 > p.Length) { SysReply(bus, "sys.files", 3, false, "bad read req"); return; }
            uint offset = ReadU32LE(p, after);
            int len = p[after + 4] | (p[after + 5] << 8);
            if (len > SysFileChunk) len = SysFileChunk;
            if (!System.IO.File.Exists(full)) { SysReply(bus, "sys.files", 3, false, "no such file"); return; }
            byte[] data; long size; int rd;
            using (System.IO.FileStream fs = new System.IO.FileStream(full, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            {
                size = fs.Length;
                if (offset > size) offset = (uint)size;
                fs.Seek(offset, System.IO.SeekOrigin.Begin);
                data = new byte[len];
                rd = fs.Read(data, 0, len);
            }
            byte eof = (byte)((offset + (uint)rd >= (uint)size) ? 1 : 0);
            byte[] r = new byte[5 + rd];
            r[0] = 3; r[1] = 1; r[2] = eof; r[3] = (byte)(rd & 0xFF); r[4] = (byte)((rd >> 8) & 0xFF);
            for (int k = 0; k < rd; k++) r[5 + k] = data[k];
            bus.Send("sys.files", r);
        }

        static void FilesWrite(SpawnWear.Services.TransportBus bus, string full, byte[] p, int after)
        {
            if (after + 7 > p.Length) { SysReply(bus, "sys.files", 4, false, "bad write req"); return; }
            uint offset = ReadU32LE(p, after);
            byte flags = p[after + 4];
            int dataLen = p[after + 5] | (p[after + 6] << 8);
            int dataOff = after + 7;
            if (dataOff + dataLen > p.Length) dataLen = p.Length - dataOff;
            bool truncate = (flags & 1) != 0;
            using (System.IO.FileStream fs = new System.IO.FileStream(full, truncate ? System.IO.FileMode.Create : System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.Write))
            {
                if (!truncate && offset > 0) fs.Seek(offset, System.IO.SeekOrigin.Begin);
                if (dataLen > 0) fs.Write(p, dataOff, dataLen);
                fs.Flush();
            }
            byte[] r = new byte[6];
            r[0] = 4; r[1] = 1; WriteU32LE(r, 2, offset + (uint)dataLen);
            bus.Send("sys.files", r);
        }

        static void FilesDelete(SpawnWear.Services.TransportBus bus, string full)
        {
            if (System.IO.Directory.Exists(full)) { DeleteDirRecursive(full); SysReply(bus, "sys.files", 5, true, "deleted dir"); }
            else if (System.IO.File.Exists(full)) { System.IO.File.Delete(full); SysReply(bus, "sys.files", 5, true, "deleted"); }
            else SysReply(bus, "sys.files", 5, false, "not found");
        }

        static void DeleteDirRecursive(string dir)
        {
            // nanoFramework FATFS enumeration needs a trailing backslash on a non-root path, else it
            // returns nothing - which would leave nested files undeleted and throw on Directory.Delete.
            string ep = (dir.Length > 0 && dir[dir.Length - 1] == '\\') ? dir : dir + "\\";
            string[] files = System.IO.Directory.GetFiles(ep);
            for (int i = 0; i < files.Length; i++) System.IO.File.Delete(files[i]);
            string[] subs = System.IO.Directory.GetDirectories(ep);
            for (int i = 0; i < subs.Length; i++) DeleteDirRecursive(subs[i]);
            System.IO.Directory.Delete(dir);
        }

        static void FilesMkdir(SpawnWear.Services.TransportBus bus, string full)
        {
            // Create each level under D:\ (nanoFramework CreateDirectory does not make intermediates).
            if (full.Length > 3)
            {
                string rest = full.Substring(3);
                string cur = "D:\\";
                string[] segs = rest.Split('\\');
                for (int i = 0; i < segs.Length; i++)
                {
                    if (segs[i].Length == 0) continue;
                    cur = cur + segs[i];
                    if (!System.IO.Directory.Exists(cur)) System.IO.Directory.CreateDirectory(cur);
                    cur = cur + "\\";
                }
            }
            SysReply(bus, "sys.files", 6, true, "ok");
        }

        static void FilesMove(SpawnWear.Services.TransportBus bus, string full, byte[] p, int after)
        {
            if (after >= p.Length) { SysReply(bus, "sys.files", 7, false, "bad move req"); return; }
            int nlen = p[after];
            if (after + 1 + nlen > p.Length) { SysReply(bus, "sys.files", 7, false, "bad move req"); return; }
            byte[] nb = new byte[nlen];
            for (int k = 0; k < nlen; k++) nb[k] = p[after + 1 + k];
            string ndest = ResolveSdPath(new string(System.Text.Encoding.UTF8.GetChars(nb)));
            if (ndest == null) { SysReply(bus, "sys.files", 7, false, "bad dest"); return; }
            if (System.IO.File.Exists(full)) { System.IO.File.Move(full, ndest); SysReply(bus, "sys.files", 7, true, "moved"); }
            else if (System.IO.Directory.Exists(full)) { System.IO.Directory.Move(full, ndest); SysReply(bus, "sys.files", 7, true, "moved dir"); }
            else SysReply(bus, "sys.files", 7, false, "not found");
        }

        // RETIRED dev HTTP trigger. The autonomous WebRtcTransportService now owns the connection
        // lifecycle and drives WebRtcConnectRun(bus) on its own thread; the HTTP server is no longer
        // started. Kept as a no-op so the (retired) HttpServer.cs still compiles.
        public static void StartWebRtcConnect()
        {
            ConnectStatus = "ignored - WebRTC is now autonomous (WebRtcTransportService)";
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

        // Public so the autonomous WebRtcTransportService can drive it. Blocks for the life of one
        // connection (connect -> mutual challenge -> stay connected, pumping the channel Bus, until the
        // peer disconnects). The bus carries the OS + app channels multiplexed over this one link.
        public static void WebRtcConnectRun(SpawnWear.Services.TransportBus bus)
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
                // Phase 7d: ROBUST announce. A single announce-and-hope is brittle - it only works if
                // the companion happens to be in the tracker pool at that exact instant. Instead,
                // re-announce our offer every ~5s for up to ~60s. The companion re-announces on the
                // hub's interval, so once it's in the pool our next re-announce reaches it and it
                // answers. WaitForAnswer reads the live socket and discards non-answers, so looping it
                // between re-announces just keeps reading.
                string answer = null;
                for (int attempt = 1; attempt <= 12 && answer == null; attempt++)
                {
                    tr.AnnounceOffer(room, pid, oid, offer);
                    ConnectStatus = "announcing (try " + attempt + ")";
                    LogSd("announce try " + attempt);
                    answer = tr.WaitForAnswer(oid, 5000);
                }
                if (answer == null) { ConnectStatus = "no-answer (after re-announces)"; return; }
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

                    // Phase 7d: PERSISTENT connection. Instead of holding 8s and closing, stay connected
                    // for the LIFE of the link: drain incoming messages and keep the channel up until the
                    // peer disconnects (GetState leaves Completed) or the link drops. This also gives the
                    // companion all the time it needs to finish ITS half of the mutual challenge. (First
                    // version runs on the HTTP thread, so /webrtc-connect stays open for the connection's
                    // life; a background-thread WebRTC service is the next refinement.)
                    LogSd("CONNECTED - bus pump + telemetry stream");
                    bus.ClearSendQueue();     // drop anything stale from a previous link
                    bus.IsConnected = true;
                    // Graceful-bye: the Companion sends "sys.disconnect" right before it tears down, so
                    // we exit immediately instead of waiting out the ~10s ICE keepalive timeout (which
                    // stays as the fallback for ungraceful drops - crash, dead WiFi).
                    _sysDisconnectRequested = false;
                    bus.Subscribe("sys.disconnect", OnSysDisconnect);
                    bus.Subscribe("sys.apps", OnSysApps);   // app management (list/install/uninstall/launch)
                    bus.Subscribe("sys.files", OnSysFiles); // SD card file access (listdir/stat/read/write/delete/mkdir)
                    // DEMO of the app.* lane: a scoped app channel that streams MessagePack alongside the
                    // OS telemetry, proving the two lanes coexist + stay isolated. A real loadable app gets
                    // its IAppChannel from the app host exactly this way (confined to app.demo.*).
                    SpawnWear.Services.IAppChannel demoApp = bus.OpenAppChannel("demo");
                    int rxCount = 0, txCount = 0, tick = 0, seq = 0;
                    byte[] rxFrame = new byte[1100]; // >= native SW_RX_MSG_MAX (1024)
                    while (SpawnDev.WebRTC.PeerConnection.GetState(h) == SpawnDev.WebRTC.PeerConnection.StateCompleted
                           && !_sysDisconnectRequested)
                    {
                        System.Threading.Thread.Sleep(200);

                        // 1) drain the bus send queue (OS + app channels, multiplexed) onto the wire
                        byte[] frame;
                        while ((frame = bus.DequeueSend()) != null)
                        {
                            SpawnDev.WebRTC.PeerConnection.Send(h, frame, frame.Length);
                            txCount++;
                        }

                        // 2) route any inbound frame to its channel subscriber (isolated per-handler)
                        int rn = SpawnDev.WebRTC.PeerConnection.TryReceive(h, rxFrame);
                        if (rn > 0)
                        {
                            rxCount++;
                            bus.RouteReceived(rxFrame, rn);
                        }

                        // 3) first sys.* consumer. IMU every tick (~200ms = 5 Hz) for smooth live
                        //    motion - cheap now that Send is lock-free. Battery every ~2 s (changes
                        //    slowly; keeps the shared I2C bus light). Both on the existing channels the
                        //    Companion dashboard already decodes ("imu" / "battery").
                        PublishImu(bus);
                        tick++;
                        if (tick >= 10)
                        {
                            tick = 0;
                            seq++;
                            PublishBattery(bus);
                            PublishDemo(demoApp, seq); // app.* lane (MessagePack), alongside sys telemetry
                            ConnectStatus = "CONNECTED - streaming (seq=" + seq + " tx=" + txCount + " rx=" + rxCount + ")";
                        }
                    }
                    bus.IsConnected = false;
                    demoApp.Close(); // app lifecycle: unsubscribe the demo channel on teardown
                    bus.ClearSendQueue();
                    string why = _sysDisconnectRequested ? "companion sys.disconnect" : "keepalive timeout";
                    ConnectStatus = "disconnected (seq=" + seq + " tx=" + txCount + " rx=" + rxCount + ")";
                    LogSd("peer disconnected (" + why + ", seq=" + seq + " tx=" + txCount + " rx=" + rxCount + ")");
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

        // First sys.* telemetry consumers: publish on the EXISTING Companion channels, in the same
        // binary schemas WatchProfileService notifies over BLE - so the Companion dashboard
        // (BridgeClient decode + Home.razor cards) renders them unchanged.
        //   battery: [percent:u8][flags:u8][mV:u16-LE][mA:i16-LE]
        //   imu:     [ax,ay,az,gx,gy,gz : i16-LE]   accel = milli-g, gyro = deci-dps (clamped to i16)
        static void PublishBattery(SpawnWear.Services.TransportBus bus)
        {
            try
            {
                int pct = _axp.ReadBatteryPercent();
                int mv = _axp.ReadBatteryMillivolts();
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                if (mv < 0) mv = 0;
                byte[] bat = new byte[6];
                bat[0] = (byte)pct;
                bat[1] = 0; // flags: charging/vbus/low not wired yet
                bat[2] = (byte)(mv & 0xFF);
                bat[3] = (byte)((mv >> 8) & 0xFF);
                bat[4] = 0;
                bat[5] = 0; // current mA not measured yet
                bus.Send("battery", bat);
            }
            catch (Exception ex) { Debug.WriteLine("[Telemetry] battery EX " + ex.Message); }
        }

        static void PublishImu(SpawnWear.Services.TransportBus bus)
        {
            try
            {
                Qmi8658Driver.ImuSample s;
                if (_imu != null && _imu.TryRead(out s))
                {
                    byte[] imu = new byte[12];
                    WriteI16Clamped(imu, 0, (int)(s.AccelX * 1000f));
                    WriteI16Clamped(imu, 2, (int)(s.AccelY * 1000f));
                    WriteI16Clamped(imu, 4, (int)(s.AccelZ * 1000f));
                    WriteI16Clamped(imu, 6, (int)(s.GyroX * 10f));
                    WriteI16Clamped(imu, 8, (int)(s.GyroY * 10f));
                    WriteI16Clamped(imu, 10, (int)(s.GyroZ * 10f));
                    bus.Send("imu", imu);
                }
            }
            catch (Exception ex) { Debug.WriteLine("[Telemetry] imu EX " + ex.Message); }
        }

        static void WriteI16Clamped(byte[] buf, int off, int val)
        {
            if (val > 32767) val = 32767;
            if (val < -32768) val = -32768;
            buf[off] = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
        }

        // DEMO of the app.* lane: a MessagePack map {msg, seq, tempC} on app.demo.ping. Proves the app
        // channel (namespaced, isolated) + the nanoFramework MessagePack encoder end to end - the
        // Companion decodes with MessagePack-CSharp. A real loadable app defines its own typed messages
        // exactly this way, confined to its own app.<appId>.* namespace.
        static void PublishDemo(SpawnWear.Services.IAppChannel demo, int seq)
        {
            float tempC = 0f;
            try
            {
                Qmi8658Driver.ImuSample s;
                if (_imu != null && _imu.TryRead(out s)) tempC = s.TempC;
            }
            catch { }
            var w = new SpawnWear.Services.MsgPackWriter(64);
            w.WriteMapHeader(3);
            w.WriteString("msg");   w.WriteString("hello from SpawnWear");
            w.WriteString("seq");   w.WriteInt(seq);
            w.WriteString("tempC"); w.WriteFloat(tempC);
            demo.Send("ping", w.ToArray());
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
                            // Raw press -> widget press-state animation (only on an active screen).
                            if (_nav != null && _screenState == ScreenState.Active)
                            {
                                var pressDown = _nav.Current as SpawnDev.UI.IPressable;
                                if (pressDown != null) pressDown.OnPress(snapX, snapY);
                            }
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
                        int adx = dx < 0 ? -dx : dx;
                        int ady = dy < 0 ? -dy : dy;
                        bool isSwipe = !stayedPut && elapsedMs < SwipeMaxMs && adx >= SwipeMinDist && adx > ady;
                        Debug.WriteLine("[Touch] UP elapsed=" + elapsedMs + "ms dxdy=(" + dx + "," + dy + ") tap=" + isTap + " long=" + isLongPress + " swipe=" + isSwipe);
                        // Wake-tap consumption: any gesture whose finger-DOWN happened while
                        // the screen was asleep is consumed by the wake itself, not dispatched
                        // to the UI.
                        if (_nav != null && _stateAtFingerDown == ScreenState.Active)
                        {
                            // Release the press-state first (button returns to normal), then the tap.
                            var pressUp = _nav.Current as SpawnDev.UI.IPressable;
                            if (pressUp != null) pressUp.OnRelease();
                            if (isLongPress) _nav.GoHome();
                            else if (isSwipe)
                            {
                                if (dx < 0) _nav.Next();  // swipe left -> next screen
                                else _nav.Prev();          // swipe right -> previous screen
                            }
                            else if (isTap)
                            {
                                long sinceLastTapMs = (nowTicks - _lastTapDispatchUtcTicks) / TimeSpan.TicksPerMillisecond;
                                if (sinceLastTapMs >= TapDebounceMs)
                                {
                                    _lastTapDispatchUtcTicks = nowTicks;
                                    _nav.HandleTap(_fingerLastX, _fingerLastY);
                                }
                                else
                                {
                                    Debug.WriteLine("[Touch] tap debounced (" + sinceLastTapMs + "ms since last)");
                                }
                            }
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
                    // InputPullUp: Falling = press, Rising = release. Classify short (= Back) vs long
                    // (= programmable, e.g. hold-to-talk to the AI agent) on release, like a Fitbit.
                    long now = DateTime.UtcNow.Ticks;
                    if (args.ChangeType == System.Device.Gpio.PinEventTypes.Falling)
                    {
                        _bootStateAtPress = _screenState; // capture BEFORE counting activity (wake-vs-act)
                        _bootDownUtcTicks = now;
                        _lastTouchUtcTicks = now;          // count as activity -> wake/un-dim the screen
                    }
                    else if (args.ChangeType == System.Device.Gpio.PinEventTypes.Rising && _bootDownUtcTicks != 0)
                    {
                        _lastTouchUtcTicks = now;          // activity
                        long heldMs = (now - _bootDownUtcTicks) / TimeSpan.TicksPerMillisecond;
                        _bootButtonClickPending = heldMs >= BootLongPressMs ? 2 : 1; // 1 = back, 2 = long
                        Debug.WriteLine("[Boot] " + (_bootButtonClickPending == 2 ? "LONG" : "short") + " press (" + heldMs + "ms)");
                        if (_eventLoop != null) _eventLoop.Wake();
                    }
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
