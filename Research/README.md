# SpawnWear Research

Investigation notes, root-cause findings, and reverse-engineering work that's grounded in measurements rather than speculation. Each entry is the kind of thing that took hours to pin down — write it once, save the next person from repeating it.

## Index

- **[nf-interpreter-deploy-ceiling.md](nf-interpreter-deploy-ceiling.md)** — `nf-deploy` silently corrupts the on-flash assembly table when total wire-protocol deploy >= ~290 KB. Reports 100% / Done; subsequent `nf-attach` shows garbled assembly names. Likely root cause: missing mmap cache invalidation in `Esp32FlashDriver_Write`. `tools/nf-deploy.cs` has a pre-flight guard.
- **[esp32s3-wifi-router-compatibility.md](esp32s3-wifi-router-compatibility.md)** — nanoFramework WiFi on ESP32-S3 fails to authenticate against modern routers running 802.11 b/g/n/ax mixed mode + auto 20/40 MHz channel width, even with correct credentials. Switch the AP to b/g/n only + 20 MHz to fix. The fix is in the router, not the watch.
- **[ft3168-burst-read-layout.md](ft3168-burst-read-layout.md)** — The FT3168 touch controller's burst-read layout starting at register 0x02 has NO reserved gap byte after FingerNum, contrary to many `FT5xxx` vendor samples. Use offsets `[1,2]` for X and `[3,4]` for Y, not `[2,3]` / `[4,5]`.

## Conventions

- **Lead with the symptom you saw and the conditions that produce it.** A future reader skimming the index needs to recognize the bug they're chasing.
- **Show the measurement.** "I read register `X` and got value `Y` while the datasheet says `Z`" beats "the chip is acting weird."
- **Include the fix AND the failure mode it doesn't cover.** A research note that only documents the happy-path resolution lets the same trap reappear on a slightly different shape.
- **Don't speculate.** If you don't know the underlying cause, say "likely / candidate" explicitly. Memory entries that confidently asserted wrong root causes have wasted more team time than blank pages.

## Related

- `Notes/co5300-quirks.md` — chip-quirk notes for the AMOLED driver IC. Originally lived in Notes/ before this directory existed; will stay there since it's tightly coupled with the driver implementation.
- Agent memory at `~/.claude/projects/D--users-tj-Projects/memory/` carries the original feedback notes that several of these entries grew out of. The repo copies are canonical for documentation purposes; the memory entries are the agent's working scratch.
