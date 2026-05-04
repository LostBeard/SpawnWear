# Development Loop

Two paths: F5 in Visual Studio (everyday code iteration) and CLI via `tools/nf-deploy.cs` (headless / agent / smoke testing).

## Daily dev loop - F5 in VS

Open `SpawnWear.slnx` in Visual Studio 2022 with the [.NET nanoFramework extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension) installed and press **F5**.

The watch must already have the nanoFramework runtime flashed (first-time install: see `Notes/flashing.md`). For routine app development, the watch is in **runtime mode** on COM9. NO bootloader-mode dance is needed.

Cycle time: ~10 seconds build → deploy → app running with breakpoints active.

`Debug.WriteLine` output streams to the VS Output window. Step-through debugging works.

## CLI dev loop - `tools/nf-deploy.cs`

For headless work (agent sessions, CI smoke tests, scripts that don't have a VS GUI):

```bash
# Build with MSBuild (skip if VS already produced the .pe files)
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" \
    SpawnWear/SpawnWear.nfproj -t:Build -v:m -p:Configuration=Debug -p:RestorePackages=false

# Deploy and capture 25 s of Debug.WriteLine output
dotnet run tools/nf-deploy.cs

# Custom bin dir + COM port + capture seconds:
dotnet run tools/nf-deploy.cs SpawnWear/bin/Debug COM9 30
```

What it does, in order:
1. Discovers the watch on COM9 via `nanoFramework.Tools.Debugger.Net`
2. Loads the VS-bundled debugger DLL via `Assembly.LoadFrom + reflection` (the nuget-published 2.4.x / 2.5.x versions hard-fail on runtimes that report `IncrementalDeployment=False`, which our custom ESP32_S3_BLE_QSPI runtime does)
3. Subscribes `OnMessage` BEFORE the deploy so the post-reboot `Main()` boot output is captured
4. Reads every `*.pe` from `bin/Debug` + active package references, runs the deploy ceiling pre-flight, calls `DebugEngine.DeploymentExecute(assemblies, rebootAfterDeploy: true)`
5. Captures `Debug.WriteLine` for the configured window (default 25 s)

## CLI tools reference

Each tool is a `.NET 10` single-file C# script. Run with `dotnet run path/to/script.cs`. Self-contained - dependencies resolved at run time via the `#:package` directive.

| Tool | Purpose |
|---|---|
| `tools/nf-deploy.cs` | Deploy + capture from CLI (the daily CLI loop) |
| `tools/nf-attach.cs` | Read-only inspection of a running runtime (assemblies, ExecutionMode) |
| `tools/check-deploy-size.cs` | Pre-flight ceiling check; bails if total `.pe` >= 290 KB. See `Research/nf-interpreter-deploy-ceiling.md` |
| `tools/bin-to-png.cs` | Convert `/screenshot.bin` HTTP response (RGB565 BE) to PNG |
| `tools/nf-graphics-repack.cs` | Rebuild the three local-feed graphics nupkgs in one shot |
| `tools/nf-flash-full.bat` | Bootloader-mode full reflash via esptool. Recovery / first install only |
| `tools/nf-build.bat` | Old wrapper around the MSBuild command above |

## Bootloader-mode operations (rare)

| Scenario | Why |
|---|---|
| First-time install on a virgin watch | No nanoFramework runtime to talk to yet |
| Runtime image update (`nanoff --update`) | The CLR is rewriting itself; ROM bootloader handles that |
| Custom nf-interpreter build via esptool | Same - runtime + bootloader + partition table rewrite |
| Recovery after wedged wire protocol | The CLR is too sick to receive an app diff; bail to a full reflash |

```bash
nanoff --target ESP32_S3_BLE --serialport COM10 --update --masserase   # First-time install on virgin watch
nanoff --target ESP32_S3_BLE --serialport COM10 --update               # Runtime update / version pin
```

The COM port number changes between bootloader mode (COM10 in our setup) and runtime mode (COM9). Re-run `nanoff --listports` whenever a port "disappears". Bootloader mode requires the chip to be in download mode (hold BOOT during cold boot via PWR power-cycle). Full first-flash recipe with every gotcha is in `Notes/flashing.md`.

## Companion PWA dev loop

For working on `SpawnWear.Companion` (Blazor WASM PWA) and `SpawnWear.Bridge` (RCL):

```bash
# Build + serve the PWA on http://localhost:5251
dotnet run --project SpawnWear.Companion -- --urls http://localhost:5251

# Run the wire-format regression suite
dotnet test SpawnWear.Bridge.Tests
```

To pair a real watch from the dev server:

1. Watch must be powered on, BLE advertising as `SW-OK-Tok`.
2. Open `http://localhost:5251/` in **Chrome / Edge / Opera on desktop or Android** (Web Bluetooth not available in Safari / Firefox).
3. Click **Pair watch** on the Home page → browser shows a Bluetooth picker filtered to SpawnWear devices → select the watch.
4. The Home page lights up with live battery / IMU / button / log cards. The other pages (`Stats`, `WiFi`, `Mirror`, `Apps`, `Console`) all share the same `BridgeClient` and surface their channels.

Hot-reload works for `.razor` and `.css` edits. `BridgeClient` event subscriptions persist across navigations because the client is a scoped DI service - no re-pair needed on page change.

If you change a wire format in `SpawnWear/BleUuids.cs` or any firmware-side `Notify*` producer, run `dotnet test SpawnWear.Bridge.Tests` first. The `BleUuidsParityTest` will fail until you mirror the change to `SpawnWear.Bridge/BleUuids.cs`. The channel decoder tests will fail until you update both the firmware schema and the Bridge decoder.

## Live screen capture over WiFi

When WiFi is up + HTTP server is listening (port 8080 by default):

```bash
# Fetch raw RGB565 BE framebuffer
curl -s -o screenshot.bin http://192.168.1.171:8080/screenshot.bin

# Convert to PNG
dotnet run tools/bin-to-png.cs screenshot.bin screenshot.png

# Or visit http://192.168.1.171:8080/ in a browser for the JS-driven canvas viewer
```

This replaces the older BOOT-button-base64-over-Debug.WriteLine path. WiFi credentials live in `SpawnWear/Config/WifiCredentials.cs` (gitignored).

## Build flags + version compatibility

Two important gotchas - full details in `Notes/flashing.md`:

- **Matched runtime + library combo (2026-04-28).** Runtime image **ESP32_S3_BLE 1.16.0.563** + stable 1.x class libraries with **`nanoFramework.System.Net` bumped to 1.11.50** (the latest stable). 1.11.47 lags the runtime by one System.Net native patch. The 2.0.0-preview library line is currently AHEAD of every released runtime, so it is unusable today.

- **Local NuGet feed for graphics.** Three local-feed packages (`nanoFramework.Graphics`, `.Graphics.Core`, `.Graphics.Co5300`) live at `D:/users/SpawnDevPackages/` at version `2.0.0-spawnwear.2`. They wrap the LostBeard fork of `nanoFramework.Graphics` and the matching native bits in our custom `nf-interpreter` build. `tools/nf-graphics-repack.cs` rebuilds them in one shot.
