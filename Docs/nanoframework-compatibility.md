# nanoFramework Compatibility

Realities of running C# on the Waveshare ESP32-S3-Touch-AMOLED-2.06 watch under nanoFramework. None of the gaps are blockers for Phases 1-4; the audio gap is a Phase 6 research item.

| Capability | Status | Notes |
|---|---|---|
| WiFi station + AP | **Supported** | `nanoFramework.System.Device.Wifi`. Drop AP to b/g/n + 20 MHz to auth - see `Research/esp32s3-wifi-router-compatibility.md` |
| BLE GATT server | **Supported** | `nanoFramework.Device.Bluetooth`. Active (pairing transport). The deploy ceiling that earlier forced it off was resolved 2026-05-05 (full 2.94 MB partition usable) - see `Research/nf-interpreter-deploy-ceiling.md` |
| OTA firmware update | **Supported** | nanoFramework has standard OTA (verify package API surface during Phase 3) |
| I²C device control (AXP2101 / QMI8658 / PCF85063 / FT3168) | **Supported** | `nanoFramework.Hardware.Esp32` + `System.Device.I2c` - drivers written by hand against the chip datasheets |
| PCF85063 RTC | **Hand-rolled** | Community driver `nanoFramework.IoT.Device.Pcf85063` exists; we wrote our own to skip its dependency tree (`Drivers/Rtc/Pcf85063Driver.cs`) |
| AXP2101 PMIC | **Hand-rolled** | Community driver `nanoFramework.IoT.Device.Axp2101` is comprehensive but heavier than we need; ours at `Drivers/Power/Axp2101Driver.cs` |
| QMI8658 IMU | **Supported (hand-rolled)** | No upstream nanoFramework driver; protocol is plain I²C register reads. `Drivers/Imu/Qmi8658Driver.cs` shipped + hardware-verified in Phase 3 (2026-06-20) |
| FT3168 touch | **Hand-rolled** | `Drivers/Touch/Ft3168Driver.cs`. See `Notes/ft3168-driver-notes.md` for the burst-read layout that bit us |
| AMOLED display via CO5300 QSPI | **Custom upstream contribution** | nanoFramework's display drivers are SPI, not QSPI. Lives on the LostBeard fork at `feature/qspi-display-driver` branches of `nf-interpreter` + `nanoFramework.Graphics`. See `Notes/qspi-display-driver-design.md`. Will PR upstream once verified end-to-end |
| I²S audio (ES8311 / ES7210) | **Gap / partial** | nanoFramework I²S surface is limited. PDM mic capture is even more constrained. Phase 6 is a research item before promising delivery |
| USB-CDC for `Debug.WriteLine` | **Supported** | Native USB-OTG → CDC. Standard nanoFramework path |
| Dynamic assembly load (`Assembly.Load(byte[])`) | **Supported with caveats** | Pure managed assemblies load cleanly; `c_Flags_NeedReboot` assemblies return `CLR_E_BUSY`. Full analysis in `Plans/sd-card-apps.md` |

## Matched runtime + library combo

Runtime image **ESP32_S3_BLE 1.16.0.563** + stable 1.x class libraries with **`nanoFramework.System.Net` bumped to 1.11.50** (the latest stable). 1.11.47 lags the runtime by one System.Net native patch.

Don't take "latest" runtime automatically: 1.16.0.567 and 1.16.0.568 also have System.Net v100.2.0.12 but their other assemblies move ahead of stable libs in different ways. The 2.0.0-preview library line is currently AHEAD of every released runtime, so it is unusable today.

## Custom NuGet feed for the QSPI fork

Three local-feed packages (`nanoFramework.Graphics`, `.Graphics.Core`, `.Graphics.Co5300`) live at `D:/users/SpawnDevPackages/` at version `2.0.0-spawnwear.2`. They wrap the LostBeard fork of `nanoFramework.Graphics` and the matching native bits in our custom `nf-interpreter` build. `tools/nf-graphics-repack.cs` rebuilds them in one shot.

`SpawnWear.nfproj` references these via standard `<HintPath>` to `..\packages\<id>.<version>\lib\*.dll`, so anyone cloning the repo gets matching API + native checksum without manual file copies.
