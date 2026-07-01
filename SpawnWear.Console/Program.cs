using System.Text;
using Microsoft.Extensions.Logging;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Cryptography.DotNet;
using SpawnWear.Bridge;
using SpawnWear.Bridge.WebRtc;

// SpawnWear console companion - manage the watch over WebRTC (via SpawnWear.Bridge).
//   App management (channel sys.apps):
//     list                       list installed apps
//     install <name> <file.pe>   install an app (single-frame - small apps only; big transfers use sys.files)
//     uninstall <name>           remove an app
//     launch <name>              launch an app on the watch
//   SD card files (channel sys.files), paths relative to the SD mount D:\ :
//     ls [path]                  list a directory (default = root)
//     stat <path>                file/dir info
//     get <remote> [local]       download a file from the SD card (chunked)
//     put <local> <remote>       upload a file to the SD card (chunked)
//     rm <path>                  delete a file or directory (recursive)
//     mkdir <path>               create a directory (parents included)
//   [--room <ascii>]             override the room (default = the watch's unpaired dev room)
//   [--verbose|-v]               SipSorcery debug logging
//
// Connects with the dev/self-test identity (WebRtcSelfTestPairing), which matches the watch's
// UNPAIRED identity (room "SWclean0623pmRoom01x").

// The watch's constrained embedded ICE answers connectivity checks ~20s in; SipSorcery's default 16s
// FAILED timeout fires first and corrupts the handshake. Bump it exactly as SpawnWear.Bridge.Desktop does.
SIPSorcery.Net.RtpIceChannel.FAILED_TIMEOUT_PERIOD = 30;
SIPSorcery.Net.RtpIceChannel.DISCONNECTED_TIMEOUT_PERIOD = 20;

if (args.Length == 0) { Usage(); return 1; }

string room = "SWclean0623pmRoom01x"; // watch's unpaired test room
bool verbose = false;
var pos = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--room" && i + 1 < args.Length) room = args[++i];
    else if (args[i] == "--verbose" || args[i] == "-v") verbose = true;
    else pos.Add(args[i]);
}
if (verbose)
{
    SIPSorcery.LogFactory.Set(LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
         .SetMinimumLevel(LogLevel.Debug)));
}
if (pos.Count == 0) { Usage(); return 1; }
string cmd = pos[0].ToLowerInvariant();

const int Chunk = 480; // must match the watch's SysFileChunk (sized so a read reply fits the 512-byte watch TX clamp)

var crypto = new DotNetCrypto();
var opts = new BridgeWebRtcOptions();
var factory = new WebRtcTransportFactory(opts, crypto, RandomPeerId());
var record = WebRtcSelfTestPairing.CompanionRecord() with { RoomKey = Encoding.ASCII.GetBytes(room) };
// Stop-and-wait reply plumbing: one outstanding request per channel at a time.
var pending = new Dictionary<string, TaskCompletionSource<byte[]>>();
var pendingLock = new object();
WebRtcTransport peer = null;

async Task<byte[]> SendRecv(string channel, byte[] payload, int timeoutMs = 15000)
{
    var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    lock (pendingLock) { pending[channel] = tcs; }
    await peer.SendAsync(new TransportMessage(channel, payload), CancellationToken.None);
    return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
}

// Connect with retry: the watch's constrained ICE is flaky (~50% per attempt); a fresh transport
// usually connects within a couple of tries. A failed attempt is disposed and replaced - important
// for multi-step ops (installpkg/getpkg) that do everything over one connection.
bool connected = false;
var connectSw = System.Diagnostics.Stopwatch.StartNew();  // TIMING: wall-clock to a successful connect
for (int attempt = 1; attempt <= 3 && !connected; attempt++)
{
    peer = factory.CreateTransport(record);
    peer.MessageReceived += m =>
    {
        TaskCompletionSource<byte[]>? tcs = null;
        lock (pendingLock) { if (pending.TryGetValue(m.ChannelId, out tcs)) pending.Remove(m.ChannelId); }
        tcs?.TrySetResult(m.Payload);
    };
    peer.ConnectionChanged += c => Console.WriteLine($"[console] connection changed: connected={c}");
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Console.WriteLine($"[console] connecting to watch (room='{room}', attempt {attempt}/3)...");
        await peer.ConnectAsync(cts.Token);
        connected = true;
        connectSw.Stop();
        Console.WriteLine($"[console] CONNECT_MS={connectSw.ElapsedMilliseconds} attempts={attempt}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[console] connect attempt {attempt} failed: {ex.GetType().Name}");
        try { await peer.DisposeAsync(); } catch { }
        peer = null;
        lock (pendingLock) { pending.Clear(); }
        if (attempt < 3) await Task.Delay(2000);
    }
}
if (!connected || peer == null) { Console.WriteLine("[console] FAIL - could not connect after retries"); return 1; }

try
{
    Console.WriteLine($"[console] connected + verified; running '{cmd}'...");

    switch (cmd)
    {
        case "list": PrintAppList(await SendRecv("sys.apps", new byte[] { 1 })); break;
        case "install":
            if (pos.Count < 3) { Usage(); return 1; }
            if (!File.Exists(pos[2])) { Console.WriteLine($"[console] file not found: {pos[2]}"); return 1; }
            PrintText(await SendRecv("sys.apps", BuildInstall(pos[1], File.ReadAllBytes(pos[2])), 30000));
            break;
        case "uninstall":
            if (pos.Count < 2) { Usage(); return 1; }
            PrintText(await SendRecv("sys.apps", BuildNameOp(3, pos[1]))); break;
        case "launch":
            if (pos.Count < 2) { Usage(); return 1; }
            PrintText(await SendRecv("sys.apps", BuildNameOp(4, pos[1]))); break;

        case "hold":
            // Leak-hunt: hold the connection open N seconds so the watch streams SUSTAINED telemetry
            // (imu/battery/demo) the whole time - the condition the ~1130B/session leak needs. The watch
            // logs freeInt/freePsram at session start and post-close; the delta over a long hold reveals
            // any duration-proportional leak that a quick list/get is too short to surface.
            int holdSec = pos.Count > 1 ? int.Parse(pos[1]) : 60;
            Console.WriteLine($"[console] holding connection {holdSec}s (watch streams telemetry)...");
            await Task.Delay(holdSec * 1000);
            Console.WriteLine("[console] hold complete, disconnecting");
            break;

        case "ls": await ListDir(pos.Count > 1 ? pos[1] : ""); break;
        case "stat":
            if (pos.Count < 2) { Usage(); return 1; }
            PrintStat(await SendRecv("sys.files", BuildFileOp(2, pos[1]))); break;
        case "rm":
            if (pos.Count < 2) { Usage(); return 1; }
            PrintText(await SendRecv("sys.files", BuildFileOp(5, pos[1]))); break;
        case "mkdir":
            if (pos.Count < 2) { Usage(); return 1; }
            PrintText(await SendRecv("sys.files", BuildFileOp(6, pos[1]))); break;
        case "mv":
            if (pos.Count < 3) { Usage(); return 1; }
            PrintText(await SendRecv("sys.files", BuildMove(pos[1], pos[2]))); break;
        case "get":
            if (pos.Count < 2) { Usage(); return 1; }
            await GetFile(pos[1], pos.Count > 2 ? pos[2] : Path.GetFileName(pos[1].Replace('\\', '/'))); break;
        case "put":
            if (pos.Count < 3) { Usage(); return 1; }
            if (!File.Exists(pos[1])) { Console.WriteLine($"[console] file not found: {pos[1]}"); return 1; }
            await PutFile(pos[1], pos[2]); break;
        case "installpkg":
            if (pos.Count < 2) { Usage(); return 1; }
            if (!Directory.Exists(pos[1])) { Console.WriteLine($"[console] dir not found: {pos[1]}"); return 1; }
            await InstallPkg(pos[1], pos.Count > 2 ? pos[2] : Path.GetFileName(pos[1].TrimEnd('\\', '/'))); break;
        case "getpkg":
            if (pos.Count < 3) { Usage(); return 1; }
            await GetPkg(pos[1], pos[2]); break;
        default: Usage(); return 1;
    }

    await peer.DisconnectAsync();
    await peer.DisposeAsync();
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"[console] FAIL - {ex.GetType().Name}: {ex.Message}");
    return 1;
}

// ---- sys.files chunked transfers ----
async Task GetFile(string remote, string local)
{
    uint offset = 0;
    using var outFs = File.Create(local);
    while (true)
    {
        byte[] reply = await SendRecv("sys.files", BuildRead(remote, offset, Chunk));
        if (reply.Length < 5 || reply[0] != 3 || reply[1] != 1) { PrintText(reply); return; }
        int dataLen = reply[3] | (reply[4] << 8);
        if (dataLen > 0) outFs.Write(reply, 5, dataLen);
        offset += (uint)dataLen;
        if (reply[2] == 1 || dataLen == 0) break;
    }
    Console.WriteLine($"[console] got {offset} bytes -> {local}");
}

async Task PutFile(string local, string remote)
{
    byte[] content = File.ReadAllBytes(local);
    uint offset = 0;
    if (content.Length == 0)
    {
        PrintText(await SendRecv("sys.files", BuildWrite(remote, 0, 0x01, content, 0, 0)));
        Console.WriteLine($"[console] put 0 bytes -> {remote}");
        return;
    }
    while (offset < content.Length)
    {
        int n = Math.Min(Chunk, content.Length - (int)offset);
        byte flags = offset == 0 ? (byte)0x01 : (byte)0x00; // truncate on first chunk
        byte[] reply = await SendRecv("sys.files", BuildWrite(remote, offset, flags, content, (int)offset, n));
        if (reply.Length < 2 || reply[0] != 4 || reply[1] != 1) { PrintText(reply); return; }
        offset += (uint)n;
    }
    Console.WriteLine($"[console] put {offset} bytes -> {remote}");
}

// Page through a directory listing, returning every entry (name, isDir, size).
async Task<List<(string name, bool isDir, uint size)>> ListDirEntries(string path)
{
    var entries = new List<(string, bool, uint)>();
    int startIdx = 0;
    while (true)
    {
        byte[] r = await SendRecv("sys.files", BuildListDir(path, startIdx));
        if (r.Length < 5 || r[0] != 1 || r[1] != 1) break; // error / not a dir -> stop
        byte more = r[2];
        int count = r[3] | (r[4] << 8);
        int o = 5;
        for (int i = 0; i < count && o < r.Length; i++)
        {
            int nl = r[o++];
            string name = Encoding.UTF8.GetString(r, o, nl); o += nl;
            bool isDir = r[o++] != 0;
            uint sz = (uint)(r[o] | (r[o + 1] << 8) | (r[o + 2] << 16) | (r[o + 3] << 24)); o += 4;
            entries.Add((name, isDir, sz));
        }
        startIdx += count;
        if (more == 0 || count == 0) break;
    }
    return entries;
}

async Task ListDir(string path)
{
    var entries = await ListDirEntries(path);
    foreach (var (name, isDir, size) in entries)
        Console.WriteLine(isDir ? $"  {name}/" : $"  {name}  ({size} bytes)");
    Console.WriteLine($"[console] {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} in {(path.Length == 0 ? "D:\\" : path)}");
}

// Install a whole app PACKAGE: upload a local directory tree to D:\apps\<id>\ over sys.files
// (mkdir + chunked put per file). The package is loose files (app.pe + manifest.json + icon +
// assets), so the watch ends up with exactly the dir-per-app layout.
async Task InstallPkg(string localDir, string id)
{
    string baseRemote = "apps/" + id;
    await SendRecv("sys.files", BuildFileOp(6, baseRemote)); // mkdir the app dir
    string[] files = Directory.GetFiles(localDir, "*", SearchOption.AllDirectories);
    int n = 0; long bytes = 0;
    foreach (string f in files)
    {
        string rel = Path.GetRelativePath(localDir, f).Replace('\\', '/');
        string remote = baseRemote + "/" + rel;
        int slash = remote.LastIndexOf('/');
        string parent = remote.Substring(0, slash);
        if (parent != baseRemote) await SendRecv("sys.files", BuildFileOp(6, parent)); // nested asset dir
        bytes += new FileInfo(f).Length;
        await PutFile(f, remote);
        n++;
    }
    Console.WriteLine($"[console] installed package '{id}': {n} file(s), {bytes} bytes -> {baseRemote}");
}

// Download a whole app package: recursively pull D:\apps\<id>\ to a local directory.
async Task GetPkg(string id, string localDir)
{
    await GetDirRecursive("apps/" + id, localDir);
    Console.WriteLine($"[console] downloaded package '{id}' -> {localDir}");
}

async Task GetDirRecursive(string remoteDir, string localDir)
{
    Directory.CreateDirectory(localDir);
    foreach (var (name, isDir, size) in await ListDirEntries(remoteDir))
    {
        if (isDir) await GetDirRecursive(remoteDir + "/" + name, Path.Combine(localDir, name));
        else await GetFile(remoteDir + "/" + name, Path.Combine(localDir, name));
    }
}

// ---- request builders ----
static byte[] BuildNameOp(byte op, string name)
{
    byte[] nb = Encoding.UTF8.GetBytes(name);
    byte[] r = new byte[2 + nb.Length];
    r[0] = op; r[1] = (byte)nb.Length;
    Array.Copy(nb, 0, r, 2, nb.Length);
    return r;
}

static byte[] BuildInstall(string name, byte[] pe)
{
    byte[] nb = Encoding.UTF8.GetBytes(name);
    byte[] r = new byte[2 + nb.Length + pe.Length];
    r[0] = 2; r[1] = (byte)nb.Length;
    Array.Copy(nb, 0, r, 2, nb.Length);
    Array.Copy(pe, 0, r, 2 + nb.Length, pe.Length);
    return r;
}

// [op][pathLen][path]
static byte[] BuildFileOp(byte op, string path)
{
    byte[] pb = Encoding.UTF8.GetBytes(path);
    byte[] r = new byte[2 + pb.Length];
    r[0] = op; r[1] = (byte)pb.Length;
    Array.Copy(pb, 0, r, 2, pb.Length);
    return r;
}

// [7][pathLen][path][newLen][newPath]
static byte[] BuildMove(string path, string newPath)
{
    byte[] pb = Encoding.UTF8.GetBytes(path);
    byte[] nb = Encoding.UTF8.GetBytes(newPath);
    byte[] r = new byte[2 + pb.Length + 1 + nb.Length];
    int o = 0;
    r[o++] = 7; r[o++] = (byte)pb.Length;
    Array.Copy(pb, 0, r, o, pb.Length); o += pb.Length;
    r[o++] = (byte)nb.Length;
    Array.Copy(nb, 0, r, o, nb.Length);
    return r;
}

// [1][pathLen][path][startIdx:u16 LE]
static byte[] BuildListDir(string path, int startIdx)
{
    byte[] pb = Encoding.UTF8.GetBytes(path);
    byte[] r = new byte[2 + pb.Length + 2];
    int o = 0;
    r[o++] = 1; r[o++] = (byte)pb.Length;
    Array.Copy(pb, 0, r, o, pb.Length); o += pb.Length;
    r[o++] = (byte)(startIdx & 0xFF); r[o++] = (byte)((startIdx >> 8) & 0xFF);
    return r;
}

// [3][pathLen][path][offset:u32 LE][len:u16 LE]
static byte[] BuildRead(string path, uint offset, int len)
{
    byte[] pb = Encoding.UTF8.GetBytes(path);
    byte[] r = new byte[2 + pb.Length + 6];
    int o = 0;
    r[o++] = 3; r[o++] = (byte)pb.Length;
    Array.Copy(pb, 0, r, o, pb.Length); o += pb.Length;
    r[o++] = (byte)(offset & 0xFF); r[o++] = (byte)((offset >> 8) & 0xFF); r[o++] = (byte)((offset >> 16) & 0xFF); r[o++] = (byte)((offset >> 24) & 0xFF);
    r[o++] = (byte)(len & 0xFF); r[o++] = (byte)((len >> 8) & 0xFF);
    return r;
}

// [4][pathLen][path][offset:u32 LE][flags][dataLen:u16 LE][data]
static byte[] BuildWrite(string path, uint offset, byte flags, byte[] data, int dataOff, int dataLen)
{
    byte[] pb = Encoding.UTF8.GetBytes(path);
    byte[] r = new byte[2 + pb.Length + 7 + dataLen];
    int o = 0;
    r[o++] = 4; r[o++] = (byte)pb.Length;
    Array.Copy(pb, 0, r, o, pb.Length); o += pb.Length;
    r[o++] = (byte)(offset & 0xFF); r[o++] = (byte)((offset >> 8) & 0xFF); r[o++] = (byte)((offset >> 16) & 0xFF); r[o++] = (byte)((offset >> 24) & 0xFF);
    r[o++] = flags;
    r[o++] = (byte)(dataLen & 0xFF); r[o++] = (byte)((dataLen >> 8) & 0xFF);
    if (dataLen > 0) Array.Copy(data, dataOff, r, o, dataLen);
    return r;
}

// ---- reply printers ----
static void PrintAppList(byte[] r)
{
    if (r.Length < 3 || r[0] != 1) { PrintText(r); return; }
    int count = r[2];
    Console.WriteLine($"[console] {count} app(s) installed:");
    int o = 3;
    for (int i = 0; i < count && o < r.Length; i++)
    {
        int nl = r[o++];
        string name = Encoding.UTF8.GetString(r, o, nl); o += nl;
        uint sz = (uint)(r[o] | (r[o + 1] << 8) | (r[o + 2] << 16) | (r[o + 3] << 24)); o += 4;
        Console.WriteLine($"  {name}  ({sz} bytes)");
    }
}

// [2][1][exists][isDir][size:u32 LE]
static void PrintStat(byte[] r)
{
    if (r.Length < 8 || r[0] != 2 || r[1] != 1) { PrintText(r); return; }
    if (r[2] == 0) { Console.WriteLine("[console] not found"); return; }
    uint sz = (uint)(r[4] | (r[5] << 8) | (r[6] << 16) | (r[7] << 24));
    Console.WriteLine(r[3] != 0 ? "[console] <dir>" : $"[console] file, {sz} bytes");
}

// [op][ok][msgLen][msg]
static void PrintText(byte[] r)
{
    if (r.Length < 2) { Console.WriteLine("[console] (empty reply)"); return; }
    int ml = r.Length > 2 ? r[2] : 0;
    string msg = ml > 0 && r.Length >= 3 + ml ? Encoding.UTF8.GetString(r, 3, ml) : "";
    Console.WriteLine($"[console] {(r[1] == 1 ? "OK" : "ERROR")}: {msg}");
}

static byte[] RandomPeerId()
{
    byte[] id = new byte[20];
    byte[] prefix = Encoding.ASCII.GetBytes("-SW0001-");
    Array.Copy(prefix, id, prefix.Length);
    Random.Shared.NextBytes(id.AsSpan(prefix.Length));
    return id;
}

static void Usage()
{
    Console.WriteLine("SpawnWear console companion - manage the watch over WebRTC");
    Console.WriteLine("App management (sys.apps):");
    Console.WriteLine("  list                          list installed apps");
    Console.WriteLine("  install <name> <file.pe>      install an app (small apps only)");
    Console.WriteLine("  uninstall <name>              remove an app");
    Console.WriteLine("  launch <name>                 launch an app on the watch");
    Console.WriteLine("SD card files (sys.files), paths relative to D:\\ :");
    Console.WriteLine("  ls [path]                     list a directory (default root)");
    Console.WriteLine("  stat <path>                   file/dir info");
    Console.WriteLine("  get <remote> [local]          download a file");
    Console.WriteLine("  put <local> <remote>          upload a file");
    Console.WriteLine("  rm <path>                     delete a file or directory");
    Console.WriteLine("  mkdir <path>                  create a directory");
    Console.WriteLine("  mv <old> <new>                rename/move a file or directory");
    Console.WriteLine("App packages (dir of loose files under D:\\apps\\<id>):");
    Console.WriteLine("  installpkg <localDir> [id]    upload a local app folder as a package");
    Console.WriteLine("  getpkg <id> <localDir>        download an installed app package");
    Console.WriteLine("  [--room <ascii>] [--verbose]");
}
