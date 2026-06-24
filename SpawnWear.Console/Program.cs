using System.Text;
using Microsoft.Extensions.Logging;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Cryptography.DotNet;
using SpawnWear.Bridge;
using SpawnWear.Bridge.WebRtc;

// SpawnWear console companion - manage the watch over WebRTC (via SpawnWear.Bridge).
//   list                       list installed apps
//   install <name> <file.pe>   install an app
//   uninstall <name>           remove an app
//   launch <name>              launch an app on the watch
//   [--room <ascii>]           override the room (default = the watch's unpaired dev room)
//
// Connects with the dev/self-test identity (WebRtcSelfTestPairing), which matches the watch's
// UNPAIRED identity (room "SWclean0623pmRoom01x"). A BLE-paired identity (the aligned path) comes later.

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

byte[]? req;
switch (cmd)
{
    case "list": req = new byte[] { 1 }; break;
    case "install":
        if (pos.Count < 3) { Usage(); return 1; }
        if (!File.Exists(pos[2])) { Console.WriteLine($"[console] file not found: {pos[2]}"); return 1; }
        req = BuildInstall(pos[1], File.ReadAllBytes(pos[2]));
        break;
    case "uninstall":
        if (pos.Count < 2) { Usage(); return 1; }
        req = BuildNameOp(3, pos[1]);
        break;
    case "launch":
        if (pos.Count < 2) { Usage(); return 1; }
        req = BuildNameOp(4, pos[1]);
        break;
    default: Usage(); return 1;
}

var crypto = new DotNetCrypto();
var opts = new BridgeWebRtcOptions();
var factory = new WebRtcTransportFactory(opts, crypto, RandomPeerId());
var record = WebRtcSelfTestPairing.CompanionRecord() with { RoomKey = Encoding.ASCII.GetBytes(room) };
await using var peer = factory.CreateTransport(record);

var replyTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
peer.MessageReceived += m => { if (m.ChannelId == "sys.apps") replyTcs.TrySetResult(m.Payload); };
peer.ConnectionChanged += c => Console.WriteLine($"[console] connection changed: connected={c}");

try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.WriteLine($"[console] connecting to watch (room='{room}')...");
    await peer.ConnectAsync(cts.Token);
    Console.WriteLine($"[console] connected + verified; sending '{cmd}'...");
    await peer.SendAsync(new TransportMessage("sys.apps", req!), cts.Token);
    byte[] reply = await replyTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    PrintReply(reply);
    await peer.DisconnectAsync();
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"[console] FAIL - {ex.GetType().Name}: {ex.Message}");
    return 1;
}

// ---- sys.apps protocol (mirrors the watch's ProcessAppsCommand) ----
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

static void PrintReply(byte[] r)
{
    if (r.Length < 2) { Console.WriteLine("[console] (empty reply)"); return; }
    byte op = r[0], ok = r[1];
    if (op == 1) // LIST: [1][ok][count] then count*{[nameLen][name][size:u32 LE]}
    {
        int count = r.Length > 2 ? r[2] : 0;
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
    else // text reply: [op][ok][msgLen][msg]
    {
        int ml = r.Length > 2 ? r[2] : 0;
        string msg = ml > 0 && r.Length >= 3 + ml ? Encoding.UTF8.GetString(r, 3, ml) : "";
        Console.WriteLine($"[console] {(ok == 1 ? "OK" : "ERROR")}: {msg}");
    }
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
    Console.WriteLine("Usage:");
    Console.WriteLine("  list                          list installed apps");
    Console.WriteLine("  install <name> <file.pe>      install an app");
    Console.WriteLine("  uninstall <name>              remove an app");
    Console.WriteLine("  launch <name>                 launch an app on the watch");
    Console.WriteLine("  [--room <ascii>]              override room (default: unpaired dev room)");
}
