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

// Phase 7b: be patient with the watch's ICE. The watch (constrained embedded peer) only starts
// answering STUN connectivity checks ~20s in - it can't validate them until it has our ufrag/pwd
// from the answer, which is delayed by non-trickle ICE gathering + hub relay. SipSorcery's default
// 16s FAILED timeout fires first -> a transient ICE failure that can corrupt the SCTP handshake.
// Set once here so EVERY desktop-peer mode (answerroom included), not just dcdiag, gets it.
SIPSorcery.Net.RtpIceChannel.FAILED_TIMEOUT_PERIOD = 30;
SIPSorcery.Net.RtpIceChannel.DISCONNECTED_TIMEOUT_PERIOD = 20;

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

// Generate a FIXED test pairing's key material (base64) to embed in the shared
// WebRtcSelfTestPairing class. Run once: dotnet run --project SpawnWear.Bridge.Desktop -- genpair
if (args.Length > 0 && args[0] == "genpair")
{
    var c = new DotNetCrypto();
    using var compKey = await c.GenerateEd25519Key();
    using var wKey = await c.GenerateEd25519Key();
    byte[] compPub = (await c.ExportPublicKeySpki(compKey))[^32..];
    byte[] wPub = (await c.ExportPublicKeySpki(wKey))[^32..];
    byte[] compPriv = await c.ExportPrivateKeyPkcs8(compKey);
    byte[] wPriv = await c.ExportPrivateKeyPkcs8(wKey);
    var rk = new byte[20];
    System.Security.Cryptography.RandomNumberGenerator.Fill(rk);
    Console.WriteLine("CompanionPubB64  = \"" + Convert.ToBase64String(compPub) + "\";");
    Console.WriteLine("CompanionPrivB64 = \"" + Convert.ToBase64String(compPriv) + "\";");
    Console.WriteLine("WatchPubB64      = \"" + Convert.ToBase64String(wPub) + "\";");
    Console.WriteLine("WatchPrivB64     = \"" + Convert.ToBase64String(wPriv) + "\";");
    Console.WriteLine("RoomKeyB64       = \"" + Convert.ToBase64String(rk) + "\";");
    return 0;
}

// Phase 7b datachannel debug: feed SipSorcery the WATCH's real setup:passive offer and inspect
// the answer's a=setup. RFC 8842: answering setup:passive MUST yield setup:active (DTLS client).
// If the answer is passive/actpass -> two DTLS servers -> deadlock = the datachannel root cause.
// No watch / no hub needed - pure local SipSorcery CreateAnswer. Run: ... -- setuptest
if (args.Length > 0 && args[0] == "setuptest")
{
    const string watchOffer =
        "v=0\r\no=- 1495799811084970 1495799811084970 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n" +
        "a=msid-semantic: iot\r\na=group:BUNDLE datachannel\r\n" +
        "m=application 50712 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\n" +
        "a=mid:datachannel\r\na=sctp-port:5000\r\na=max-message-size:262144\r\n" +
        "a=fingerprint:sha-256 E2:D5:E4:2B:AB:7D:00:52:FE:EF:E8:66:18:0F:E5:26:BA:2F:C3:9F:AB:B9:DA:A1:BD:50:71:97:DB:C7:B6:19\r\n" +
        "a=setup:passive\r\na=ice-ufrag:IJ2Y\r\na=ice-pwd:IJ2YZjhuHmpHKsrVNcbtv6ZI\r\n" +
        "a=candidate:0 1 UDP 2129201151 192.168.1.170 59655 typ host\r\n";
    SIPSorcery.LogFactory.Set(LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug)));
    var stpc = SpawnDev.RTC.RTCPeerConnectionFactory.Create(new BridgeWebRtcOptions().ToPeerConnectionConfig());
    await stpc.SetRemoteDescription(new SpawnDev.RTC.RTCSessionDescriptionInit { Type = "offer", Sdp = watchOffer });
    var ans = await stpc.CreateAnswer();
    await stpc.SetLocalDescription(ans);
    Console.WriteLine("=== OFFER setup line: a=setup:passive (watch = DTLS server) ===");
    Console.WriteLine("=== ANSWER SDP ===");
    Console.WriteLine(ans.Sdp);
    var setupLine = (ans.Sdp ?? "").Split('\n').FirstOrDefault(l => l.Contains("a=setup"));
    Console.WriteLine($"\n>>> ANSWER a=setup = '{setupLine?.Trim()}'  (EXPECT setup:active = DTLS client; setup:passive = BUG = two servers)");
    return 0;
}

// Phase 7b datachannel lifecycle diagnostic: join the room with our OWN RtcPeerConnectionRoomHandler
// and log the ANSWER peer-connection's full lifecycle (ICE state, connection state, OnDataChannel).
// Tells us exactly how far the watch<->SipSorcery answer PC gets: ICE-connected? DTLS connected?
// does OnDataChannel ever fire (= watch's DCEP reached SipSorcery's SCTP)? Run: ... -- dcdiag [room]
if (args.Length > 0 && args[0] == "dcdiag")
{
    var dcRoomStr = args.Length > 1 ? args[1] : "SWclean0623pmRoom01x";
    // (ICE FAILED_TIMEOUT_PERIOD bumped to 30 at the top of Main - covers every desktop mode.)
    SIPSorcery.LogFactory.Set(LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }).SetMinimumLevel(LogLevel.Debug)));
    var dcOpts = new BridgeWebRtcOptions();
    var dcSignaling = SpawnDev.RTC.Signaling.TrackerSignalingClient.GetOrCreate(dcOpts.AnnounceUrl, RandomPeerId());
    var dcHandler = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(dcOpts.ToPeerConnectionConfig());
    dcHandler.OnPeerConnectionCreated = (pc, id) =>
    {
        Console.WriteLine($"[dcdiag] >>> answer PC created for peer={id} (initial conn={pc.ConnectionState} ice={pc.IceConnectionState})");
        pc.OnConnectionStateChange   += s => Console.WriteLine($"[dcdiag]   ConnectionState -> {s}  (peer={id})");
        pc.OnIceConnectionStateChange += s => Console.WriteLine($"[dcdiag]   IceConnectionState -> {s}  (peer={id})");
        pc.OnDataChannel             += dc =>
        {
            Console.WriteLine($"[dcdiag]   *** OnDataChannel FIRED label='{dc.Label}' state={dc.ReadyState} (peer={id}) ***");
            dc.OnBinaryMessage += data => Console.WriteLine($"[dcdiag]   *** RECV BINARY {data.Length}B: '{System.Text.Encoding.UTF8.GetString(data)}' ***");
            dc.OnStringMessage += s => Console.WriteLine($"[dcdiag]   *** RECV STRING: '{s}' ***");
        };
        return Task.CompletedTask;
    };
    dcHandler.OnDataChannel += (dc, id) => Console.WriteLine($"[dcdiag] *** handler.OnDataChannel label='{dc.Label}' peer={id} ***");
    dcHandler.OnPeerConnection += (pc, id) => Console.WriteLine($"[dcdiag] OnPeerConnection (answer returned) peer={id} conn={pc.ConnectionState} ice={pc.IceConnectionState}");
    var dcRoom = SpawnDev.RTC.Signaling.RoomKey.FromBytes(System.Text.Encoding.ASCII.GetBytes(dcRoomStr));
    dcSignaling.Subscribe(dcRoom, dcHandler);
    await dcSignaling.AnnounceAsync(dcRoom, new SpawnDev.RTC.Signaling.AnnounceOptions { Event = "started", Left = 1 });
    Console.WriteLine($"[dcdiag] joined room '{dcRoomStr}' on {dcOpts.AnnounceUrl} - waiting for the watch's offer (Ctrl+C to stop)");
    await Task.Delay(System.Threading.Timeout.Infinite);
    return 0;
}

// Long-lived "watch" peer for the browser <-> .NET demo (Stage 1b). Joins the fixed
// self-test room with the watch-role record and echoes anything the browser sends, so a
// person can SEE the browser Companion talk to a .NET WebRTC peer over the hub.
// Run: dotnet run --project SpawnWear.Bridge.Desktop -- watch
if (args.Length > 0 && args[0] == "watch")
{
    var wcrypto = new DotNetCrypto();
    var wopts = new BridgeWebRtcOptions();
    var wfactory = new WebRtcTransportFactory(wopts, wcrypto, RandomPeerId());
    await using var w = wfactory.CreateTransport(WebRtcSelfTestPairing.WatchRecord());
    w.MessageReceived += m =>
    {
        var text = System.Text.Encoding.UTF8.GetString(m.Payload);
        Console.WriteLine($"[watch] recv channel='{m.ChannelId}' payload='{text}'");
        try { _ = w.SendAsync(new TransportMessage(m.ChannelId, System.Text.Encoding.UTF8.GetBytes("echo: " + text))); }
        catch (Exception ex) { Console.WriteLine($"[watch] echo failed: {ex.Message}"); }
    };
    Console.WriteLine($"[watch] hub={wopts.AnnounceUrl}");
    Console.WriteLine($"[watch] joining the self-test room, waiting for the Companion (browser) to connect... (Ctrl+C to stop)");
    await w.ConnectAsync(CancellationToken.None);
    Console.WriteLine("[watch] CONNECTED + mutually verified. Echoing messages. Ctrl+C to stop.");
    await Task.Delay(System.Threading.Timeout.Infinite);
    return 0;
}

// Companion-role peer using the SAME fixed self-test pairing - to verify the fixed records
// + the watch-mode echo end-to-end on .NET before the browser button is exercised.
// Run (against a running `-- watch`): dotnet run --project SpawnWear.Bridge.Desktop -- companion
if (args.Length > 0 && args[0] == "companion")
{
    var ccrypto = new DotNetCrypto();
    var copts = new BridgeWebRtcOptions();
    var cfactory = new WebRtcTransportFactory(copts, ccrypto, RandomPeerId());
    await using var cpeer = cfactory.CreateTransport(WebRtcSelfTestPairing.CompanionRecord());
    var echoTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    cpeer.MessageReceived += m => echoTcs.TrySetResult(System.Text.Encoding.UTF8.GetString(m.Payload));
    try
    {
        using var ccts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Console.WriteLine("[companion] connecting to the watch peer over the hub...");
        await cpeer.ConnectAsync(ccts.Token);
        Console.WriteLine("[companion] connected + verified; sending ping...");
        await cpeer.SendAsync(new TransportMessage("selftest", System.Text.Encoding.UTF8.GetBytes("ping from companion")), ccts.Token);
        var reply = await echoTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"[companion] SUCCESS - watch replied: '{reply}'");
        return 0;
    }
    catch (Exception ex) { Console.WriteLine($"[companion] FAIL - {ex.GetType().Name}: {ex.Message}"); return 1; }
}

// Answer offers in a plain ASCII-named room - the peer for the watch firmware's milestone-3
// WebRtcConnectTest (which is the OFFERER and does NOT run the Ed25519 challenge yet). We only
// want to prove the libpeer(watch) <-> SipSorcery(desktop) ICE/DTLS/datachannel interop: the
// transport answers the watch's offer and the datachannel opens (the watch then reads
// PeerConnection state == Connected). The challenge will fail (the watch doesn't participate),
// which is expected here - the connection forms first.
// Run: dotnet run --project SpawnWear.Bridge.Desktop -- answerroom [roomAscii]
if (args.Length > 0 && args[0] == "answerroom")
{
    // Phase 7b: surface SipSorcery's internal ICE/DTLS/SCTP/DCEP logs so we can see whether the
    // watch's libpeer(usrsctp) datachannel actually reaches SipSorcery's SCTP association + OnDataChannel.
    SIPSorcery.LogFactory.Set(LoggerFactory.Create(b =>
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
         .SetMinimumLevel(LogLevel.Debug)));
    var roomStr = args.Length > 1 ? args[1] : "SWtestRoom0123456789";
    var room = System.Text.Encoding.ASCII.GetBytes(roomStr);
    var arcrypto = new DotNetCrypto();
    var aropts = new BridgeWebRtcOptions();
    var arfactory = new WebRtcTransportFactory(aropts, arcrypto, RandomPeerId());
    var arRecord = WebRtcSelfTestPairing.CompanionRecord() with { RoomKey = room };
    await using var arpeer = arfactory.CreateTransport(arRecord);
    arpeer.MessageReceived += m =>
        Console.WriteLine($"[answerroom] recv channel='{m.ChannelId}' payload='{System.Text.Encoding.UTF8.GetString(m.Payload)}'");
    Console.WriteLine($"[answerroom] hub={aropts.AnnounceUrl} room='{roomStr}' - answering the watch's offer (Ctrl+C to stop)");
    try
    {
        await arpeer.ConnectAsync(CancellationToken.None);
        Console.WriteLine("[answerroom] CONNECTED + verified (datachannel up).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[answerroom] ConnectAsync ended: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("[answerroom] (datachannel may still have opened - watch only needs ICE/DTLS up. Holding the process.)");
    }
    await Task.Delay(System.Threading.Timeout.Infinite);
    return 0;
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
