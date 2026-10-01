#:package nanoFramework.Tools.Debugger.Net@2.5.28

// Reboot a running SpawnWear watch into the ESP32-S3 ROM download mode over the wire protocol - the
// flasher's mode - so esptool can write firmware with NO BOOT-button dance.
//
// Needs nanoCLR built from nf-interpreter with upstream #3534 (2026-09-22): EnterProprietaryBooter sets
// RTC_CNTL_FORCE_DOWNLOAD_BOOT and resets. The LostBeard fork has it from branch
// spawnwear/upstream-2026-10-01. Older firmware ignores the request (the watch just keeps running).
// The flag lives in the always-on domain: it survives the reset that follows, and a power cycle clears it.
//
// On that firmware the CLR's wire protocol and the ROM download mode are the SAME USB-Serial-JTAG port,
// so the COM port does not change between "running" and "ready to flash". tools\nf-flash-py313.bat does
// not need this tool: esptool's own auto-reset reaches download mode through the USB-Serial-JTAG hardware.
// This is the firmware route (what VS / nanoff use), verified on the watch 2026-10-01.
//
// 🔴 Connect with requestCapabilities: true. Without it the library never reads the target's capabilities,
// sees SoftReboot = false, and silently sends NormalReboot instead (the watch just restarts the app).
//
// Usage:
//   dotnet run tools/nf-bootloader.cs COM6        reboot into download mode
//   tools\nf-flash-py313.bat COM6                  then flash (esptool resets it back into the new firmware)

using System;
using System.Threading.Tasks;
using nanoFramework.Tools.Debugger;
using nanoFramework.Tools.Debugger.Extensions;

if (args.Length < 1) { Console.WriteLine("Usage: dotnet run tools/nf-bootloader.cs <COM>"); return 1; }
string port = args[0];

var portBase = PortBase.CreateInstanceForSerial(true);
for (int i = 0; i < 40 && portBase.NanoFrameworkDevices.Count == 0; i++) await Task.Delay(250);

NanoDeviceBase? device = null;
foreach (var d in portBase.NanoFrameworkDevices)
{
    if (d.ConnectionId.IndexOf(port, StringComparison.OrdinalIgnoreCase) >= 0) { device = d; break; }
}
if (device == null)
{
    Console.WriteLine($"No nanoFramework device on {port}. Found: {portBase.NanoFrameworkDevices.Count} device(s).");
    foreach (var d in portBase.NanoFrameworkDevices) Console.WriteLine($"  {d.ConnectionId}  {d.Description}");
    return 1;
}
Console.WriteLine($"Selected: {device.Description} ({device.ConnectionId})");

if (!device.DebugEngine.Connect(5000, force: true, requestCapabilities: true)) { Console.WriteLine("Connect failed."); return 1; }

Console.WriteLine($"Target: {device.TargetName} {device.Platform}  CLR {device.CLRVersion}");

// The library only sends EnterProprietaryBooter when the target reports SoftReboot (or is in nanoBooter);
// otherwise it silently substitutes NormalReboot - so report both, never assume.
Console.WriteLine($"Capabilities.SoftReboot = {device.DebugEngine.Capabilities.SoftReboot}");
bool ok = device.DebugEngine.RebootDevice(RebootOptions.EnterProprietaryBooter, new Progress<string>(m => Console.WriteLine($"  [debugger] {m}")));
Console.WriteLine($"RebootDevice returned {ok}");
await Task.Delay(500); // let the progress callbacks print
return ok ? 0 : 1;
