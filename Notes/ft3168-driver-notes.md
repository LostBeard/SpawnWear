# FT3168 Touch Controller - Driver Notes

The FT3168 is the FocalTech self-capacitance touch controller on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch. I²C address `0x38`. INT pin on `GPIO38` (active-low while a finger is in contact). RESET pin on `GPIO9`.

## Sources

- **Datasheet**: limited public availability; many `FT5xxx` family samples document related-but-not-identical layouts. Don't trust offsets from FT5316/FT5436/FT5516 vendor code without verification on actual silicon.
- **Vendor sample**: Waveshare's Arduino demo at `_vendor-waveshare-demo/` uses the LVGL touch driver path, which abstracts the register layout away. Read the LVGL `lv_indev_touch_read` source to see what shape the layout is expected to be.
- **Production driver**: `SpawnWear/Drivers/Touch/Ft3168Driver.cs`.

## Initialization

1. Pulse RESET (GPIO9) low for ~5 ms, then high; wait ~50 ms for the chip to come up.
2. Probe register `0xA8` (CHIP_ID) - returns `0x03` on this silicon. `0x86` is the FT5316 / FT6206 family; if you see that, the wrong driver is on the I²C bus.
3. Optionally write `0xA4` (interrupt mode) to set `0x00` (polling) or `0x01` (trigger). We default to polling - the EventLoop already polls on a tick budget anyway, and IRQ-driven adds threading complexity we don't need yet.

## Burst-read register layout (THE one that bit us)

Reading 5 bytes from register `0x02`:

| Offset | Field | Notes |
|---|---|---|
| 0 | FingerNum | Low 4 bits = number of fingers down. Top 4 bits reserved. |
| 1 | X1H | Low 4 bits = X[11:8]. High 4 bits = event flags (touch-down / touch-up / contact). |
| 2 | X1L | X[7:0] |
| 3 | Y1H | Low 4 bits = Y[11:8]. High 4 bits = touch ID (0..N-1 for multi-touch) |
| 4 | Y1L | Y[7:0] |

For multi-touch (N > 1 finger), additional fingers follow at offsets `5 + 6*(i-1)` in the same shape, plus a 1-byte gap between fingers.

**There is NO reserved gap byte between FingerNum (offset 0) and X1H (offset 1).** Many `FT5xxx` vendor samples DO have a gap (they read `XH` from offset 2 / `YH` from offset 4 because their first-finger block starts at offset 2, not offset 1). Do not transplant offsets between FT3168 and FT5xxx code without verification. Full bug writeup: `Research/ft3168-burst-read-layout.md`.

## 12-bit coordinate decode

```csharp
static ushort Decode12Bit(byte hi, byte lo)
{
    // Low 4 bits of high byte | full low byte. Top 4 bits of `hi` are
    // event flags (X) or touch ID (Y) - mask them off.
    return (ushort)(((hi & 0x0F) << 8) | lo);
}
```

Returned coordinates are panel-relative (0..409 for X, 0..501 for Y on the 410x502 panel). No row / column offset to apply.

## Detection rule for layout bugs

If your driver returns coordinates wildly out of range (e.g. > 2000 on a 410-wide panel), suspect a layout issue BEFORE suspecting hardware. The FT3168 silently returns plausible-looking offset data when the read window is wrong; no error code surfaces. The decode table above is the canonical layout for the silicon on this watch as of 2026-05-04.

## Tap classification (firmware-side)

Lives in `Program.cs`. Touch DOWN → record (x, y, ticks). Touch UP → compute elapsed + (Δx, Δy):

| Condition | Result |
|---|---|
| elapsed < 30 ms | Reject as noise |
| Δx ≤ 6 && Δy ≤ 6 && elapsed >= 30 ms && elapsed < 800 ms | **Tap** |
| elapsed >= 800 ms | **Long-press** (routed to `_nav.GoHome()`) |
| Δx > 6 or Δy > 6 | **Drag/swipe** (V1 ignores; Phase 2 reserved for scrolling) |

Wake-from-sleep state-machine integration:
- If `_stateAtFingerDown == ScreenState.Sleep`, the tap-up is consumed silently as "wake-tap"; no UI tap fires. Without this, tapping a sleeping screen would wake it AND fire a phantom tap, which on the Settings screen meant "tap SLEEP again immediately and go right back to sleep" - the bug TJ caught 2026-05-03.

## Power model

INT pin is active-low while finger is in contact. We poll on the EventLoop tick budget (16 ms while finger held, 1 s while idle). Phase 2's switch to INT-edge-driven reads would let us reduce idle I²C traffic from ~1 Hz to event-only.

Touch sensing on the FT3168 itself is on a self-running clock that the AXP2101 keeps powered through the same rail as the AMOLED. Putting the panel to sleep does NOT cut touch power; that's why we can wake-tap.
