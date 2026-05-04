# SpawnWear Apps

Each subfolder in this repo's root that ends in `App` is a standalone, dynamically-loadable SpawnWear app. They're separate `.nfproj`s that reference the `SpawnWear.AppContracts` interface surface (which lives inside `SpawnWear/AppContracts/` in the firmware project today; will graduate to a separate NuGet package in Phase 8).

## Try them

Two paths - the watch's built-in HTML page or the SpawnWear.Companion PWA.

**Watch built-in page** (zero install):

1. Make sure the watch is on WiFi and you know its IP. Boot the firmware, look for `[SpawnWear] HTTP at http://<ip>:8080/` in the deploy log, or read it off the **WIFI** tile.
2. Open `http://<watch-ip>:8080/` in any browser on the same network.
3. Drag any `.pe` from the table below onto the drop zone, or click "browse" to pick one.
4. Watch responds `OK: <APP NAME>` and the screen-mirror canvas refreshes with the app rendering.
5. Tap the **APP** tile on the launcher itself to interact.
6. Long-press anywhere on the watch screen → return to the launcher.

**SpawnWear.Companion PWA** (richer surface, BLE pairing + WiFi mirror):

1. `dotnet run --project SpawnWear.Companion -- --urls http://localhost:5251` from this repo, or visit a hosted Companion build.
2. On the Home page click **Pair watch** → browser shows the Bluetooth picker → select `SW-OK-Tok`.
3. Navigate to **Apps** in the sidebar. The watch URL auto-fills from the WiFi-status notify the watch sends after pairing.
4. Drop a `.pe` (or click to pick one). It POSTs to `/loadapp` over WiFi with the same outcome as path 1.
5. Switch to **Mirror** to watch the app render live; **Console** shows the watch's `Debug.WriteLine` stream.

## App library

| App | Folder | What it does | Demonstrates |
|---|---|---|---|
| **CounterApp** | `CounterApp/` | Tap to increment a counter. Big number centered. | Basic state mutation; `OnTap`; full-frame redraw |
| **DiceApp** | `DiceApp/` | Tap to roll a six-sided die. Classical pip layout. | Random source; complex rendering with helper functions |
| **PaintApp** | `PaintApp/` | Tap to paint colored dots on a black canvas. 8-color palette cycles per tap. | `OnTap` with non-trivial side effects; partial flush of just the dirty rectangle; respecting status-bar / page-indicator zones |
| **HelloWorldApp** | `HelloWorldApp/` | Smoke-test only - exposes a static `Greet()` method instead of implementing `ISpawnApp`. | Pure `Assembly.Load(byte[])` + `MethodInfo.Invoke` happy path. **Won't activate** as a SpawnWear app because it doesn't implement `ISpawnApp`; the `/loadapp` endpoint returns "no ISpawnApp implementer in assembly". |
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
