# SpawnWear Plans

Forward-looking design notes. Each file describes WHAT a future feature looks like, WHY it's shaped that way, and the open questions that need answers before it ships. Implementation sequencing lives in `roadmap.md`; everything else is per-topic.

## Index

- **[roadmap.md](roadmap.md)** - canonical phase-by-phase roadmap. Status / Milestones in `README.md` tracks what already shipped; this file describes what's next. Phase 1 (display + touch) complete 2026-05-03. Phase 2 (UI framework + launcher) largely complete 2026-05-04. Phase 3 (system services + WiFi + HTTP) in progress.
- **[launcher-android-quality.md](launcher-android-quality.md)** - what "Android-quality" means concretely for the home screen, what we've shipped, and the iteration order for what's next.
- **[sd-card-apps.md](sd-card-apps.md)** - design sketch for user-installable apps loaded from microSD. Verified against the actual nf-interpreter source: `Assembly.Load(byte[])` works, `c_Flags_NeedReboot` returns `CLR_E_BUSY` (not a forced reboot), name collisions don't dedupe (both copies link, second is unreachable). Microkernel architecture: core firmware in flash, apps load from SD via dynamic assembly load.
- **[app-contracts-v1.md](app-contracts-v1.md)** - the concrete C# surface for `SpawnWear.AppContracts v1`. Full ISpawnApp + IServiceHost + capability interface definitions, manifest schema, V1 verification harness checklist. Phase 8 entry point.
- **[companion-pwa.md](companion-pwa.md)** - the Blazor WASM PWA that mirrors the watch over BLE + WiFi. Optional companion, not a tether.

## Conventions

- **State the goal in concrete terms.** "Android-quality launcher" is a fine title; the body of the file should list specific UI affordances ("rounded tile corners", "page indicator pill") so progress is measurable.
- **Order by visual impact + dependency, not by what's easiest.** A plan that says "first I'll do A because it's quick" hides the strategic question of whether A is even worth doing.
- **Call out anti-goals.** Features that LOOK like they belong but actively hurt the project (e.g. live wallpapers on an AMOLED watch) are worth listing explicitly so we don't drift toward them.
- **Link to constraints.** Every plan should reference the production constraints it's bound by: the deploy ceiling, the AMOLED power model, the CO5300 alignment quirks. The constraint files live in `Research/` and `Notes/`.
