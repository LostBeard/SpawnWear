# SpawnWear Docs

Reference material for working on SpawnWear. The split between this folder and the others:

- **Docs/** (this folder) - reference material that doesn't change often. Architecture, hardware pin map, dev loop, API surface.
- **Notes/** - operational know-how. Flashing recipes, chip quirks, build-environment setup, design docs for in-flight upstream contributions.
- **Plans/** - forward-looking plans. Roadmap, design sketches for features we haven't shipped yet.
- **Research/** - investigations + findings. Bug repros, root-cause writeups, performance measurements.

## Index

- **[architecture.md](architecture.md)** - the five system layers (HAL → drivers → services → UI framework → apps), boot sequence, BLE GATT layout, power model
- **[hardware.md](hardware.md)** - canonical pin map + IC list + I²C bus addresses + flash partition layout for the Waveshare ESP32-S3-Touch-AMOLED-2.06
- **[dev-loop.md](dev-loop.md)** - F5-in-VS daily loop, CLI deploy via `tools/nf-deploy.cs`, when bootloader-mode is actually needed (rare), live screen capture over WiFi
- **[milestones.md](milestones.md)** - historical record of every significant ship date. README "Recent highlights" calls out the last few; this file is the full log
- **[nanoframework-compatibility.md](nanoframework-compatibility.md)** - what nanoFramework's class libraries support out of the box on this watch and where we had to hand-roll, with the matched runtime + library version pinning
