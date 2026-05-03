// Deploy a built nanoFramework project to a watch in runtime mode and capture
// Debug.WriteLine output. Same wire-protocol path Visual Studio uses; works
// without the bootloader-mode dance.
//
// Loads the VS-bundled `nanoFramework.Tools.DebugLibrary.Net.dll` directly via
// Assembly.LoadFrom + reflection. Reason: the nuget-published 2.4.x and 2.5.x
// versions of the Debugger.Net library hard-fail on runtimes that report
// IncrementalDeployment=False (which our custom ESP32_S3_BLE_QSPI runtime
// does). VS's bundled 2.5.0.0 DLL deploys to the same runtime fine, so we use
// THAT DLL directly. (Path resolved at runtime - fallback search order below.)
//
// Usage:
//   dotnet run tools/nf-deploy.cs                       # uses defaults: SpawnWear/bin/Debug + COM9 + 25s capture
//   dotnet run tools/nf-deploy.cs <binDir>
//   dotnet run tools/nf-deploy.cs <binDir> <com>
//   dotnet run tools/nf-deploy.cs <binDir> <com> <secs>

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

string binDir = args.Length > 0 ? args[0] : @"SpawnWear\bin\Debug";
string port = args.Length > 1 ? args[1] : "COM9";
int captureSeconds = args.Length > 2 ? int.Parse(args[2]) : 25;

if (!Path.IsPathRooted(binDir)) binDir = Path.GetFullPath(binDir);
if (!Directory.Exists(binDir))
{
    Console.WriteLine($"binDir does not exist: {binDir}");
    return 1;
}

// Find the VS-bundled debugger DLL - it ships in a randomly-named extension folder.
// Try a few likely roots. Adjust as needed for other VS installs.
string[] vsRoots = new[]
{
    @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\Extensions",
    @"C:\Program Files\Microsoft Visual Studio\17\Community\Common7\IDE\Extensions",
    @"C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\Extensions",
    @"C:\Program Files\Microsoft Visual Studio\17\Enterprise\Common7\IDE\Extensions",
    @"C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\Extensions",
    @"C:\Program Files\Microsoft Visual Studio\17\Professional\Common7\IDE\Extensions",
};
string dllPath = null;
foreach (var root in vsRoots)
{
    if (!Directory.Exists(root)) continue;
    var hit = Directory.GetFiles(root, "nanoFramework.Tools.DebugLibrary.Net.dll", SearchOption.AllDirectories).FirstOrDefault();
    if (hit != null) { dllPath = hit; break; }
}
if (dllPath == null)
{
    Console.WriteLine("Could not find VS-bundled nanoFramework debugger DLL.");
    Console.WriteLine("Install the .NET nanoFramework Visual Studio extension first:");
    Console.WriteLine("  https://marketplace.visualstudio.com/items?itemName=nanoframework.nanoFramework-VS2022-Extension");
    return 1;
}
Console.WriteLine($"Using VS bundled DLL: {dllPath}");

var asm = Assembly.LoadFrom(dllPath);

// Collect .pe files: bin/Debug first (covers SpawnWear.pe + standard refs), then
// scan packages/*/lib/*.pe to pick up package-shipped PEs that MSBuild's nfproj
// targets failed to copy (warning MSB3030 fires for any package missing a .pdbx
// file, which silently breaks the .pe copy step). De-dupe by filename, bin/Debug
// wins on collision (project's own build output is freshest).
var peByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var f in Directory.GetFiles(binDir, "*.pe", SearchOption.TopDirectoryOnly))
{
    peByName[Path.GetFileName(f)] = f;
}

// Scan ../packages OR the project parent's packages folder for additional .pe files.
// Resolve relative to binDir: typical nfproj layout is <repo>/<proj>/bin/Debug, with
// packages at <repo>/packages.
var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(binDir));
var packagesDir = projectDir != null ? Path.Combine(Path.GetDirectoryName(projectDir) ?? "", "packages") : null;
if (packagesDir != null && Directory.Exists(packagesDir))
{
    foreach (var f in Directory.GetFiles(packagesDir, "*.pe", SearchOption.AllDirectories))
    {
        var name = Path.GetFileName(f);
        if (!peByName.ContainsKey(name)) peByName[name] = f;
    }
}

var peFiles = peByName.Values.OrderBy(p => Path.GetFileName(p)).ToArray();
if (peFiles.Length == 0)
{
    Console.WriteLine($"No .pe files found in {binDir} or packages/. Build the project first.");
    return 1;
}
Console.WriteLine($"Found {peFiles.Length} .pe assemblies (bin + packages).");
foreach (var p in peFiles)
{
    var fi = new FileInfo(p);
    var src = p.Contains("packages", StringComparison.OrdinalIgnoreCase) ? "[pkg]" : "[bin]";
    Console.WriteLine($"  {src} {fi.Name,-50} {fi.Length,8} bytes  {fi.LastWriteTime:HH:mm:ss}");
}

// Reflect: PortBase.CreateInstanceForSerial(true)
var portBaseType = asm.GetType("nanoFramework.Tools.Debugger.PortBase");
var createForSerial = portBaseType.GetMethod("CreateInstanceForSerial", new[] { typeof(bool) });
object portBase = createForSerial.Invoke(null, new object[] { true });

// Get NanoFrameworkDevices observable collection (it implements IList).
var nanoDevicesProp = portBaseType.GetProperty("NanoFrameworkDevices");
var devicesList = (System.Collections.IList)nanoDevicesProp.GetValue(portBase);
for (int i = 0; i < 40 && devicesList.Count == 0; i++)
{
    await Task.Delay(250);
}
Console.WriteLine($"Discovered {devicesList.Count} device(s).");

object device = null;
foreach (var d in devicesList)
{
    var connectionId = (string)d.GetType().GetProperty("ConnectionId").GetValue(d);
    var description = (string)d.GetType().GetProperty("Description").GetValue(d);
    Console.WriteLine($"  {connectionId} - {description}");
    if (connectionId.IndexOf(port, StringComparison.OrdinalIgnoreCase) >= 0)
    {
        device = d;
        break;
    }
}
if (device == null)
{
    Console.WriteLine($"No nanoFramework device on {port}.");
    return 1;
}
var deviceDesc = (string)device.GetType().GetProperty("Description").GetValue(device);
Console.WriteLine($"Selected: {deviceDesc}");

var debugEngineProp = device.GetType().GetProperty("DebugEngine");
object engine = debugEngineProp.GetValue(device);
var engineType = engine.GetType();

// VS-bundled DLL has Connect(int millisecondsTimeout, bool force, bool requestCapabilities)
var connectMi = engineType.GetMethods()
    .Where(m => m.Name == "Connect" && m.GetParameters().Length == 3)
    .FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(int) &&
                          m.GetParameters()[1].ParameterType == typeof(bool) &&
                          m.GetParameters()[2].ParameterType == typeof(bool));
if (connectMi == null)
{
    // Fall back to (bool, bool) overload + default timeout, or just () overload
    connectMi = engineType.GetMethods()
        .Where(m => m.Name == "Connect" && m.GetParameters().Length == 2)
        .FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(bool) && m.GetParameters()[1].ParameterType == typeof(bool));
}
if (connectMi == null)
{
    Console.WriteLine("Could not find a Connect overload on Engine - VS DLL ABI mismatch.");
    return 1;
}
object[] connectArgs = connectMi.GetParameters().Length == 3
    ? new object[] { 5000, true, true }
    : new object[] { true, true };
bool connected = (bool)connectMi.Invoke(engine, connectArgs);
if (!connected) { Console.WriteLine("Connect failed."); return 1; }
Console.WriteLine("Connected.");

// OnMessage event - signature: (IncomingMessage, string)
var onMessage = engineType.GetEvent("OnMessage");
var handlerType = onMessage.EventHandlerType;
var invokeMethod = handlerType.GetMethod("Invoke");
// Build a delegate that prints text. We use reflection to find the parameter shape.
var paramTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();
// Use a closure delegate: Delegate.CreateDelegate against a static method we provide.
// Easier: use Action<object, string> and fit it via DynamicInvoke fallback...
// Simpler approach: inspect handlerType, expect (IncomingMessage, string text).
// We'll use Reflection.Emit-free path: Delegate.CreateDelegate from MethodInfo.

void OnMessageStaticAdapter(object msg, string text)
{
    var line = (text ?? "").TrimEnd('\r', '\n');
    if (!string.IsNullOrEmpty(line)) Console.WriteLine($"[runtime] {line}");
}
// Wrap our local function in a delegate matching handlerType.
// handlerType expected to be MessageHandler(IncomingMessage, string) - parameter[0] is non-string.
var localFnInfo = ((Delegate)(Action<object, string>)OnMessageStaticAdapter).Method;
var del = Delegate.CreateDelegate(handlerType, ((Action<object, string>)OnMessageStaticAdapter).Target, localFnInfo);
onMessage.AddEventHandler(engine, del);

// DeploymentExecute(List<byte[]>, bool, bool, IProgress<MessageWithProgress>, IProgress<string>)
var deploymentExecute = engineType.GetMethod("DeploymentExecute", new[]
{
    typeof(List<byte[]>),
    typeof(bool),
    typeof(bool),
    typeof(IProgress<>).MakeGenericType(asm.GetType("nanoFramework.Tools.Debugger.MessageWithProgress")),
    typeof(IProgress<string>),
});
if (deploymentExecute == null)
{
    // Try to find by name regardless of signature
    deploymentExecute = engineType.GetMethods().FirstOrDefault(m => m.Name == "DeploymentExecute" && m.GetParameters().Length >= 3);
    if (deploymentExecute == null)
    {
        Console.WriteLine("DeploymentExecute method not found on Engine.");
        return 1;
    }
    Console.WriteLine($"Using DeploymentExecute overload with {deploymentExecute.GetParameters().Length} parameters.");
}

var assemblyBytes = peFiles.Select(p => File.ReadAllBytes(p)).ToList();
var paramsForDeploy = deploymentExecute.GetParameters();
object[] callArgs = new object[paramsForDeploy.Length];
callArgs[0] = assemblyBytes;
callArgs[1] = true;  // rebootAfterDeploy
if (paramsForDeploy.Length >= 3) callArgs[2] = false; // skipErase
for (int i = 3; i < paramsForDeploy.Length; i++) callArgs[i] = null;

Console.WriteLine($"Deploying {assemblyBytes.Count} assemblies via VS DLL DeploymentExecute (reboot=true)...");
bool ok;
try
{
    ok = (bool)deploymentExecute.Invoke(engine, callArgs);
}
catch (TargetInvocationException tie)
{
    Console.WriteLine($"DeploymentExecute threw: {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
    return 1;
}
if (!ok)
{
    Console.WriteLine("DeploymentExecute returned false.");
    return 1;
}

Console.WriteLine($"Deploy + reboot OK. Capturing Debug.WriteLine output for {captureSeconds}s...");
await Task.Delay(captureSeconds * 1000);
Console.WriteLine("Done.");
return 0;
