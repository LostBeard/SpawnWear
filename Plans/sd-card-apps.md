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

## Constraints verified by reading nf-interpreter source

(Done 2026-05-04 against `D:/users/tj/Projects/SpawnWear/_vendor-nf-interpreter/`. Updated from "we need to verify" to "this is what the code does.")

### 1. Assembly unloading - no public API; reboot is the only full reclaim

- **`CLR_RT_Assembly::DestroyInstance()`** exists (`Core/TypeSystem.cpp:1885`). It (a) clears the type-system slot via `g_CLR_RT_TypeSystem.m_assemblies[m_idx - 1] = NULL`, (b) frees the header memory IF the `FreeOnDestroy` flag is set (`m_flags & 0x100`), and (c) appends the assembly object to the event cache for recycling.
- **It is called from two places**: the Load() error path (`CorLib/corlib_native_System_Reflection_Assembly.cpp:395`, only on failure) and `CLR_RT_TypeSystem::TypeSystem_Cleanup()` (`Core/TypeSystem.cpp:3438`, only at full CLR shutdown).
- **No public managed-side API** wraps DestroyInstance for a successfully-loaded assembly. Gemini was right that there's no `Assembly.Unload()`.
- **`FreeOnDestroy` is NOT set on byte[]-loaded assemblies.** The Load() native impl rooted the byte[] in `assm->m_pFile = array;` (`corlib_native_System_Reflection_Assembly.cpp:319`) and never sets the flag. So even if DestroyInstance were called, the header memory stays alive (it's GC-managed via the byte[], not malloc-allocated). `CLRStartup.cpp:306` is the only place that DOES set `FreeOnDestroy`, and only for non-XIP startup loads.
- **Cross-reference dangling-pointer concern**: real but bounded. Other assemblies' `m_pCrossReference_AssemblyRef[i].m_target` would point to a recycled CLR_RT_Assembly. `NANOCLR_FOREACH_ASSEMBLY` iteration skips NULL slots in `m_assemblies[]`, so type-system traversal stays safe; but specific resolved cross-references would dangle. Don't unload an assembly that other live assemblies reference.

**Implication for the V1 plan**: a "Soft Reboot (ClrOnly)" cycle is the only full-reclaim path. Apps can be loaded freely until heap pressure builds up, at which point a reboot is needed. UI design should treat reboot as cheap (~3 s) and explicit ("Restart the watch to reclaim memory") rather than something to hide.

### 2. `c_Flags_NeedReboot` - returns CLR_E_BUSY, not a forced reboot

- **Actual behavior** (`corlib_native_System_Reflection_Assembly.cpp:312-315`): `if (header->flags & CLR_RECORD_ASSEMBLY::c_Flags_NeedReboot) NANOCLR_SET_AND_LEAVE(CLR_E_BUSY);`. That's an HRESULT failure, propagated as an exception to managed code. NOT a forced reboot. NOT a "LinkFailure".
- **The managed caller decides what to do**: catch the exception, prompt the user, save a "pending app" pointer, call `Power.RebootDevice(RebootOption.ClrOnly)`. None of that is built in - we'd have to write it.
- **What sets the flag in the .pe header**: the build pipeline sets `c_Flags_NeedReboot` when the assembly contains native interop method definitions whose checksum doesn't match the running CLR's compiled-in native methods table. Pure managed app code referencing only managed surfaces should NOT have this flag.
- **Verification step**: build a "hello world" `.pe` for the watch (managed-only, references `SpawnWear.AppContracts.dll` only), dump the first 32 bytes of the file, check the `flags` field at offset 16 (`CLR_RECORD_ASSEMBLY` layout: 8-byte marker, 4-byte headerCRC, 4-byte assemblyCRC, then 4-byte flags). Bit 0 should be clear.

### 3. Name collision - the loader does NOT dedupe; both copies link

- **Gemini's claim "loader returns the existing handle" is WRONG for the Assembly.Load(byte[]) path.** Looking at `corlib_native_System_Reflection_Assembly.cpp:317-321`: `CreateInstance(header, assm)` followed immediately by `g_CLR_RT_TypeSystem.Link(assm)`. There is NO `FindAssembly()` call before linking.
- **`Link()` itself** (`Core/TypeSystem.cpp:3454`) iterates `NANOCLR_FOREACH_ASSEMBLY_NULL` to find the first NULL slot and stores the new pointer there. Both the existing AND the newly-loaded assembly end up in `m_assemblies[]`, in different slots.
- **`FindAssembly(name, version, exact)`** (`Core/TypeSystem.cpp:3486`) iterates and returns the FIRST match - so subsequent type-resolution lookups would consistently pick whichever assembly happens to be in the lower-indexed slot. Two distinct CLR_RT_Assembly instances exist, but only one is reachable via name lookup.
- **AppDomain-level deduplication DOES exist** (`Core/TypeSystem.cpp:2284-2286`: `if (FindAppDomainAssembly(assm) != NULL) return S_OK;`), but that's at a higher layer that the Load(byte[]) path doesn't traverse for its initial Link.
- **`CLR_E_ASSM_WRONG_CHECKSUM`** (`nf_errors_exceptions.h:83`) is for native-interop checksum mismatches at deploy-time, not for name collisions. Different code path.

**Implication**: don't rely on the loader to dedupe. The launcher's app loader MUST call `Assembly.GetAssemblies()` (or equivalent metadata API), check for an existing match by name+version, and either skip the load or fail the install BEFORE handing bytes to the CLR.

### 4. Same-version contract design

- An app built against `SpawnWear.AppContracts v1.0` may load against firmware running v1.1 IF the version comparison in `FindAssembly` accepts non-exact matches. The `fExact` parameter (line 3500) is what controls this.
- For interfaces specifically, the contract surface is the method signatures recorded in the .pe metadata. Adding a method to `ISpawnApp` would change the interface's metadata and potentially break apps built against the older contract.
- **Design rule**: `SpawnWear.AppContracts.dll` must be APPEND-ONLY at the type level. Existing methods on `ISpawnApp` never change. New capabilities arrive as new interfaces (`IAppHasNotifications`, `IAppHasBackgroundService`) that the firmware checks via `is` casts at runtime.

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

## Empirical validation 2026-05-05

The full dynamic-load + invoke path verified end-to-end on the watch:

1. Built a minimal `HelloWorldApp.pe` (416 bytes, managed-only, references mscorlib only). `tools/check-pe-header.cs` confirmed `flags = 0` and `nativeMethodsChecksum = 0`.
2. Added a `POST /loadpe` HTTP endpoint to SpawnWear's `HttpServer.cs` that reads the request body as a byte[], runs `System.Reflection.Assembly.Load(byte[])`, finds the type `HelloWorldApp.HelloWorldPayload`, invokes static method `Greet()` via reflection, returns the result string in the HTTP response.
3. POSTed the .pe file via curl: `curl -X POST --data-binary @HelloWorldApp.pe http://192.168.1.171:8080/loadpe`
4. Watch responded: `OK: Hello from SD-card-loadable app, watch is at 05/04/2026 12:20:29`

This confirms the architectural assumptions:
- `Assembly.Load(byte[])` works at runtime on the LostBeard nf-interpreter fork as deployed
- Pure-managed assemblies (flags = 0) load cleanly without `CLR_E_BUSY`
- Loaded assemblies are reachable via `assembly.GetType("...")`
- Static methods on loaded types are invocable via `MethodInfo.Invoke(null, null)`
- Return values cross the assembly boundary correctly

**Phase 8 is unblocked.** The full app loader can be built on top of this foundation with confidence the foundation is real.

What remains untested:
- Assembly unload + heap reclaim across many load cycles (Gemini's "metadata never reclaimed" claim - needs a 100x load test)
- Cross-assembly interface calls (loaded app implementing `ISpawnApp` from the firmware-deployed AppContracts)
- Same-name+version collision behavior on a real load attempt
- Loading from SD card (FileSystem package not yet on the firmware)
