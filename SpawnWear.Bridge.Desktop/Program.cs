using Microsoft.Extensions.Logging;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Cryptography.DotNet;
using SpawnWear.Bridge;
using SpawnWear.Bridge.Pairing;
using SpawnWear.Bridge.WebRtc;

// SpawnWear.Bridge.Desktop - WebRTC self-test (Phase 7 Stage 1a).
//
// Spins up TWO .NET WebRTC peers (a simulated companion + a simulated watch),
// both pointed at the SpawnDev.RTC hub, sharing a freshly generated pairing.
// Proves the entire non-firmware path end-to-end over a REAL hub:
//   hub signaling + SDP/ICE + datachannel open + mutual Ed25519 challenge
//   (WebRtcChallenge) + TransportMessage framing (WebRtcDataFraming).
//
// This is the software de-risk before the watch's native libpeer stack lands:
// if a non-browser WebRTC stack (SipSorcery via SpawnDev.RTC) can rendezvous +
// authenticate + exchange data through the hub, the watch just has to do the
// same thing with libpeer. Run: dotnet run --project SpawnWear.Bridge.Desktop

// Diagnostic: does a desktop (SipSorcery) peer connection embed ICE candidates into
// LocalDescription.Sdp after gathering? The tracker path (RtcPeerConnectionRoomHandler)
// is NON-trickle - it announces LocalDescription.Sdp, so if candidates aren't in it the
// remote peer can't connect. Run: dotnet run --project SpawnWear.Bridge.Desktop -- icediag
if (args.Length > 0 && args[0] == "icediag")
{
    var cfg = new SpawnDev.RTC.RTCPeerConnectionConfig
    {
        IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:hub.spawndev.com:3478", "stun:stun.l.google.com:19302" } } }
    };
    using var diagPc = SpawnDev.RTC.RTCPeerConnectionFactory.Create(cfg);
    int candCount = 0;
    diagPc.OnIceCandidate += _ => System.Threading.Interlocked.Increment(ref candCount);
    diagPc.OnIceGatheringStateChange += s => Console.WriteLine($"[icediag] gatheringState -> {s}");
    using var diagDc = diagPc.CreateDataChannel("diag");
    var diagOffer = await diagPc.CreateOffer();
    await diagPc.SetLocalDescription(diagOffer);
    Console.WriteLine("[icediag] offer set; waiting up to 8s for ICE gathering...");
    for (int i = 0; i < 80 && diagPc.IceGatheringState != "complete"; i++) await Task.Delay(100);
    var sdp = diagPc.LocalDescription?.Sdp ?? "(null)";
    int sdpCands = System.Text.RegularExpressions.Regex.Matches(sdp, "a=candidate").Count;
    Console.WriteLine($"[icediag] gatheringState={diagPc.IceGatheringState}  OnIceCandidate fired={candCount}  a=candidate lines in LocalDescription.Sdp={sdpCands}");
    Console.WriteLine("[icediag] ---- LocalDescription.Sdp ----");
    Console.WriteLine(sdp);
    return 0;
}

// Surface SipSorcery's internal ICE/DTLS/SCTP logs (diagnostic). Set SW_RTC_LOG=1 to enable.
if (Environment.GetEnvironmentVariable("SW_RTC_LOG") == "1")
{
    SIPSorcery.LogFactory.Set(LoggerFactory.Create(b =>
        b.AddConsole().SetMinimumLevel(LogLevel.Debug)));
    Console.WriteLine("[selftest] SipSorcery logging ENABLED");
}

var crypto = new DotNetCrypto();
var options = new BridgeWebRtcOptions(); // default hub: wss://hub.spawndev.com:44365/announce

Console.WriteLine($"[selftest] hub = {options.AnnounceUrl}");
Console.WriteLine("[selftest] minting a fresh test pairing (two Ed25519 identities + shared room key)...");
var (companionRecord, watchRecord) = await MintPairAsync(crypto);
Console.WriteLine($"[selftest] room={Hex(companionRecord.RoomKey)} companionPub={Hex(companionRecord.OurPubKey)} watchPub={Hex(watchRecord.OurPubKey)}");

// Distinct peer ids => distinct tracker sessions, so the room handler matches them as two peers.
var companionFactory = new WebRtcTransportFactory(options, crypto, RandomPeerId());
var watchFactory     = new WebRtcTransportFactory(options, crypto, RandomPeerId());

await using var companion = companionFactory.CreateTransport(companionRecord);
await using var watch     = watchFactory.CreateTransport(watchRecord);

// Receive side: the watch peer records the first message it gets.
var gotProbe = new TaskCompletionSource<TransportMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
watch.MessageReceived += m => gotProbe.TrySetResult(m);

int exitCode;
try
{
    // Stagger: the watch peer joins + announces first and waits in the room; the companion
    // joins ~3s later so it sees the watch's offer on its FIRST announce (the realistic
    // pattern - the two never connect at the same instant, and it avoids depending on the
    // periodic re-announce interval to recover a simultaneous cold-start race).
    Console.WriteLine("[selftest] watch peer joining the room first...");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var watchConnect = watch.ConnectAsync(cts.Token);
    await Task.Delay(3000, cts.Token);
    Console.WriteLine("[selftest] companion peer joining (SDP/ICE + Ed25519 challenge)...");
    var companionConnect = companion.ConnectAsync(cts.Token);
    await Task.WhenAll(watchConnect, companionConnect);
    Console.WriteLine($"[selftest] CONNECTED + mutually verified. companion={companion.IsConnected} watch={watch.IsConnected}");

    var payload = System.Text.Encoding.UTF8.GetBytes("hello-from-companion");
    await companion.SendAsync(new TransportMessage("selftest", payload), cts.Token);
    Console.WriteLine("[selftest] sent probe companion -> watch, awaiting receipt...");

    var received = await gotProbe.Task.WaitAsync(TimeSpan.FromSeconds(15));
    bool ok = received.ChannelId == "selftest" && received.Payload.AsSpan().SequenceEqual(payload);
    Console.WriteLine($"[selftest] received: channel='{received.ChannelId}' payload='{System.Text.Encoding.UTF8.GetString(received.Payload)}' match={ok}");

    if (ok)
    {
        Console.WriteLine("[selftest] SUCCESS - WebRTC datachannel + Ed25519 mutual challenge + framing verified over the hub.");
        exitCode = 0;
    }
    else
    {
        Console.WriteLine("[selftest] FAIL - payload did not round-trip intact.");
        exitCode = 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[selftest] FAIL - {ex.GetType().Name}: {ex.Message}");
    exitCode = 1;
}

return exitCode;

// ---- helpers ----

static async Task<(PairingRecord companion, PairingRecord watch)> MintPairAsync(IPortableCrypto crypto)
{
    using var companionKey = await crypto.GenerateEd25519Key();
    using var watchKey     = await crypto.GenerateEd25519Key();

    byte[] companionPub  = RawPub(await crypto.ExportPublicKeySpki(companionKey));
    byte[] watchPub      = RawPub(await crypto.ExportPublicKeySpki(watchKey));
    byte[] companionPriv = await crypto.ExportPrivateKeyPkcs8(companionKey);
    byte[] watchPriv     = await crypto.ExportPrivateKeyPkcs8(watchKey);

    var roomKey = new byte[20];
    System.Security.Cryptography.RandomNumberGenerator.Fill(roomKey);

    // Each peer's record is from ITS OWN perspective: Our* = self, WatchPubKey = the peer.
    var companion = new PairingRecord(
        WatchPubKey: watchPub, OurPubKey: companionPub, OurPrivKey: companionPriv,
        RoomKey: roomKey, PairedAt: DateTimeOffset.UtcNow, FriendlyName: "self-test companion");
    var watch = new PairingRecord(
        WatchPubKey: companionPub, OurPubKey: watchPub, OurPrivKey: watchPriv,
        RoomKey: roomKey, PairedAt: DateTimeOffset.UtcNow, FriendlyName: "self-test watch");
    return (companion, watch);

    // Ed25519 SPKI = 12-byte ASN.1 prefix + the raw 32-byte key.
    static byte[] RawPub(byte[] spki) => spki[^32..];
}

static byte[] RandomPeerId()
{
    var b = new byte[20];
    System.Security.Cryptography.RandomNumberGenerator.Fill(b);
    var prefix = System.Text.Encoding.ASCII.GetBytes("-SW0001-");
    Buffer.BlockCopy(prefix, 0, b, 0, prefix.Length);
    return b;
}

static string Hex(byte[] b) => Convert.ToHexString(b, 0, Math.Min(4, b.Length)).ToLowerInvariant();
