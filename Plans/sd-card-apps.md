# SD-card-loadable Apps

The 32 GB microSD slot on the Waveshare watch makes "user-installable apps" a viable feature. The constraint is that nanoFramework's assembly loader resolves references at deploy time, not runtime - it can't `Assembly.Load(byte[])` a fresh DLL after boot the way desktop .NET can. So "install an app from SD" has to mean something more careful.

Phase 8 territory; this file is the design sketch so we know what we're aiming at.

## What "an app" actually is

An app is a registration entry the launcher consumes:

- **Tile metadata**: label, icon, optional badge count source, optional accent color
- **Screen factory**: a function that returns an `IScreen` when the tile is tapped
- **Required services**: list of system service interfaces the app needs to function

Built-in apps are hard-coded today in `Program.cs`. SD-card apps would arrive at boot via:

1. **Manifest scan** - launcher reads `/sd/apps/*/manifest.json` and registers metadata for each found app
2. **Code load** - the actual app behavior comes from one of three options listed below

## Three load strategies, ordered by feasibility

### Option A - Manifest-only "remote apps"

The app binary lives on TJ's PC; the watch only stores a manifest pointing to a URL. Tapping the tile opens a WebRTC / HTTP session to the PC, which streams a UI description (think VNC-lite or remote-Blazor) that the watch renders.

- **Pro**: works today, no nanoFramework loader changes needed
- **Pro**: PC-side apps can be heavy, complex, change frequently
- **Con**: useless when WiFi is down or the PC is off

This is what AI Assistant (Phase 7) already looks like under the hood. Generalizing it for arbitrary "remote apps" is mostly UI scaffolding.

### Option B - Bundled-firmware app slots

Each "installable app" is actually a managed assembly compiled into the firmware at build time, but the launcher only registers it if a corresponding marker file exists on the SD card. Installing = drop a 0-byte `enable` file in `/sd/apps/<name>/`. Uninstalling = delete it.

- **Pro**: works inside nanoFramework's loader constraints
- **Pro**: full native-side performance, no I/O on the hot path
- **Con**: doesn't actually let users add NEW apps, just toggle bundled ones. Not really "install."

Decent for Phase 8 v1 but not the long-term answer.

### Option C - Per-app firmware slots (the honest answer)

The deploy partition is 2.94 MB; a typical SpawnWear .pe assembly today is ~35 KB. We could carve the partition into N slots, each holding a single app's `.pe` plus metadata. "Installing an app" = OTA-write that slot. "Launching an app" = the launcher reboots into a different SpawnWear configuration where that slot's assembly is loaded.

- **Pro**: actually delivers user-installable apps
- **Con**: requires a custom nf-interpreter loader extension that can register an extra `.pe` after the standard deploy is committed
- **Con**: a tap-to-launch involves a CLR reboot, which is slow (~3 seconds) - feels weird for casual launches but acceptable for "open this dedicated app and spend a while in it"

This is a research project before it's a feature. Probably Phase 9 or later.

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

Pursue **Option B**. Concretely:

1. Add a `LauncherTileSource` interface and a `SdCardTileSource` that scans `/sd/apps/*/manifest.json`
2. Built-in apps get a static `BuiltInTileSource` so the launcher's tile list is the union of both sources
3. Tapping an SD-listed tile calls into the matching built-in screen factory if present, else shows a "not installed in this firmware" placeholder
4. Settings → Apps page shows the union and lets the user toggle individual entries

This unblocks "user-installable apps" feel without committing to per-app firmware slots before we know they're worth the complexity.

## Open questions

- **Discoverability**: how does someone get an app onto the SD card in the first place? Companion PWA upload? USB MSC mode? We probably need both.
- **Code-signing**: do we want it before user-installable apps ship? The watch is going to live on TJ's wrist; the threat model for accepting random `.pe` files isn't the same as a phone app store, but it isn't zero either.
- **Sandboxing**: a user-installed app gets the same access as a built-in one today. Phase 9+ might want capability-limited apps (e.g. "no WiFi access").
