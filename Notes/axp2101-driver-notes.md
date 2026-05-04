# AXP2101 PMIC - Driver Notes

The AXP2101 is the X-Powers Power Management IC on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch. I²C address `0x34`. It owns:

- USB-C VBUS detect + charge controller for the lithium battery
- Multiple regulated rails: DC1-5 (high-current buck converters) + ALDO1-4 / BLDO1-2 / CPUSLDO / DLDO1-2 (LDOs)
- ADC channels: VBAT, battery temperature (TS), VBUS, VSYS, die temperature
- Built-in fuel gauge that reports battery State-of-Charge as a 0..100 percent
- An "EXIO" GPIO multiplexer including the watch's PWR side button on EXIO6
- An IRQ output line that signals charging events + EXIO presses on `GPIO10`

## Sources

- **Datasheet**: X-Powers AXP2101 datasheet. Public; long; complete.
- **XPowersLib**: <https://github.com/lewisxhe/XPowersLib> - C++ Arduino driver. Useful reference for register names + sequences, but written for many different X-Powers chips so the AXP2101-specific code is mixed with conditional code for other parts.
- **Rust port reference**: `_vendor-rust-watch/src/peripherals/power.rs` - cleanest concise reference for what THIS watch needs the chip to do.
- **Vendor demo**: `_vendor-waveshare-demo/.../port_axp2101.cpp` - what Waveshare's Arduino sample does.
- **nanoFramework community driver**: `_vendor-nanoframework-iot/devices/Axp2101/` - comprehensive port we don't use because it pulls in additional NuGet refs we'd rather skip.
- **Production driver**: `SpawnWear/Drivers/Power/Axp2101Driver.cs`.

## Rail mapping on this watch

| Rail | Voltage | Powers | Notes |
|------|---------|--------|-------|
| DC1 | **3.3 V** | Main 3.3 V supply for SoC peripherals | Always-on |
| DC3 | 3.3 V | Audio rail (ES8311 + ES7210) | Phase 6 |
| ALDO1 | **3.3 V** | Display + peripheral 3.3 V rail | Required for AMOLED to light up |
| ALDO2 | 3.3 V | Touch + sensor rail | FT3168 + QMI8658 power |
| ALDO3 | 3.3 V | Display backlight / OLED secondary | Sometimes routed to LCD bias on similar boards |
| BLDO1-2 | 1.8 V / 3.3 V | Reserved | Not used in V1 |

Empirically (verified by register reads at boot) **every rail comes up enabled at AXP2101 power-on-reset / bootloader handoff** on this watch. We don't need to toggle anything to get the panel lit; we just re-write the voltages defensively in case a future low-power state has dropped them, and bit-OR the on/off register so we never accidentally turn off a rail another driver depends on.

The earlier "panel is dark because we forgot to enable ALDO1" hypothesis from 2026-04-29 was a red herring - the rails were already on; the dark screen was the QSPI command-byte BSS-uninit bug (see `Notes/co5300-quirks.md` and `feedback_native_field_index_must_be_read.md`).

## Init sequence (boot)

```csharp
EnableDisplayRails();   // (Re-)write DC1 + ALDO1-3 to 3.3 V; bit-OR enable bits
EnableAdc();            // Enable VBAT + TS + VBUS + VSYS + die-temp ADC channels
int batPct = ReadBatteryPercent();
int batMv  = ReadBatteryMillivolts();
bool vbus = IsVbusPresent();
```

## Fuel gauge

`REG_BAT_PERCENT (0xA4)` returns a 0..100 byte directly. The AXP2101 has an integrated coulomb-counting fuel gauge that calibrates itself against discharge cycles; the percent reading is reasonable on a charged-then-discharged unit but can be wildly off on a cell that's just been swapped in (the gauge needs a few full charge cycles to find its bounds).

Cap-and-fall behavior: brand-new, unused units sometimes report 100% for hours before starting to drop. That's the gauge's "I have not seen this cell run down yet, assuming full" mode. Once the cell hits ~3.6 V, the gauge starts tracking properly.

We treat percent < 0 (read failure) and percent == 0 (uncalibrated / dead) interchangeably for UI purposes - both render as a hollow battery outline.

## VBUS detect

`REG_STATUS1 (0x00)` bit 5 = VBUS present. Used in the status bar to show / hide the USB plug glyph. When VBUS goes from absent to present the AXP2101 raises an IRQ on `GPIO10`; we don't yet hook that line (Phase 3 logger / event service work) but the polled read is sufficient for V1.

## PWR button via EXIO6

The PWR side button on the bezel is NOT a direct GPIO. It's wired to the AXP2101's EXIO6 input pin and reflected through the AXP's IRQ register. The chip has hardware-level handling for it:
- Single click while powered = wake from soft-off (if VBUS present) OR ignored
- Hold > 6 s = power off the entire system (AXP cuts main rails)
- Multiple clicks / patterns: configurable

Hold-to-power-off is enabled by default at POR. **Do not hold PWR > 6 s during normal use** or the watch shuts off mid-debug. We don't reconfigure this for V1; Phase 4 Settings → Power may expose the timeout slider.

To READ the PWR button state from software (for "click to wake from sleep"), poll the AXP IRQ status register and check the EXIO6 short-press flag. Phase 3 system service work.

## ADC reads

Every ADC channel result is a 14-bit value spread across an H/L byte pair. Production driver reads VBAT only (mV) and converts the percent register directly. ADC enable bit map (`REG_ADC_ENABLE = 0x30`):

| Bit | Channel |
|-----|---------|
| 0 | VBAT |
| 1 | TS (battery temperature thermistor) |
| 2 | VBUS |
| 3 | VSYS |
| 4 | Die temperature |

We write `0x1F` (all enabled) at boot. Each channel adds ~5 µA to the chip's quiescent draw; negligible.

## Power coordination across services (Phase 3)

The Power system service should expose:

- `BatteryPercent` / `BatteryMillivolts` / `IsCharging` / `IsVbusPresent` (read-only, cached, refreshed on a slow tick)
- `RequestLowPowerMode(string reason)` / `ReleaseLowPowerMode(string reason)` - reference-counted; when no consumer requests Active power, the service tells the AXP to drop into a low-power state (rails dimmed but on, brightness off, sensors gated)
- `SubscribeBatteryEvent` - fires on charging-state change so the launcher can re-render the status bar without polling

This service is the single point of truth for "is the watch on battery vs charging vs low?" and apps consume it via the IServiceHost surface (see `Plans/app-contracts-v1.md`).

## What we deliberately don't do today

- **Charge-current configuration**: we accept the AXP2101 default. Datasheet allows 100..1000 mA; default is 500 mA which is fine for the cell on the watch. Phase 4 Settings → Battery may expose this.
- **Pre-charge / trickle thresholds**: defaults are sane for a Li-ion of this size; don't reconfigure.
- **Reset on hold**: AXP can be configured to reset the SoC on a different press pattern. Not needed; BOOT button + soft-reboot via the CLR debugger handle reset.
- **Coin-cell trickle charge**: the AXP2101 supports trickle-charging a CR1220-class coin cell from VBUS. If the unit comes with a coin cell and we want it kept alive across long off-periods, this needs to be enabled. Verify with TJ whether the unit has a coin cell installed before promising RTC persistence across full power loss.
