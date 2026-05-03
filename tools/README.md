# tools/

Development tools for SpawnWear bring-up, written as `.NET 10` single-file C# scripts (`dotnet run path/to/script.cs`). Self-contained — `dotnet run` resolves the `#:package` directive at the top of each file and pulls the dependency on the fly.

## Daily dev loop

For routine code iteration the canonical path is **Visual Studio 2022 + nanoFramework extension + F5 / Build → Deploy Solution**. The watch must be in runtime mode (COM9 in our setup). See [`Notes/flashing.md`](../Notes/flashing.md) for the full discussion of why the bootloader-mode dance is NOT the right path for app re-deploys.

The tools below cover the CLI-driven cases — useful when iterating from a Claude Code agent, scripting CI smoke tests, or debugging without a VS GUI.

## nf-deploy.cs — deploy + capture from CLI (recommended for headless / agent use)

Pushes built `*.pe` assemblies to the watch over the wire protocol — the same path VS uses — and streams runtime `Debug.WriteLine` output back to stdout.

```bash
# From repo root, with watch on COM9 and VS debug stopped:
dotnet run tools/nf-deploy.cs

# Custom bin dir + COM port + capture seconds:
dotnet run tools/nf-deploy.cs SpawnWear/bin/Debug COM9 30
```

What it does, in order:
1. Discovers the watch on COM9 via `nanoFramework.Tools.Debugger.Net`.
2. Subscribes `OnMessage` BEFORE the deploy so the post-reboot `Main()` boot output is captured.
3. Reads every `*.pe` from the bin dir, calls `DebugEngine.DeploymentExecute(assemblies, rebootAfterDeploy: true)`. This is the wire-protocol command the runtime understands — it erases the deploy region, writes the new assembly set, validates checksums, then reboots the CLR.
4. Captures `Debug.WriteLine` for the configured window (default 25 s).

Build the project first via the .nfproj target — `nf-deploy.cs` does NOT build, it only deploys what is already in `bin/Debug/*.pe`.

```bash
# Rebuild from CLI without VS:
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" \
    SpawnWear/SpawnWear.nfproj -t:Rebuild -v:m -p:Configuration=Debug -p:RestorePackages=false
```

(Adjust the MSBuild path for your VS install. `RestorePackages=false` skips NuGet restore — only safe when the `packages/` tree is already populated. If a fresh clone or new dependencies, drop that flag.)

## nf-attach.cs — read-only inspection of a running runtime

Lists currently-deployed assemblies and polls `ExecutionMode` over the wire protocol. Does NOT redeploy or reboot. Good for: "is the watch reachable", "which assemblies are loaded", "what version of SpawnWear is on the chip."

```bash
dotnet run tools/nf-attach.cs              # COM9, 5s ExecutionMode poll
dotnet run tools/nf-attach.cs COM9 30      # COM9, 30s poll
```

**Caveat:** `ExecutionMode` from this CLI path is misleading — see the comment block at the top of `nf-attach.cs`. Use VS breakpoints for crash diagnosis, not this tool's `GetExecutionMode()` reading.

## ble-scan.cs — BLE advertisement scanner

(Not yet copied into this folder — currently lives in `%TEMP%`. Will be added as a tool when the SpawnWear PWA companion is wired up so the watch <-> PWA discovery flow can be smoke-tested without launching a browser.)

## Why not just bash / esptool?

`nf-flash-full.bat` (esptool writing the raw `SpawnWear.bin` to flash slot 0x1B0000) is the wrong path for app deploys — the user assembly bytes land on the chip, the wire protocol shows them as loaded, but `Main()` does not consistently run as expected (BLE adverts do not fire, externally-observable user code does not produce side effects). The wire-protocol `DeploymentExecute` path used by VS and `nf-deploy.cs` here uses the runtime's assembly loader properly. Reserve `nf-flash-full.bat` (or any esptool path) for nanoCLR runtime image updates and recovery — never for routine app deploys.

Documented in `Notes/flashing.md` → "Daily app development - F5 in VS, NO bootloader dance."
