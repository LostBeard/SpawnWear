# SpawnWear App Template

Starter scaffold for writing a SpawnWear app. Apps are pure-managed C# assemblies that implement `SpawnWear.AppContracts.ISpawnApp`. The firmware loads them at runtime via `Assembly.Load(byte[])` and runs them as foreground screens.

## Quickstart

1. Copy this folder anywhere outside the SpawnWear repo.
2. Rename the folder, project file, namespace, and class to your app's name.
3. Edit `MyApp.cs` - in particular the `Name` property and the `Render` method.
4. Build with MSBuild + the nanoFramework extension: `dotnet build` won't work because the nanoFramework targets aren't on the public NuGet path; use Visual Studio 2022 with the [nanoFramework extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension), or invoke MSBuild directly:

    ```
    "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" YourApp.nfproj -t:Build -v:m -p:Configuration=Debug -p:RestorePackages=false
    ```

5. The build produces `bin/Debug/YourApp.pe` — typically 1-3 KB.
6. Verify the .pe is safe to runtime-load by checking its header flags are 0:

    ```
    dotnet run path/to/SpawnWear/tools/check-pe-header.cs YourApp.pe
    ```

   `flags = 0x00000000  no flags set` means it's loadable. If you see `c_Flags_NeedReboot SET`, your app references native interop the running firmware doesn't have — strip the offending reference.

7. Install the `.pe` on the watch's SD card under `D:\apps\YourApp\` - either copy it from a desktop with the card out, or push it over the WebRTC `sys.files` lane (see [`Docs/transport.md`](../../Docs/transport.md)). On the next mount/reboot the launcher shows a tile for your app; tap it to run. (The old `http://<watch-ip>:8080/` drop-zone is retired - the firmware no longer runs an HTTP server.)

## What lives in `MyApp.cs`

The lifecycle methods, in call order:

| Method | When | What to do |
|---|---|---|
| ctor | App is loaded | Nothing expensive - the constructor runs at activation time |
| `OnCreate(IServiceHost)` | Once after load, before any rendering | Capture the services reference; allocate long-lived state; return false to refuse activation |
| `OnResume(IDisplayBuffer)` | App becomes the foreground screen | Repaint the framebuffer; the panel is in Active state |
| `Tick(IDisplayBuffer)` | While app is foreground; ~1 Hz idle, ~60 Hz while finger held | Redraw if state has changed; return promptly (no spinning) |
| `OnTap(x, y)` | User taps the screen | Mutate state + flag dirty; return true to consume |
| `OnPause` | User navigated away | Stop timers, drop transient state, but keep service refs |
| `OnDestroy` | App is being unloaded | Drop service references; final cleanup |

## What you can do via `IServiceHost`

- `GetPower()` - battery percent, mV, USB-VBUS state
- `GetRtc()` - current date/time + weekday
- `GetWifi()` - is connected, IP, SSID
- `GetLogger()` - Info / Warn / Error wrappers around Debug.WriteLine
- `GetDisplay()` - same `IDisplayBuffer` your `OnResume` / `Tick` get

The full contract surface lives in `SpawnWear/AppContracts/` in the firmware repo and the design doc is at `SpawnWear/Plans/app-contracts-v1.md`.

## Constraints

- **No native interop in your app**. References must be limited to managed assemblies the firmware already loads (mscorlib, nanoFramework.Graphics.Core, SpawnWear.exe).
- **No threads**. Apps run on the firmware's UI thread via `Tick` / `OnTap` callbacks. Background work goes through services that own threads.
- **Don't draw in the status bar zone** (top `StatusBarHeight` pixels) **or page indicator zone** (bottom `PageIndicatorHeight` pixels). The firmware overdraws those on every tick.
- **Even/odd alignment for partial flushes is automatic** - the firmware applies the CO5300 quirk inside `Bitmap.Flush(x, y, w, h)`. Pass any rectangle to `IDisplayBuffer.Flush(x, y, w, h)`.
- **AMOLED black background = ~0 mA per off pixel.** Tinted backgrounds cost battery proportional to brightness. Use solid black where it doesn't hurt UX.
