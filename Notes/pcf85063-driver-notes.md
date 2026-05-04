# PCF85063 RTC - Driver Notes

The PCF85063 is the I²C real-time clock on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch. Address `0x51`. Battery-backed via the AXP2101's coin-cell pin (when a coin cell is installed); time survives main-battery removal. INT pin on `GPIO39` (alarm output).

## Sources

- **Datasheet**: NXP PCF85063A / PCF85063TP. Public, well-documented.
- **Reference implementation**: `nanoFramework.IoT.Device.Pcf85063` exists at `_vendor-nanoframework-iot/devices/Pcf85063/` (cloned outside this repo). Solid driver; we wrote our own anyway because it pulls in additional NuGet dependencies we didn't want in the deploy budget.
- **Production driver**: `SpawnWear/Drivers/Rtc/Pcf85063Driver.cs`.

## Register map (the subset we use)

| Reg  | Name | Notes |
|------|------|-------|
| 0x00 | CTRL1 | Bit 5 = STOP (clear to run oscillator). Bit 1 = 12_24 (clear for 24-hour mode). |
| 0x01 | CTRL2 | Alarm interrupt enable, alarm flag |
| 0x04 | SECONDS | BCD seconds. **Bit 7 = OS (oscillator stop) flag** - set on power-up before the oscillator has stabilized; means "time is invalid until you set it" |
| 0x05 | MINUTES | BCD |
| 0x06 | HOURS | BCD, low 6 bits in 24h mode |
| 0x07 | DAYS | BCD day-of-month, low 6 bits |
| 0x08 | WEEKDAYS | Low 3 bits, application-defined start day |
| 0x09 | MONTHS | BCD month, low 5 bits |
| 0x0A | YEARS | BCD 0..99, mapped to 2000..2099 by convention |
| 0x0B-0x10 | Alarm registers | Phase 5 territory; unused in V1 |

## Critical: the OS (Oscillator Stop) flag

On a fresh power-up (or coin-cell-too-low + main-battery-pulled state), the chip's seconds register reads back with bit 7 set. The chip is running, but it considers its time invalid because the oscillator hasn't been stable since the last clear. Software that ignores this flag will read back garbage time.

Our `TryRead` returns `false` when the OS flag is set, signaling "RTC reports invalid time, use uptime fallback." The Watchface and StatusBar both have a fallback path:

```csharp
if (_rtc != null && _rtc.TryRead(out var t))
{
    // RTC is valid - use t.Hour, t.Minute, etc.
}
else
{
    // Fallback to uptime
    long elapsedSec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
    h = (int)((elapsedSec / 3600) % 24);
    // ...
}
```

The `Set()` path clears the OS flag implicitly because the seconds-register write replaces the entire byte (including bit 7).

## Initialization sequence

1. Read CTRL1.
2. If `(ctrl1 & 0x22) != 0` (STOP bit set OR 12-hour mode), write back with both cleared. Idempotent if already running in 24h mode.

That's it. The chip is running by default at ~32.768 kHz crystal driven by the on-board crystal. No further calibration needed for our use case.

## Setting the time

```csharp
_rtc.Set(new RtcTime
{
    Year = 2026, Month = 5, Day = 4,
    Hour = 23, Minute = 50, Second = 0,
    Weekday = 1  // 1 = Monday in our convention
});
```

Boot code seeds 2026-05-04 23:50:00 if the RTC reports OS-flag-set, so the watch always has SOMETHING to display before the user has a chance to set it via Settings or NTP. Phase 4's Settings → Time page lets the user adjust.

## Power consumption

In normal-running mode (oscillator on, no alarm output), the chip draws on the order of nanoamps. The AXP2101 keeps it alive through the coin-cell pin even when main battery is dead. With a CR1220 coin cell, expected battery life is years.

## Phase 5: alarms

Not yet implemented. Will use:
- Write alarm time to `0x0B-0x0F` (seconds / minutes / hours / day / weekday alarms)
- Set CTRL2 bit 7 (AIE - alarm interrupt enable)
- Hook `GPIO39` falling-edge interrupt - on fire, AIE clears the alarm flag and wakes the CLR

This is the path to "wake from low-power sleep at a specific wall-clock time" which the AI Assistant flagship app needs for scheduled prompts.

## Weekday convention

The chip stores weekday in 3 bits, application-defined start. Our convention (matching the standard `DateTime.DayOfWeek` enum):
- 0 = Sunday
- 1 = Monday
- ...
- 6 = Saturday

Watchface's date label (`FormatDate`) uses this convention for the `WeekdayNames[]` lookup. Don't re-number or you'll see "MON" rendering on a Wednesday.

## What we deliberately don't use

- **Timer registers** (0x10-0x12): could implement a wake-after-N-minutes capability, but Phase 5 alarms cover the same ground with absolute time, which is more useful.
- **CLKOUT pin**: configurable square wave output. Not exposed on this board's pinout - the trace runs to a no-connect pad.
- **Capture-on-INT mode**: would let us record time at the moment a sensor interrupt fires (e.g. shake to wake). Useful but not yet needed.
