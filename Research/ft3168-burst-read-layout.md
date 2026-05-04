# FT3168 Touch Controller — Burst-Read Register Layout

The FT3168 self-capacitance touch controller (FocalTech, I²C address 0x38) reports finger position in a register block starting at 0x02. Most online references and many vendor samples document the layout WITH a reserved gap byte after the finger-count byte. **On the watch's FT3168 silicon, there is NO gap.** Decoding with the gap shifts every coordinate by one byte and produces nonsense values.

Discovered 2026-05-04 during SpawnWear bring-up.

## What the registers actually hold

Reading 5 bytes from register 0x02 in a single I²C burst:

| Offset | Field | Notes |
|---|---|---|
| 0 | FingerNum (low 4 bits) | Number of fingers currently down |
| 1 | X1 high byte (low 4 bits = X[11:8]) | High nibble = event flags |
| 2 | X1 low byte (X[7:0]) | |
| 3 | Y1 high byte (low 4 bits = Y[11:8]) | High nibble = touch ID |
| 4 | Y1 low byte (Y[7:0]) | |

There is no reserved byte between FingerNum (offset 0) and X1H (offset 1). Multi-touch fingers 2..5 follow at offsets 5+ in the same layout pattern.

## The bug we hit

Initial implementation read the burst block and decoded with offsets `[2,3]` for X and `[4,5]` for Y - matching what some `FT5xxx`-family vendor samples document. Result: tap at panel center (~205, 251) reported as (~2305-3584, varying) - clearly off by one byte across both axes.

**Detection rule:** if reported coordinates are way out of the panel size range (e.g. > panel_width or > panel_height by more than 2x), suspect a layout issue before suspecting the chip is broken. The FT3168 silently returns valid-looking but offset values when the read range is wrong; no error code surfaces.

## Fix

```csharp
byte fingerCount = readBuf[0];
ushort x1 = Decode12Bit(readBuf[1], readBuf[2]); // X1H | X1L
ushort y1 = Decode12Bit(readBuf[3], readBuf[4]); // Y1H | Y1L

static ushort Decode12Bit(byte hi, byte lo)
{
    // Low 4 bits of high byte | full low byte
    return (ushort)(((hi & 0x0F) << 8) | lo);
}
```

Lives in `SpawnWear/Drivers/Touch/Ft3168Driver.cs`.

## Verification

After the fix, taps in known-position UI elements report coordinates within 5 px of expected. Long-press / drag detection works. Multi-finger reads (when needed) extend the burst to 5 + 6*(N-1) bytes for N fingers.

## What about INT-pin-driven reads?

Per the spec the INT pin (GPIO38 on this board) goes LOW while a finger is in contact. Polling the I²C bus on every event-loop tick works fine at the 1 s idle / 16 ms touch-held tick budget; the INT-pin-edge approach saves power but isn't strictly needed for V1. Phase 2 (UI framework + lifecycle) should consider switching to INT-driven reads to reduce idle I²C traffic.
