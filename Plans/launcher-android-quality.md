# Launcher: Android-Quality UI

The launcher is the watch's home screen. TJ's standard, set 2026-05-03, is "Android-quality" - the launcher is the visible artifact a user (or someone TJ shows the watch to) sees first, so it has to look like a finished consumer device, not a hobby project.

This is the iterative plan for getting there. As of 2026-05-04 we're maybe 60% there visually.

## What "Android-quality" means here

Concrete checklist, drawn from the Pixel launcher reference TJ shared on 2026-05-04:

- [x] **Grid of icons in clearly-rounded tiles** (corner radius is visually obvious; not a sharp rectangle)
- [x] **Vertical gradient background per tile** (top-down depth, hints at material thickness)
- [x] **Status bar at top** (HH:MM time + WiFi + USB + BLE + battery icons; collapses unused slots)
- [x] **Page indicator at bottom** (Android-style pill for active page, dim dots for others)
- [x] **Notification badges on tiles** (red bubble with count, top-right corner)
- [x] **Tap-to-launch** (tapping a tile opens that app; long-press returns to launcher)
- [x] **Wake-tap consumption** (touching a sleeping screen wakes it without firing the launcher tap)
- [ ] **Smooth transitions between tiles and apps** (currently snap-cut; should fade or slide)
- [ ] **Anti-aliased font** (current SmallFont 5x7 is blocky; Android uses sub-pixel-positioned vector fonts)
- [ ] **Touch ripple feedback** (visual confirmation that a tap registered, even before the app loads)
- [ ] **Per-tile accent color from app icon** (today, tile background is hand-picked; should be derived)
- [ ] **Vertical scroll for >9 tiles** (today the launcher is fixed 3x3; SD-card apps will push past 9)
- [ ] **Folder support** (group related apps into a single tile that expands)

## Constraints we accept

- **No 3rd-party graphics primitives.** We render via `nanoFramework.Graphics.Bitmap.FillRectangle` only. No font library, no curve primitive, no alpha compositing. Anti-aliasing has to come from font bitmaps with intermediate gray values, not from a runtime AA engine.
- **AMOLED-friendly black background.** Solid black uses ~0 mA per off pixel; gradients and white text are fine but a fully-white screen at full brightness costs significant battery.
- **Partial flush over full flush.** The CO5300 alignment quirks are baked into the firmware (every `Bitmap.Flush(x, y, w, h)` rounds for free), so partial flush is the default. Animations should target dirty rectangles, not full-screen redraws.
- **Wire-protocol deploy budget.** Per `Research/nf-interpreter-deploy-ceiling.md`, total .pe is capped around 235 KB. Every visual feature has to fit in remaining headroom. Big bitmap fonts are expensive; procedural rendering via FillRectangle is the dominant pattern.

## Iteration order (by visual impact + safety)

1. **Anti-aliased font** - likely the single biggest perceptual jump. Two options: (a) a custom 8x10 bitmap font with 2-bit gray values stored as packed nibbles; (b) a simple grayscale lookup that draws each pixel of the existing 5x7 font as 2x2 with row/column anti-aliasing. (a) is bigger but cleaner. **Watch the .pe budget.**
2. **Smooth screen transitions** - slide-left/slide-right when navigating between launcher screens. Implementable as N intermediate full-frame draws over ~150 ms. Does NOT need new graphics primitives. Costs CPU during the transition only.
3. **Touch ripple** - a 200 ms expanding-circle animation centered on the tap point. One new primitive needed: filled circle approximation via FillRectangle scanlines, similar to the corner-mask staircase already in `LauncherScreen`.
4. **Vertical scroll for >9 tiles** - blocked by needing more apps; keep designed but defer until SD-card apps land (Plans/sd-card-apps.md).
5. **Per-tile accent color from icon** - blocked on having actual icons (not procedural rectangles). Real icons are PNG decoded onto the framebuffer; nanoFramework.Graphics has PNG decode but I haven't measured its memory cost yet.

## Anti-goals (things that look good but aren't worth it)

- **Full-screen scroll bounce** - the elastic over-scroll Android does on its launcher. CO5300 partial-flush makes this expensive (the entire grid would have to redraw on every frame), and it's a fixation on a Pixel-specific affordance that doesn't fit a watch.
- **Live wallpapers** - same cost as full-frame draws, every frame. The black AMOLED background is the right answer for power.
- **Drag-to-reorder tiles** - sound idea, but Phase 9 territory; needs touch-hold-and-drag input handling we haven't built yet.

## Cross-references

- **Stock Waveshare firmware screenshot** - the 3x3 colored-icon grid reference TJ shared 2026-05-04 (Image #8 in the 2026-05-04 conversation log). Our launcher's hand-picked tile colors are inspired by this layout.
- **`SpawnWear/UI/LauncherScreen.cs`** - the production launcher. 413 lines as of 2026-05-04.
- **`SpawnWear/UI/StatusBar.cs`** - the production status bar.
- **`SpawnWear/UI/PageDots.cs`** - the production page indicator.
- **`screenshots/launcher-2026-05-04.png`** - the canonical "current state" image embedded at the top of the README. Update this whenever a visual change ships.
