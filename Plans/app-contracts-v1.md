# SpawnWear.AppContracts v1

Concrete C# surface for the SD-card-loadable apps architecture (`Plans/sd-card-apps.md`). This file is the v1 design - a single shared assembly that both the SpawnWear firmware AND every external app references. Apps implement `ISpawnApp`; the firmware passes them an `IServiceHost` from which they pull the specific capabilities they need.

The surface here is small on purpose. Contracts that ship in v1 must be APPEND-ONLY at the type level forever - new methods on `ISpawnApp` would break apps built against earlier versions. Capabilities arrive as new optional interfaces (e.g. `IAppHasNotifications`, `IAppHasBackgroundService`) that the firmware checks for via `is` casts at runtime.

This file is design, not buildable code. The first commit to a real `SpawnWear.AppContracts.nfproj` should match this shape line-for-line; deviations want an explicit rationale here.

## Project shape

```
SpawnWear/                                          (consuming repo root)
├── SpawnWear.AppContracts/                         (Phase 8 - new project)
│   ├── SpawnWear.AppContracts.nfproj
│   ├── ISpawnApp.cs
│   ├── IServiceHost.cs
│   ├── Capabilities/
│   │   ├── IAppHasNotifications.cs
│   │   ├── IAppHasBackgroundService.cs
│   │   └── IAppHasSettingsPage.cs
│   ├── Services/
│   │   ├── IDisplayBuffer.cs
│   │   ├── IInputProvider.cs
│   │   ├── IPowerService.cs
│   │   ├── IRtcService.cs
│   │   ├── IStorageService.cs
│   │   ├── IWifiService.cs
│   │   └── ILogger.cs
│   └── AppManifest.cs                              (POCO, decoded from manifest.json)
└── SpawnWear/                                      (firmware - already exists)
    └── SpawnWear.nfproj  -> references AppContracts
```

The `.nfproj` ships as a NuGet package on TJ's local feed (`D:/users/SpawnDevPackages/`). App authors take it as a `<PackageReference>`. The firmware takes it as a `<ProjectReference>`.

Versioning: bump major on contract changes the firmware can't backward-handle (e.g. removing an interface). Bump minor on additive changes (new capability interface, new method on a capability that the firmware checks for via `is` first). Both firmware and apps record the AppContracts version they were built against; the launcher's app loader compares before activating an app and refuses to load if the major version mismatches.

## Core contracts

### ISpawnApp

```csharp
namespace SpawnWear.AppContracts;

/// <summary>
/// Minimal contract every external app implements. The launcher activates
/// exactly one app at a time; this interface is the lifecycle surface the
/// firmware drives. Apps live on the launcher's framebuffer and react to
/// touch + tick events on the same UI thread.
///
/// Apps MUST be cheap to instantiate. The constructor runs at the moment
/// the user taps the launcher tile; long-running setup goes in OnCreate.
/// </summary>
public interface ISpawnApp
{
    /// <summary>
    /// Called once after the app is loaded from disk and before any other
    /// method. Use this to capture the IServiceHost reference, allocate
    /// long-lived state, and declare which optional capabilities your app
    /// implements. Return false to refuse activation (e.g. required service
    /// not available); the launcher will display an error toast.
    /// </summary>
    bool OnCreate(IServiceHost services);

    /// <summary>
    /// Called when the app becomes the foreground screen. Repaint the
    /// framebuffer here; the firmware guarantees the panel is in Active
    /// state and the framebuffer is yours to scribble on.
    /// </summary>
    void OnResume();

    /// <summary>
    /// Called when the app stops being the foreground screen. Stop any
    /// timers, flush any pending I/O, free GC roots that don't need to
    /// survive into background. Called BEFORE the next app's OnResume.
    /// </summary>
    void OnPause();

    /// <summary>
    /// Called once when the app is being unloaded. App instance should
    /// drop any references held to IServiceHost subsurfaces so the GC can
    /// collect them.
    /// </summary>
    void OnDestroy();

    /// <summary>
    /// Called by the firmware's event loop while the app is active. The
    /// firmware decides the tick budget (16 ms while finger held, 1 s
    /// otherwise) and only calls Tick when something might change. Apps
    /// should NOT spin in a loop here - return promptly.
    /// </summary>
    void Tick();

    /// <summary>
    /// Called on a single-finger tap inside the panel. Coordinates are in
    /// raw panel space (0..panelWidth, 0..panelHeight). Return true to
    /// consume the tap; false lets the launcher take over (typically
    /// returning to the home screen on long-press, etc.).
    /// </summary>
    bool OnTap(int x, int y);
}
```

### IServiceHost

```csharp
namespace SpawnWear.AppContracts;

/// <summary>
/// The single point through which apps reach system services. The host
/// lazily exposes specific capabilities; apps ask via TryGet&lt;T&gt; and
/// fall back gracefully if a capability isn't present (e.g. WiFi off,
/// no SD card, etc.).
///
/// The instance handed to OnCreate is valid for the lifetime of the app.
/// Apps MUST NOT cache references to specific services across an
/// OnPause / OnResume boundary; pull them again on resume so the firmware
/// can swap out implementations (e.g. low-power vs full-power audio).
/// </summary>
public interface IServiceHost
{
    /// <summary>
    /// Try to obtain a specific service. Returns false if the requested
    /// service isn't currently available; the app should handle that case
    /// gracefully.
    ///
    /// Generic dispatch isn't supported on nanoFramework, so we use type-
    /// specific accessors (GetDisplayBuffer / GetRtc / etc.) below instead.
    /// This method is here for future expansion.
    /// </summary>
    bool TryGetService(System.Type contract, out object service);

    IDisplayBuffer GetDisplayBuffer();
    IInputProvider GetInputProvider();
    IPowerService GetPower();
    IRtcService GetRtc();
    IStorageService GetStorage();
    IWifiService GetWifi();   // null if WiFi service not running
    ILogger GetLogger();

    /// <summary>
    /// Path to the app's private data directory on the SD card.
    /// Persists across uninstall if a "keep-data" marker is present.
    /// </summary>
    string GetAppDataDirectory();

    /// <summary>
    /// Path to the app's read-only assets directory on the SD card.
    /// Useful for icons, fonts, sounds bundled with the app payload.
    /// </summary>
    string GetAppAssetsDirectory();

    /// <summary>
    /// AppContracts version this firmware was built against.
    /// Apps can compare against their own AppContractsVersion baked
    /// at build time and refuse to run if the firmware is older.
    /// </summary>
    string GetAppContractsVersion();
}
```

### IDisplayBuffer

```csharp
namespace SpawnWear.AppContracts;

using System.Drawing;

/// <summary>
/// Framebuffer surface apps draw into. Mirrors the subset of
/// nanoFramework.UI.Bitmap that's safe for app code; hides direct
/// access to the native framebuffer pointer so apps can't accidentally
/// trample firmware state.
///
/// Coordinates are in panel-relative pixels. Apps SHOULD reserve space
/// for the system status bar (top StatusBarHeight pixels) and the page
/// indicator (bottom PageIndicatorHeight pixels) - the firmware keeps
/// drawing into those regions on every tick.
/// </summary>
public interface IDisplayBuffer
{
    int Width { get; }
    int Height { get; }
    int StatusBarHeight { get; }
    int PageIndicatorHeight { get; }
    int CornerSafeInset { get; }    // bezel inset; tile content stays inside

    void Clear(Color background);
    void FillRectangle(int x, int y, int w, int h, Color color);
    void DrawString(string text, int x, int y, int scale, Color color);
    int MeasureString(string text, int scale);

    /// <summary>
    /// Push pending pixels to the panel. Apps SHOULD call Flush at the end
    /// of OnResume / on visible state changes; the firmware doesn't auto-
    /// flush on app's behalf.
    /// </summary>
    void Flush();

    /// <summary>
    /// Partial flush for animation hot paths. The firmware applies
    /// CO5300 even/odd alignment automatically; pass any rectangle.
    /// </summary>
    void Flush(int x, int y, int w, int h);
}
```

### Service surfaces (sketches)

```csharp
namespace SpawnWear.AppContracts;

public interface IPowerService
{
    int BatteryPercent { get; }       // -1 = uncalibrated
    int BatteryMillivolts { get; }
    bool IsCharging { get; }
    bool IsVbusPresent { get; }
}

public interface IRtcService
{
    System.DateTime Now { get; }
    int Weekday { get; }              // 0=Sun .. 6=Sat
    bool IsValid { get; }             // false if RTC's OS flag is set
}

public interface IStorageService
{
    bool IsSdCardMounted { get; }

    // app-private settings; firmware persists to internal flash so they
    // survive SD-card removal
    string GetSetting(string key, string defaultValue);
    void SetSetting(string key, string value);

    // app-private files on SD; throws if SD is unmounted
    System.IO.Stream OpenAppFile(string relativePath, System.IO.FileMode mode);
}

public interface IWifiService
{
    bool IsConnected { get; }
    string IpAddress { get; }         // null when not connected
    string ConnectedSsid { get; }     // null when not connected
    int SignalBars { get; }           // 0..4; -1 if radio off
}

public interface IInputProvider
{
    /// <summary>
    /// Subscribe to long-press events while this app is foreground. The
    /// firmware invokes the callback on the UI thread before unwinding
    /// the gesture; apps return true to consume (and stay foreground)
    /// or false to let the launcher take over.
    /// </summary>
    void SubscribeLongPress(LongPressHandler handler);
    void UnsubscribeLongPress(LongPressHandler handler);
}

public delegate bool LongPressHandler(int x, int y, int durationMs);

public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
```

### Optional capabilities

Apps OPT IN to capabilities by implementing additional interfaces beyond `ISpawnApp`. The firmware checks for them via `is` casts when relevant.

```csharp
namespace SpawnWear.AppContracts;

/// <summary>
/// App declares it has notifications worth surfacing on the launcher tile.
/// Firmware polls NotificationCount periodically and renders the badge.
/// </summary>
public interface IAppHasNotifications
{
    int NotificationCount { get; }     // 0..N, where N renders as "N+" if > 9
}

/// <summary>
/// App has a Settings page that should appear under Settings > Apps.
/// Firmware constructs the page lazily and calls RenderSettings when
/// the user navigates to it.
/// </summary>
public interface IAppHasSettingsPage
{
    void RenderSettings(IDisplayBuffer fb);
    bool OnSettingsTap(int x, int y);
}

/// <summary>
/// App has a background tick that should run even when the app is paused.
/// The firmware calls BackgroundTick at a coarser rate (default 30 s)
/// and the app should NOT do anything that holds power rails active.
///
/// This is for apps like a stopwatch that need to keep counting while the
/// user is on a different app, NOT for arbitrary background work. Apps
/// that hog this get killed.
/// </summary>
public interface IAppHasBackgroundService
{
    void BackgroundTick();
    int PreferredBackgroundIntervalMs { get; }
}
```

## App manifest schema

`/sd/apps/<id>/manifest.json`:

```json
{
  "id": "com.tj.calendar",
  "name": "Calendar",
  "version": "1.0.0",
  "appContractsVersion": "1.0",
  "entryAssembly": "TJ.Calendar.pe",
  "entryType": "TJ.Calendar.CalendarApp",
  "icon": "icon.png",
  "accent": "#3060A0",
  "requiredServices": ["IDisplayBuffer", "IRtcService", "IStorageService"],
  "minFirmwareVersion": "0.2.0"
}
```

Validated by the launcher's app loader on scan. Missing fields = app is unloadable; the launcher renders a red-X tile and surfaces the reason in Settings > Apps.

## V1 verification harness (Phase 8a smoke test)

Before committing to the full launcher integration, build a throwaway harness that exercises the dynamic-load path end to end:

1. **Hello-world app** - implements `ISpawnApp` with `OnResume` drawing "HELLO FROM SD" centered. Built against `SpawnWear.AppContracts v1.0`. Outputs `helloworld.pe`.
2. **Manual load test** - drop `helloworld.pe` on the SD card at `/sd/apps/com.tj.helloworld/app.pe`. Add a debug screen to SpawnWear that, when tapped, reads the file into a byte[], calls `Assembly.Load(byte[])`, finds the `ISpawnApp` type, instantiates it, calls `OnCreate(serviceHost) + OnResume()`. Verify "HELLO FROM SD" renders.
3. **Reload test** - load + drop reference + load again. Confirm heap doesn't grow unboundedly. If it does, document and design around the constraint.
4. **Two-app test** - load app A, navigate away (call OnPause + drop), load app B, navigate back to A by re-loading from SD. Confirm A starts cleanly.
5. **Bad-flag test** - hand-edit a .pe to set `c_Flags_NeedReboot` (offset 16 bit 0). Confirm Assembly.Load throws + the launcher catches + renders a "needs reboot" toast.
6. **Collision test** - load `helloworld.pe` twice without a reboot. Confirm second load either succeeds (and app B is reachable) or fails predictably; document either way.
7. **Cross-version test** - app built against `AppContracts v1.0` against firmware built against v1.1 (additive change). Confirm load + activation succeed.

These tests don't need automation - they're one-shot manual smokes that prove the architecture is viable. Once they pass, Phase 8 has a green light.
