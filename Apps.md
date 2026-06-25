# SpawnWear Apps

Each subfolder in this repo's root that ends in `App` is a standalone, dynamically-loadable SpawnWear app. They're separate `.nfproj`s that reference the `SpawnWear.AppContracts` interface surface (the `SpawnWear.AppContracts` project in this repo).

## Try them

Apps install as loose `.pe` files on the watch's SD card. The firmware's `AppRepositoryService` enumerates `D:\apps` (one directory per app) and shows a live launcher tile for each.

1. Build the app you want (see "Build them yourself" below) to produce its `.pe`.
2. Copy the `.pe` to the SD card under `D:\apps\<AppName>\` - either pull the card and copy it from a desktop, or push it over the WebRTC `sys.files` lane (chunked SD write; see [`Docs/transport.md`](Docs/transport.md)).
3. Re-mount the card / reboot the watch. The launcher shows a tile per app it found.
4. Tap the app's tile on the launcher to run it.
5. Long-press anywhere on the watch screen → return to the launcher.

**SpawnWear.Companion PWA** (pairing + live telemetry/console over BLE + the WebRTC bus):

1. `dotnet run --project SpawnWear.Companion -- --urls http://localhost:5251` from this repo, or visit a hosted Companion build.
2. On the Home page click **Pair watch** → browser shows the Bluetooth picker → select `SW-OK-Tok`.
3. The **Console** page shows the watch's `Debug.WriteLine` stream; telemetry pages show live battery / IMU / RTC.

(Live screen capture is now pulled over USB with `tools/nf-screenshot.cs` - BOOT-button triggered - not over HTTP. The old `http://<watch-ip>:8080/` drop-zone and `/loadapp` POST path are retired; the firmware no longer runs an HTTP server.)

## App library

| App | Folder | What it does | Demonstrates |
|---|---|---|---|
| **CounterApp** | `CounterApp/` | Tap to increment a counter. Big number centered. | Basic state mutation; `OnTap`; full-frame redraw |
| **DiceApp** | `DiceApp/` | Tap to roll a six-sided die. Classical pip layout. | Random source; complex rendering with helper functions |
| **PaintApp** | `PaintApp/` | Tap to paint colored dots on a black canvas. 8-color palette cycles per tap. | `OnTap` with non-trivial side effects; partial flush of just the dirty rectangle; respecting status-bar / page-indicator zones |
| **HelloWorldApp** | `HelloWorldApp/` | Smoke-test only - exposes a static `Greet()` method instead of implementing `ISpawnApp`. | Pure `Assembly.Load(byte[])` + `MethodInfo.Invoke` happy path. **Won't activate** as a SpawnWear app because it doesn't implement `ISpawnApp`; the app loader skips it ("no ISpawnApp implementer in assembly"). |
| **AppTemplate** | `AppTemplate/` | Fork-me starter scaffold. Renders "FORK ME!" centered. | Minimal valid `ISpawnApp` implementation with the full lifecycle. Read its [README](AppTemplate/README.md) to write your own. |

## Build them yourself

Each app has its own `.nfproj`. Build with MSBuild + the nanoFramework extension:

```
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" \
    DiceApp/DiceApp/DiceApp.nfproj -t:Build -v:m -p:Configuration=Debug -p:RestorePackages=false
```

Or open the SpawnWear solution in VS 2022 and build the project of choice.

The build produces `bin/Debug/<AppName>.pe` — typically 1-3 KB. Verify it's safe to runtime-load with `tools/check-pe-header.cs`:

```
dotnet run tools/check-pe-header.cs DiceApp/DiceApp/bin/Debug/DiceApp.pe
```

`flags = 0x00000000  no flags set` and `nativeMethodsChecksum = 0x00000000` means it has no native interop and will load cleanly on any SpawnWear firmware. If you see `c_Flags_NeedReboot SET`, the assembly references native interop the running firmware doesn't have - strip the offending reference.

## Constraints (read this before writing your own)

- **No native interop**. References must be limited to managed assemblies the firmware already loads (`mscorlib`, `nanoFramework.Graphics.Core`, `SpawnWear.exe`).
- **No threads**. Apps run on the firmware's UI thread via `Tick` / `OnTap` callbacks. Background work goes through services that own threads.
- **Reserve the system-chrome zones**. Don't draw in the top `IDisplayBuffer.StatusBarHeight` pixels (status bar) or the bottom `IDisplayBuffer.PageIndicatorHeight` pixels (page dots). The firmware overdraws those on every tick.
- **AMOLED black is free**. Solid black uses ~0 mA per off pixel; a fully-white screen at full brightness costs significant battery.

The full `ISpawnApp` lifecycle, `IServiceHost` accessors, and `IDisplayBuffer` drawing surface are documented in [`Plans/app-contracts-v1.md`](Plans/app-contracts-v1.md).
