# SD-card-loadable Apps

The 32 GB microSD slot on the Waveshare watch makes "user-installable apps" a viable feature. **nanoFramework DOES support runtime assembly loading** via `Assembly.Load(byte[])` - I was wrong about this in an earlier draft of this file. The native impl is at `targets/.../src/CLR/CorLib/corlib_native_System_Reflection_Assembly.cpp:277` and it does the full pipeline: parse the .pe header, link into the type system (`g_CLR_RT_TypeSystem.Link`), resolve references (`ResolveAll`), prepare for execution, and spawn static constructors.

**The one caveat**: if the loaded assembly has `CLR_RECORD_ASSEMBLY::c_Flags_NeedReboot` set in its header (line 983 of `nanoCLR_Types.h`), the load returns `CLR_E_BUSY` - those assemblies require a reboot to register. The flag is set on assemblies that contain native (non-managed) entry points - i.e. anything that calls into a hand-rolled C++ method via the native-stubs path. Pure managed code from a `.nfproj` should NOT have this flag set, so app assemblies that consume system services through the public managed surface (which is what we want anyway) load cleanly.

This means the architecture Gemini sketched in TJ's 2026-05-04 conversation is the correct shape: microkernel-style core firmware in flash, app payloads on the TF card, dynamic load via `Assembly.Load(byte[])`. Phase 8 territory; this file is the design sketch so we know what we're aiming at.

## What "an app" actually is

An app is a registration entry the launcher consumes:

- **Tile metadata**: label, icon, optional badge count source, optional accent color
- **Screen factory**: a function that returns an `IScreen` when the tile is tapped
- **Required services**: list of system service interfaces the app needs to function

Built-in apps are hard-coded today in `Program.cs`. SD-card apps would arrive at boot via:

1. **Manifest scan** - launcher reads `/sd/apps/*/manifest.json` and registers metadata for each found app
2. **Code load** - the actual app behavior comes from one of three options listed below

## Architecture: microkernel + app plugins

Inspired by TJ's 2026-05-04 design conversation with Gemini:

**Core firmware (internal flash, ~290 KB headroom under the deploy ceiling):**
- HAL / drivers (CO5300, FT3168, AXP2101, PCF85063, etc.) - nothing leaves
- System services (Power, WiFi, BLE, RTC, Audio, Storage, Logger, App Loader)
- UI Framework (drawing primitives, navigation, system widgets)
- The launcher
- Recovery apps (Settings, About, System Stats) - these MUST stay in flash so the watch is functional even if the SD card is removed, corrupted, or unmounted

**External apps (microSD card):**
- One folder per app: `/sd/apps/com.tj.calendar/`
  - `manifest.json` - {name, version, icon (optional), accent (color), required services}
  - `app.pe` - compiled managed assembly implementing `ISpawnApp`
  - `assets/` - app-private read-only resources (icons, sounds, localized strings)
  - `data/` - app-private read-write storage; persists across uninstalls if a `keep-data` marker file is present
- Apps are dynamically loaded via `Assembly.Load(byte[])` when the user taps the tile
- Apps are unloaded when the user navigates away (assembly + heap freed)

**Shared contracts** in a small `SpawnWear.AppContracts.dll` that both the firmware AND every app reference:
- `ISpawnApp` - lifecycle interface (`OnCreate(IServiceHost services)`, `OnResume`, `OnPause`, `OnDestroy`, `Tick(deltaMs)`, `OnTap(x, y)`)
- `IServiceHost` - the surface apps use to ask for `IPowerService`, `IRtcService`, `IDisplayBuffer`, etc.
- The framework owns the renderer; apps call `IDisplayBuffer.FillRectangle / DrawString / Flush` rather than bringing their own font.

App load + launch flow:

1. Launcher boot - scan `/sd/apps/*/manifest.json`, register tile metadata in the launcher's tile list
2. User taps tile - launcher shows a "Loading..." transition (Gemini's suggestion - hides the SPI read latency)
3. Read `/sd/apps/<name>/app.pe` into a byte[]
4. Wrap in try/catch. `var asm = Assembly.Load(payload);`
5. Find the type implementing `ISpawnApp`, instantiate it, call `OnCreate(serviceHost)`, then `OnResume`
6. Push the app's `IScreen`-equivalent onto the navigation stack
7. On exit, `OnPause` → `OnDestroy`, drop the reference, let the GC reclaim the assembly + its heap

## Constraints we need to verify before betting on this

- **Assembly unloading**: `Assembly.Load(byte[])` works, but does the runtime actually free the assembly's metadata when the last reference goes away? Need to test by loading + unloading the same app 100x and watching the managed heap. If it leaks, "launching the same app multiple times" becomes a slow death.
- **Native-method limits**: per `c_Flags_NeedReboot` (line 983 of `nanoCLR_Types.h`), assemblies with native entries need reboot-to-load. We need to confirm that an app built only against managed assemblies (`mscorlib`, `nanoFramework.Graphics`, etc.) does NOT get this flag. The flag is set by the build/metadata stage, so the test is: build a tiny "hello world" app, inspect the .pe header bytes, see if the flag is clear.
- **Cross-assembly calls**: when our core firmware exposes `IServiceHost` from `SpawnWear.AppContracts`, an app loaded at runtime invokes a method on that interface. That call has to traverse the assembly boundary. nanoFramework's type system handles this for built-in assemblies; we need to confirm it works for runtime-loaded ones too.
- **Same-version requirement**: an app built against `SpawnWear.AppContracts v1.0` may or may not work against firmware running v1.1, depending on whether the runtime checks struct layouts strictly. Need to design `ISpawnApp` to be genuinely stable, with extensibility through optional capability interfaces (e.g. `IAppHasNotifications`) rather than method additions to the base contract.

## Performance + UX

- **Asset caching** (Gemini's idea, good): launcher caches app icons + tile metadata in a single binary file in internal flash so the home screen doesn't re-scan the SD tree on every navigation back to launcher. Cache invalidates on app install / uninstall (manifest mtime changes).
- **Loading indicator**: tapping a tile flips to a "Loading..." screen immediately; the actual SPI read + Assembly.Load happens behind that. Even a 200 ms load feels less janky if there's a transition than if the UI freezes.
- **Same-thread execution**: apps run on the framework's UI thread (via `Tick` + `OnTap` callbacks). They don't get their own thread. Background work goes through services. This matches Android's UI-thread model and avoids the most common class of plugin bugs (concurrent access to the framebuffer).
- **Crash isolation**: every call into an app goes through `try / catch (Exception)`. An exception from an app surfaces as a one-shot toast + a return-to-launcher; the firmware never dies for an app's bug.

## SD card layout (regardless of strategy)

```
/sd/
  apps/
    com.tj.calendar/
      manifest.json       <- {name, version, icon, requires: [Storage, RTC]}
      icon.png            <- 96x96 PNG, optional (falls back to a generic tile)
      payload.pe          <- Option C only; the actual managed assembly
      data/               <- app-private storage, persists across uninstalls if marker file present
    com.aubs.draw/
      manifest.json
      icon.png
      data/
        sketches/
  system/
    log/
      2026-05-04.log
      2026-05-03.log
    settings.json         <- system service config, mirrored to internal flash
```

## V1 scope (Phase 8 first try)

In order:

1. **Verify the runtime constraints** above with a tiny throwaway harness - loading + unloading + reloading a "hello world" app, watching for leaks and `c_Flags_NeedReboot` issues
2. **Define `SpawnWear.AppContracts.dll`** - `ISpawnApp` interface + `IServiceHost` + the small set of capability interfaces (rendering, RTC, storage). Check it into the SpawnWear repo as a separate `.nfproj` so apps can take it as a NuGet reference.
3. **Implement `AppLoader` system service** in the core firmware - scans `/sd/apps/*/manifest.json` at boot, exposes registered apps to the launcher, dynamically loads + unloads on demand.
4. **Convert one built-in app to live on SD** as a smoke test (Stats or Activity is a good candidate - simple, no audio / WebRTC complexity).
5. **First user-facing release**: drop a sample `com.tj.helloworld.pe` on the SD card, document the build pipeline, demo it on the watch.

## Open questions

- **Discoverability**: how does someone get an app onto the SD card in the first place? Companion PWA upload over BLE? USB MSC mode? Initial answer: copy the `.pe` to `/sd/apps/<name>/app.pe` while the SD card is mounted on a host PC. Long term: PWA-driven OTA install via `WriteValueAsync` chunks over BLE GATT.
- **Code-signing**: do we want it before user-installable apps ship? The watch is going to live on TJ's wrist; the threat model for accepting random `.pe` files isn't the same as a phone app store, but it isn't zero either. Probably ship V1 without signing, add it before "user-contributed apps via the install path" lands in Phase 9.
- **Sandboxing**: a user-installed app gets the same access as a built-in one today. Phase 9+ might want capability-limited apps (e.g. "no WiFi access"). The `IServiceHost` design should make this expressible: a capability-restricted host hands out a subset of services.
- **App-side dependencies**: if every app could bring its own NuGet refs, the SD card becomes a dependency-hell minefield. V1 says: apps may only reference `SpawnWear.AppContracts.dll` + the specific managed assemblies the firmware already loads. No transitive dependencies. Apps build clean against a known firmware version's contract surface or they fail to load.

## What WAS in this file but was wrong

An earlier version of this file said `Assembly.Load(byte[])` doesn't work in nanoFramework. That was based on a bad recollection rather than reading the source. **It does work** - see `corlib_native_System_Reflection_Assembly.cpp::Load___STATIC__SystemReflectionAssembly__SZARRAY_U1` in the LostBeard nf-interpreter fork. Rule 4b violation - corrected here so the next reader doesn't propagate the wrong assumption.
