#:package nanoFramework.Tools.Debugger.Net@2.4.42

// Attach to a running nanoFramework runtime over the wire protocol and inspect
// what is deployed. Read-only - does NOT redeploy or restart the user app.
//
// Usage:
//   dotnet run tools/nf-attach.cs                # COM9, 5s ExecutionMode poll, then disconnect
//   dotnet run tools/nf-attach.cs COM9 30        # COM9, 30s poll
//
// What it shows:
//   - The list of assemblies currently loaded by the runtime (versions + names)
//   - ExecutionMode reads (NOTE: see caveat below before drawing conclusions)
//
// CAVEAT - ExecutionMode is misleading from this CLI path.
//   nanoFramework.Tools.Debugger.Net's GetExecutionMode() may return
//   "ProgramExited, DebuggerEnabled" even while a Main() is actively running and
//   reachable via VS breakpoints on the same build. Do NOT use this script to
//   conclude that the user app crashed - use VS breakpoints + the Exception popup
//   instead. This tool is good for: confirming what assemblies are deployed,
//   checking the device is reachable, and listing target metadata. Burned 90 min
//   on this 2026-05-03 - documented in Notes/flashing.md.
//
// Prerequisites:
//   - Watch is in runtime mode (COM9 in our setup, NOT COM10 bootloader)
//   - No other process holds the COM port (VS debug stopped, terminals closed)

using System;
using System.Threading;
using System.Threading.Tasks;
using nanoFramework.Tools.Debugger;
using nanoFramework.Tools.Debugger.Extensions;

string port = args.Length > 0 ? args[0] : "COM9";
int pollSeconds = args.Length > 1 ? int.Parse(args[1]) : 5;

var portBase = PortBase.CreateInstanceForSerial(true);
for (int i = 0; i < 40 && portBase.NanoFrameworkDevices.Count == 0; i++)
{
    await Task.Delay(250);
}

NanoDeviceBase device = null;
foreach (var d in portBase.NanoFrameworkDevices)
{
    Console.WriteLine($"  candidate: ConnectionId={d.ConnectionId}  Description={d.Description}");
    if (d.ConnectionId.IndexOf(port, StringComparison.OrdinalIgnoreCase) >= 0)
    {
        device = d;
        break;
    }
}
if (device == null && portBase.NanoFrameworkDevices.Count > 0)
{
    device = portBase.NanoFrameworkDevices[0];
    Console.WriteLine($"Falling back to first device: {device.Description}");
}
if (device == null)
{
    Console.WriteLine("No nanoFramework device found.");
    return 1;
}
Console.WriteLine($"Selected: {device.Description}");

if (!device.DebugEngine.Connect(5000, true))
{
    Console.WriteLine("Connect failed.");
    return 1;
}
Console.WriteLine("Connected.");

device.DebugEngine.OnMessage += (m, t) =>
{
    var line = (t ?? "").TrimEnd('\r', '\n');
    if (!string.IsNullOrEmpty(line)) Console.WriteLine($"[runtime] {line}");
};

try
{
    var assemblies = device.DebugEngine.ResolveAllAssemblies();
    if (assemblies != null)
    {
        Console.WriteLine($"--- Assemblies ({assemblies.Count}) ---");
        foreach (var a in assemblies)
        {
            Console.WriteLine($"  {a.Result?.Name} v{a.Result?.Version.ToString() ?? "?"}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"ResolveAllAssemblies failed: {ex.Message}");
}

for (int i = 0; i < pollSeconds; i++)
{
    try
    {
        var em = device.DebugEngine.GetExecutionMode();
        Console.WriteLine($"  t+{i}s ExecutionMode (caveat above): {em}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  t+{i}s GetExecutionMode failed: {ex.Message}");
    }
    await Task.Delay(1000);
}

Console.WriteLine("Done.");
return 0;
