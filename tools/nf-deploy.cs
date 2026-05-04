#:package System.IO.Ports@9.0.0

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
// We bring System.IO.Ports along for the ride because the VS DLL's
// PortSerialManager has it as a transitive dependency that .NET 10 file-scripts
// don't pick up automatically when loading the DLL via Assembly.LoadFrom.
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

// Note: the actual ceiling check is deferred to AFTER we've assembled the full
// .pe list (bin/Debug + active package references) below, since some assemblies
// (e.g. nanoFramework.Device.Bluetooth from packages/) only show up there.

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

// Collect .pe files: bin/Debug first (covers SpawnWear.pe + the standard refs that
// MSBuild successfully copies), then for any nfproj Reference whose .pe is NOT in
// bin/Debug we grab it from packages/ (covers the spawnwear-1 packages where
// missing .pdbx breaks MSBuild's .pe copy step).
//
// We do NOT blindly scan packages/ for every .pe - doing that includes assemblies
// the user app doesn't reference, which still get deployed and load into the runtime
// heap, which can starve the BLE host stack and cause OOM at GattLocalCharacteristic
// allocation time. The .nfproj Reference list is the right allow-list.
var peByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var f in Directory.GetFiles(binDir, "*.pe", SearchOption.TopDirectoryOnly))
{
    peByName[Path.GetFileName(f)] = f;
}

var projectDir = Path.GetDirectoryName(Path.GetDirectoryName(binDir));
var nfproj = projectDir != null ? Directory.GetFiles(projectDir, "*.nfproj").FirstOrDefault() : null;
var allowedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (nfproj != null)
{
    // Strip XML comments before matching so Reference elements inside <!-- ... -->
    // don't get added to the allow-list. Without this, commenting out a
    // <Reference Include="..."> still pulled the .pe from packages/, defeating
    // the whole point of trimming references to stay under the deploy ceiling.
    var nfprojXml = File.ReadAllText(nfproj);
    var stripped = System.Text.RegularExpressions.Regex.Replace(
        nfprojXml,
        @"<!--.*?-->",
        "",
        System.Text.RegularExpressions.RegexOptions.Singleline);
    foreach (var line in stripped.Split('\n'))
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"<Reference\s+Include=""([^""]+)""");
        if (m.Success) allowedAssemblies.Add(m.Groups[1].Value.Trim());
    }
    if (allowedAssemblies.Count > 0)
    {
        Console.WriteLine($"Project references {allowedAssemblies.Count} assemblies from {Path.GetFileName(nfproj)}");
    }
}

var packagesDir = projectDir != null ? Path.Combine(Path.GetDirectoryName(projectDir) ?? "", "packages") : null;
if (packagesDir != null && Directory.Exists(packagesDir))
{
    foreach (var f in Directory.GetFiles(packagesDir, "*.pe", SearchOption.AllDirectories))
    {
        var name = Path.GetFileName(f);
        var assemblyName = Path.GetFileNameWithoutExtension(name);
        // If we have a nfproj allow-list, gate package .pe by it. Otherwise include all
        // (preserves old behavior when called from outside an nfproj-tree).
        if (allowedAssemblies.Count > 0 && !allowedAssemblies.Contains(assemblyName)) continue;
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
long peTotalForCheck = 0;
foreach (var p in peFiles)
{
    var fi = new FileInfo(p);
    var src = p.Contains("packages", StringComparison.OrdinalIgnoreCase) ? "[pkg]" : "[bin]";
    Console.WriteLine($"  {src} {fi.Name,-50} {fi.Length,8} bytes  {fi.LastWriteTime:HH:mm:ss}");
    peTotalForCheck += fi.Length;
}

// Deploy ceiling guard. Holdover from the 2026-05-04 corruption-at-242 KB
// observations. Resolution 2026-05-05 11:00: rebuilt the nf-interpreter
// firmware from current source on the LostBeard fork's
// feature/qspi-display-driver branch; deployed 295 KB cleanly with all 17
// assemblies (including BLE) intact. The previously-flashed firmware was
// older than commit 89a4a947 (Bitmap CO5300 alignment, 2026-05-03 20:27)
// and missed at least one fix that affected deploys. With current
// firmware the deploy region is the full 2.94 MB partition.
//
// Keeping a generous 2 MB ceiling as a sanity guard against runaway
// deploys; raise if a legitimate use case needs more.
const int DeployCeilingBytes = 2000000;
Console.WriteLine($"deploy size: {peTotalForCheck} bytes (active .pe sum); ceiling {DeployCeilingBytes}; headroom {DeployCeilingBytes - peTotalForCheck}");
if (peTotalForCheck > DeployCeilingBytes)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"** DEPLOY-CEILING ALERT: total .pe = {peTotalForCheck} > ceiling {DeployCeilingBytes} **");
    Console.Error.WriteLine($"** This deploy will likely CORRUPT the on-flash assembly table. **");
    Console.Error.WriteLine($"** Trim a feature or fix nf-interpreter's deploy commit path before retrying. **");
    Console.Error.WriteLine($"** See feedback_nf_deploy_ceiling_298kb.md (agent memory) for context. **");
    return 1;
}

// Reflect: PortBase.CreateInstanceForSerial(false) - we don't need the watcher,
// we'll register the device explicitly by COM port via AddDevice. This avoids all
// the watcher-timing flakiness that has been biting us after multiple failed deploys.
var portBaseType = asm.GetType("nanoFramework.Tools.Debugger.PortBase");
var createForSerial = portBaseType.GetMethod("CreateInstanceForSerial", new[] { typeof(bool) });
object portBase = createForSerial.Invoke(null, new object[] { false });

var addDeviceMi = portBaseType.GetMethod("AddDevice", new[] { typeof(string) });
object device = addDeviceMi.Invoke(portBase, new object[] { port });
if (device == null)
{
    Console.WriteLine($"AddDevice({port}) returned null - device not present or holder of port.");
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
// Bump DefaultTimeout BEFORE Connect - the deploy's last commit block (the 824-byte
// trailer) needs the runtime to flush flash + checksum, which takes longer than the
// default wire-protocol timeout. Empirically reached "Error writing 824 bytes ... No
// reply from nanoDevice" at the deploy tail; bigger timeout gives the runtime time
// to finalize.
var defaultTimeoutProp = engineType.GetProperty("DefaultTimeout");
if (defaultTimeoutProp != null && defaultTimeoutProp.CanWrite)
{
    defaultTimeoutProp.SetValue(engine, 30000);
    Console.WriteLine("Set DefaultTimeout=30000ms");
}

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
callArgs[1] = true;   // rebootAfterDeploy - let DeploymentExecute handle the reboot, same as VS
if (paramsForDeploy.Length >= 3) callArgs[2] = false; // skipErase = false (full erase + write)

// Build IProgress<MessageWithProgress> + IProgress<string> instances so the deploy
// internals stream their per-step status back. Without these the only visible failure
// mode was "DeploymentExecute returned false" with no detail.
for (int i = 3; i < paramsForDeploy.Length; i++)
{
    var pType = paramsForDeploy[i].ParameterType;
    if (pType.IsGenericType && pType.GetGenericTypeDefinition() == typeof(IProgress<>))
    {
        var argType = pType.GetGenericArguments()[0];
        if (argType == typeof(string))
        {
            callArgs[i] = new ReflectProgress<string>(s => Console.WriteLine($"[deploy-log] {s}"));
        }
        else
        {
            callArgs[i] = Activator.CreateInstance(typeof(ReflectProgressUntyped<>).MakeGenericType(argType), new object[] { (Action<object>)(o => Console.WriteLine($"[deploy-progress] {o}")) });
        }
    }
    else
    {
        callArgs[i] = null;
    }
}

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

// rebootAfterDeploy=true above means DeploymentExecute already restarted the CLR.
// Same path VS uses; no manual reboot needed (and manual reboot was leaving the
// runtime in a state where Main wasn't actually re-running fresh).
Console.WriteLine($"Deploy + reboot OK. Capturing Debug.WriteLine output for {captureSeconds}s...");
await Task.Delay(captureSeconds * 1000);
Console.WriteLine("Done.");
return 0;

class ReflectProgress<T> : IProgress<T>
{
    readonly Action<T> _on;
    public ReflectProgress(Action<T> on) { _on = on; }
    public void Report(T value) => _on(value);
}

class ReflectProgressUntyped<T> : IProgress<T>
{
    readonly Action<object> _on;
    public ReflectProgressUntyped(Action<object> on) { _on = on; }
    public void Report(T value) => _on(value);
}
