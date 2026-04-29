# Flashing the nanoFramework Runtime

Step-by-step recipe to put the .NET nanoFramework runtime on a brand-new Waveshare ESP32-S3-Touch-AMOLED-2.06 watch, with every gotcha we hit so the next person doesn't.

## Prerequisites

- **Windows machine** (Linux / macOS work too but path examples below are Windows).
- **`nanoff` CLI** - `dotnet tool install -g nanoff` (we used 2.5.131; tool reports a newer version on every run, take the update if you see it).
- **USB-C cable** - any data cable; the watch has native USB-OTG off the ESP32-S3, so no special USB-to-UART adapter needed.

## The connection - native USB-CDC, two COM ports

The ESP32-S3 has native USB. Depending on which mode it is in, **Windows assigns a different COM port number**. Both ports are the same physical USB-C connector - the chip enumerates differently in each mode.

| Mode | What is running | Typical USB-CDC class | Example port |
|---|---|---|---|
| Bootloader | ESP32 ROM-resident bootloader (esptool target) | "USB Serial Device" | COM10 |
| Runtime | nanoFramework CLR (or any user firmware) | "USB Serial Device" | COM9 |

**The port number can flip every time the firmware reboots into a different state.** This is normal, not a fault. Re-run `nanoff --listports` whenever a `Could not find file 'COMx'` error appears.

```
> nanoff --listports
Available COM ports:
  COM1
  COM10
```

If you see two ports and only one is the watch, the other is usually a built-in `COM1` from the motherboard. Unplug the watch and re-list to identify which one disappears.

## Step 1 - confirm the chip is the right one

Read chip details from ROM bootloader (this works **before** you flash anything):

```
nanoff --serialport COM10 --platform esp32 --devicedetails
```

Expected output for our watch:

```
Connected to:
ESP32-S3 (ESP32-S3 (QFN56) (revision v0.2))
Features Wi-Fi, BT 5 (LE), Dual Core + LP Core, 240MHz, Embedded PSRAM 8MB (AP_3v3)
Flash size 32MB unknown from GIGADEVICE (manufacturer 0x200 device 0x16409)
PSRAM: 8MB
Crystal 40MHz
MAC <unique to your unit>
```

**Confirm:** `ESP32-S3R8` family (8MB embedded PSRAM, 32MB external GigaDevice flash). If the chip family is different, **stop** - you are on the wrong board, do not flash. Other Waveshare AMOLED watches (1.8 / 1.91 / 2.41) and the C6 variant have different chip layouts and require different targets.

### Gotcha - "Another application has exclusive access to the device"

If you see:

```
Error E6002: Couldn't access serial device. Another (nanoFramework) application has exclusive access to the device.
```

something else is holding the COM port. Most common culprits:

- **Visual Studio** with the nanoFramework extension installed - the "Device Explorer" pane auto-attaches to any nanoFramework or ESP32 device on a USB-CDC port
- **VS Code** with a serial monitor open
- A previous `nanoff` invocation that crashed or got stuck
- PuTTY / Tera Term / any terminal emulator with the port open

Close the holder, then retry. There is no need to unplug the watch.

## Step 2 - flash the runtime

For a brand-new watch (factory Waveshare demo firmware, no nanoFramework partition) the canonical command is:

```
nanoff --target ESP32_S3_BLE --serialport COM10 --update --masserase
```

Why these flags:

| Flag | Why |
|---|---|
| `--target ESP32_S3_BLE` | The 2.06 watch needs both BLE and the USB-CDC variant; `ESP32_S3` (no BLE) and `ESP32_S3_ALL` (adds Ethernet we don't have) are wrong |
| `--update` | "Install or update" - same flag works on a virgin board and on subsequent updates |
| `--masserase` | Erases all flash before writing. **Required on first install** because nanoff's "Backup configuration" step fails on a board that has never run nanoFramework (there is no config partition to back up - see gotcha below) |

A successful run finishes with three "Hash of data verified" lines:

```
Wrote 19488 bytes (12630 compressed) at 0x00000000 in 0.3 seconds (599.6 kbit/s).
Hash of data verified.                            <-- bootloader

Wrote 1370768 bytes (940308 compressed) at 0x00010000 in 9.8 seconds (1117.4 kbit/s).
Hash of data verified.                            <-- firmware

Wrote 3072 bytes (136 compressed) at 0x00008000 in 0.1 seconds (236.1 kbit/s).
Hash of data verified.                            <-- partition table
```

After all three are verified, **the flash is complete**, even if you see the cosmetic error described in the next section.

### Gotcha - "Backup configuration... Error E4000: Error executing esptool command. (The handle is invalid.)"

On a brand-new board, **without** `--masserase`, the flash fails very early with:

```
Backup configuration...
Error E4000: Error executing esptool command. (The handle is invalid.)
```

This is nanoff trying to read an existing nanoFramework configuration partition that does not exist on a Waveshare-factory watch, and esptool's USB-CDC handle going invalid mid-step. **`--masserase` skips the backup attempt entirely** and proceeds straight to the erase + write path. Use it on first install. Subsequent updates do not need it.

### Gotcha - "Hard resetting via RTS pin... Error E4000" at the very end

Even with a successful `--masserase` flash, the LAST line of output is:

```
Hard resetting via RTS pin...
Error E4000: Error executing esptool command. (The handle is invalid.)
```

**This is cosmetic.** The flash is already complete by the time esptool gets to the post-flash hard reset. The reset fails because the watch's USB-CDC re-enumerates between the "send reset" and "read response" steps - the COM port the handle pointed at no longer exists by the time the reset reply arrives. Ignore this error. The runtime is on the chip.

## Step 3 - verify the runtime is running

After flashing, the watch reboots into the nanoFramework CLR. The COM port number **will change** because the runtime exposes a different USB-CDC class identifier than the bootloader.

```
> nanoff --listports
Available COM ports:
  COM1
  COM9              <-- was COM10 before flashing, now COM9
```

To prove the runtime is alive, try the chip-details command on the new port:

```
nanoff --serialport COM9 --platform esp32 --devicedetails
```

You will see:

```
A fatal error occurred: Failed to connect to Espressif device:
Invalid head of packet (0x4E): Possible serial noise or corruption.
```

**This error is the proof.** `0x4E` is the start byte of a nanoFramework debug-protocol packet (`'N'`). esptool only knows the ESP32 ROM bootloader protocol and has no idea what nanoFramework's packet stream means, so it bails. Seeing `0x4E` means the runtime is up and chatting on USB-CDC. If you saw the bootloader instead (silence, or `0xC0` SLIP framing), the flash failed.

To talk to the running runtime properly, use:

- **Visual Studio 2022+** with the [.NET nanoFramework extension](https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension) - Device Explorer pane lists the watch with firmware version
- A serial terminal at **921600 baud, 8N1** - reads `Debug.WriteLine` output

## Step 4 - deploying the SpawnWear assembly

Once the runtime is on the watch:

1. Open `SpawnWear.slnx` in Visual Studio 2022 with the nanoFramework extension installed.
2. The Device Explorer pane should list the watch as `ESP32_S3_BLE` on the new COM port.
3. Right-click the `SpawnWear` project → "Set as Startup Project".
4. F5 to deploy + run, or Ctrl+F5 to deploy without attaching the debugger.
5. The first deploy uploads ~10 small managed assemblies; subsequent deploys are diff-only.

`Debug.WriteLine` output streams to the Output pane, and to any external serial terminal at 921600 baud on the same COM port (only one process can hold the port at a time).

## Re-flashing while the runtime is already on the chip

Once the chip is running nanoFramework, **`nanoff --update` cannot reach the bootloader on its own** for native-USB ESP32-S3 boards. esptool talks to the ROM bootloader; the runtime exposes a different USB-CDC endpoint and answers its own debug protocol. nanoff trying to chip-detect against the runtime port returns:

```
A fatal error occurred: Failed to connect to Espressif device:
Invalid head of packet (0x4E): Possible serial noise or corruption.
```

That `0x4E` IS the proof the runtime is alive (it is the start byte of a nanoFramework packet), but esptool cannot interpret it.

### Buttons on this watch (important - there is no separate RESET button)

Both buttons are on the **right edge** of the case:

- **Top-right button = BOOT** (wired to GPIO0).
  - Held during chip power-up: ROM enters download mode (USB-Serial-JTAG bootloader).
  - Pressed during runtime: a normal user button event (GPIO0 goes low while pressed).
- **Bottom-right button = PWR** (toggles the watch's main power through the AXP2101 PMIC).
  - Tap (short press) when off: power on.
  - Hold 6+ seconds when on: power off (AXP2101 cuts every rail).

If you press BOOT while the chip is already booted into runtime, **nothing changes** - it is just a user-button event. To enter the bootloader you have to power-cycle the chip with BOOT held during the cold boot.

If the buttons feel identical and you cannot tell which is which, confirm by elimination: the one that powers the watch off when held 6+ seconds is PWR; the other is BOOT.

### To enter the bootloader (download mode)

1. **Power the watch fully off** by holding PWR for 6+ seconds. The screen goes black and the COM port disappears entirely from `nanoff --listports`. Wait 2-3 seconds.
2. **Hold BOOT** (and keep holding it).
3. With BOOT still held, **tap PWR** to power back on. The AXP2101 raises the rails; the ESP32-S3 ROM samples GPIO0 at boot, sees BOOT low, and stays in download mode.
4. Wait 2 seconds, **release BOOT**. The screen stays black (no firmware running) - this is correct.
5. Run `nanoff --listports`. The bootloader-class COM port appears (typically the lower number, e.g. `COM10`).
6. Re-flash:
   - Runtime upgrade / downgrade: `nanoff --target ESP32_S3_BLE --serialport COMx --update [--fwversion X.Y.Z.W]`
   - Clean reset: `nanoff --target ESP32_S3_BLE --serialport COMx --update --masserase`

### After a flash, the chip may stay in bootloader mode

Because of the same USB re-enumeration / RTS-reset issue that produces the cosmetic `Error E4000: Hard resetting via RTS pin... The handle is invalid.` at the end of a successful flash, the chip sometimes does not automatically boot the freshly-written firmware. It just sits idle on the bootloader-class port (COM10 in our setup).

Symptom: after `nanoff` reports verified hashes for all three partitions, `nanoff --listports` still shows the same bootloader port (no flip), and probing it with `nanoff --devicedetails` succeeds (esptool replies, which means it is still in bootloader, not runtime).

Recovery is a clean PMIC power-cycle:

1. **Hold PWR 6+ seconds** to cut all rails.
2. Wait 2-3 seconds.
3. **Tap PWR briefly** to power back on (do NOT hold BOOT - we want a normal boot now, not a download-mode boot).
4. The chip boots from flash into nanoFramework. The runtime swings the USB descriptor, the COM port re-enumerates (COM9 in our setup).
5. Probe to confirm: `nanoff --serialport COM9 --platform esp32 --devicedetails` should fail with `Invalid head of packet (0x4E)` - that error is the proof the runtime is up.

`esptool ... --after hard_reset run` was tried as a software-only alternative; on this watch it issues the reset but the chip stays in bootloader. The PMIC power-cycle is the only reliable path.

There is no way to permanently brick the chip from software - the ROM bootloader lives in mask ROM and is not flashable. Worst case: full mass-erase + re-flash.

## The matched-runtime / matched-libraries gotcha

**Each nanoFramework runtime image expects specific native checksums for the managed class libraries.** Mixing stable class libraries (1.x.x) with a too-new runtime fails at deploy time with:

```
The connected target has the wrong version for the following assembly(ies):
    'System.Net' requires native v100.2.0.11, checksum 0xD82C1452.
    Connected target has v100.2.0.12, checksum 0x6DFA71D6.
```

**This is normal, not a packaging bug.** When the nanoFramework team bumps a native interop interface they:

1. Release a new runtime image with the bumped native checksum
2. Release matching managed class libraries (often as `2.0.0-preview.X` while the new ABI is stabilizing)
3. Eventually graduate the previews to a new stable line that targets the bumped checksum

**Two valid responses:**

A. **Pin a runtime that matches the stable libraries you want to use.**
   `nanoff --target ESP32_S3_BLE --serialport COMx --update --fwversion 1.16.0.567`
   (Pick the highest runtime whose stable libraries you can find on nuget.org.)

B. **Adopt the preview class libraries** that match the latest runtime.
   Update `packages.config` to use `2.0.0-preview.X` versions of the runtime-coupled packages (System.Net, System.IO.Streams, System.Threading, Runtime.Events, Runtime.Native, etc).
   Trade-off: preview API surface may change before stable.

For SpawnWear we currently take path **A** - pin to the runtime that matches stable libraries.

### How to find a matched-runtime version

1. List nuget.org versions for the affected package:
   `curl -s https://api.nuget.org/v3-flatcontainer/nanoframework.system.net/index.json`
2. Find the latest STABLE version (no `-preview`, no `-alpha`).
3. Look in `_vendor-nanoframework-iot/devices/<DeviceName>/packages.config` for any device whose CI uses that stable version - their tested runtime is implicitly the runtime your stable libraries match.
4. Use `nanoff --listtargets --platform esp32` to see which runtime versions are available; pick one slightly older than the latest if the latest broke compatibility.

### A clarifying example

When this repo was first scaffolded:
- We flashed `ESP32_S3_BLE-1.16.0.568` (the latest at the time).
- VS deploy failed with `System.Net requires native v100.2.0.11 / target has v100.2.0.12`.
- We confirmed `nanoFramework.System.Net 1.11.50` (latest stable) ships native `v100.2.0.11`, while `1.16.0.568` runtime expects `v100.2.0.12`.
- Bumping all stable libraries to latest stable (1.x) did not fix it - the native bump is in 568.
- Re-flashing `1.16.0.567` (one version older) brought the runtime back into stable-library compatibility.

## Recovery - if the watch becomes unresponsive

Same as above: hold BOOT + tap RESET to force ROM bootloader mode, then re-flash.

## Recipe summary (copy / paste)

```bash
# 1. List ports, confirm watch is connected
nanoff --listports

# 2. Confirm chip identity (replace COM10 with whatever shows up)
nanoff --serialport COM10 --platform esp32 --devicedetails

# 3. First flash - mass-erase + install nanoFramework runtime
nanoff --target ESP32_S3_BLE --serialport COM10 --update --masserase

# 4. (after reboot, port number changed) Verify - "Invalid head of packet (0x4E)" means success
nanoff --listports
nanoff --serialport COM9 --platform esp32 --devicedetails

# 5. Deploy app from Visual Studio 2022 with nanoFramework extension
```

## What we measured today (2026-04-28, first watch off the truck)

- Chip: ESP32-S3 QFN56 rev v0.2, 8MB embedded PSRAM, 32MB external GigaDevice flash, 40MHz crystal, MAC `1C:DB:D4:7B:03:0C`
- Firmware: ESP32_S3_BLE-1.16.0.568 (latest at time of flashing)
- Bootloader port: **COM10**, runtime port: **COM9** (USB re-enumerated between modes)
- Flash time: ~10 seconds for the 1.37 MB compressed firmware payload at 1117 kbit/s on USB-CDC
- Both `--update` (no mass-erase) and `--update --masserase` were tried; only the latter completed (the first failed at "Backup configuration" - documented above)
