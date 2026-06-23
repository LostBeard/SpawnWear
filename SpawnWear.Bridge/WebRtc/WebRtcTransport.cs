using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.RTC;
using SpawnDev.RTC.Signaling;
using SpawnWear.Bridge.Pairing;

namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// <see cref="ITransport"/> implementation over a peer-to-peer WebRTC
/// data channel, signaled through <c>hub.spawndev.com</c> (or any
/// <see cref="ISignalingClient"/> the consumer wires up).
///
/// Used after the BLE pairing handshake. The two peers cached each
/// other's Ed25519 public keys + a shared <c>RoomKey</c> in their
/// respective <see cref="PairingRecord"/>s; from then on this
/// transport is enough to reach the watch from anywhere on the
/// internet — no shared LAN required.
///
/// Connection sequence:
/// <list type="number">
///   <item>Subscribe to the room key on the signaling client + announce.</item>
///   <item>SpawnDev.RTC's <see cref="RtcPeerConnectionRoomHandler"/>
///         runs SDP offer/answer + ICE; surfaces an
///         <see cref="IRTCDataChannel"/> via <c>OnDataChannel</c>.</item>
///   <item>This transport sends a 32-byte challenge nonce; receives + signs
///         the peer's nonce; verifies the peer's signed response.</item>
///   <item>After mutual verification both sides flip the channel to
///         "ready"; subsequent traffic is framed
///         <see cref="TransportMessage"/>s via <see cref="WebRtcDataFraming"/>.</item>
/// </list>
///
/// If verification fails on either side the data channel is closed and
/// <see cref="ConnectAsync"/> completes with an exception.
/// </summary>
public class WebRtcTransport : ITransport, IAsyncDisposable
{
    readonly ISignalingClient _signaling;
    readonly IPortableCrypto _crypto;
    readonly PairingRecord _pairing;
    readonly RTCPeerConnectionConfig? _rtcConfig;
    readonly RtcPeerConnectionRoomHandler _handler;

    IRTCDataChannel? _channel;
    string? _remotePeerId;

    PortableEd25519Key? _ourSignKey;
    PortableEd25519Key? _watchVerifyKey;

    byte[]? _ourChallengeNonce;
    bool _ourChallengeVerified;
    bool _peerChallengeAnswered;

    TaskCompletionSource<bool>? _verifyTcs;

    /// <param name="signaling">Already-constructed signaling client (e.g. <see cref="TrackerSignalingClient"/> bound to <c>wss://hub.spawndev.com</c>).</param>
    /// <param name="crypto">Cross-platform Ed25519. <c>SpawnDev.BlazorJS.Cryptography</c> picks the right backend per runtime.</param>
    /// <param name="pairing">Result of a prior BLE pairing. Contains both Ed25519 keypairs (raw + PKCS8) + the agreed <c>RoomKey</c>.</param>
    /// <param name="rtcConfig">Optional ICE / bundle / transport-policy config. <c>null</c> = platform defaults.</param>
    public WebRtcTransport(
        ISignalingClient signaling,
        IPortableCrypto crypto,
        PairingRecord pairing,
        RTCPeerConnectionConfig? rtcConfig = null)
    {
        _signaling = signaling;
        _crypto = crypto;
        _pairing = pairing;
        _rtcConfig = rtcConfig;
        _handler = new RtcPeerConnectionRoomHandler(rtcConfig);
        _handler.OnDataChannel    += OnDataChannel;
        _handler.OnPeerDisconnected += OnPeerDisconnected;
    }

    public bool IsConnected { get; private set; }

    /// <summary>Friendly label from the pairing record (the user's
    /// label for this watch, e.g. "Aubs's watch") if any.</summary>
    public string? PeerName => _pairing.FriendlyName;

    public event Action<bool>? ConnectionChanged;
    public event Action<TransportMessage>? MessageReceived;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        // Import keys once; reuse on every challenge / response.
        _ourSignKey   = await _crypto.ImportEd25519Key(WrapRawPubKey(_pairing.OurPubKey), _pairing.OurPrivKey);
        _watchVerifyKey = await _crypto.ImportEd25519Key(WrapRawPubKey(_pairing.WatchPubKey));

        _verifyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var roomKey = RoomKey.FromBytes(_pairing.RoomKey);
        _signaling.Subscribe(roomKey, _handler);
        await _signaling.AnnounceAsync(roomKey, new AnnounceOptions { Event = "started", Left = 1 }, ct);

        using (ct.Register(() => _verifyTcs.TrySetCanceled(ct)))
        {
            try { await _verifyTcs.Task; }
            catch
            {
                await DisconnectAsync();
                throw;
            }
        }

        IsConnected = true;
        ConnectionChanged?.Invoke(true);
    }

    public Task SendAsync(TransportMessage message, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected");
        if (_channel is null) throw new InvalidOperationException("Data channel went away.");
        var frame = WebRtcDataFraming.Pack(message);
        _channel.Send(frame);
        return Task.CompletedTask;
    }

    /// <summary>WebRTC has no concept of "readable state" — state arrives
    /// via push from the watch over the data channel. No-op.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Pairing happens over BLE, not WebRTC.</summary>
    public Task<byte[]> ReadWatchPublicKeyAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("WebRTC transport doesn't carry pairing - pair over BLE first.");

    /// <summary>Pairing happens over BLE, not WebRTC.</summary>
    public Task<byte[]> ExchangePairingHandshakeAsync(byte[] companionWritePayload, CancellationToken ct = default) =>
        throw new NotSupportedException("WebRTC transport doesn't carry pairing - pair over BLE first.");

    public async Task DisconnectAsync()
    {
        if (_channel is not null)
        {
            // Unhook our handlers BEFORE closing, so our own Close() does not re-enter
            // OnDataChannelClosed (which would fire-and-forget another DisconnectAsync and
            // surface as an unhandled exception during disposal).
            _channel.OnBinaryMessage -= OnBinaryMessage;
            _channel.OnClose         -= OnDataChannelClosed;
            try { _channel.Close(); } catch { /* ignore */ }
            try { _channel.Dispose(); } catch { /* ignore */ }
            _channel = null;
        }

        // Close the peer connection(s), not just the data channel. Closing only the channel leaves the
        // RTCPeerConnection (and its ICE agent) alive, so the browser keeps sending ICE consent
        // keepalives and the remote peer never trips its keepalive timeout - it stays "connected"
        // forever (the watch's Companion-link icon stayed green after Disconnect). Unhook our handlers
        // first to avoid re-entering DisconnectAsync via OnPeerDisconnected, then dispose the handler,
        // which Close()s + Dispose()s every pooled/active IRTCPeerConnection. This transport is one-shot
        // (a fresh one is created per Connect), so disposing the handler here is correct.
        _handler.OnDataChannel      -= OnDataChannel;
        _handler.OnPeerDisconnected -= OnPeerDisconnected;
        try { _handler.Dispose(); } catch { /* ignore */ }

        var roomKey = RoomKey.FromBytes(_pairing.RoomKey);
        try { _signaling.Unsubscribe(roomKey); } catch { /* ignore */ }
        try { await _signaling.AnnounceAsync(roomKey, new AnnounceOptions { Event = "stopped" }); } catch { /* ignore */ }

        _ourSignKey?.Dispose();   _ourSignKey   = null;
        _watchVerifyKey?.Dispose(); _watchVerifyKey = null;

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _handler.OnDataChannel    -= OnDataChannel;
        _handler.OnPeerDisconnected -= OnPeerDisconnected;
        _handler.Dispose();
        GC.SuppressFinalize(this);
    }

    void OnDataChannel(IRTCDataChannel channel, string remotePeerId)
    {
        // First channel wins. SpawnDev.RTC's room handler may surface
        // multiple channels per peer; we ignore subsequent ones.
        if (_channel is not null) return;
        _channel = channel;
        _remotePeerId = remotePeerId;
        channel.OnBinaryMessage += OnBinaryMessage;
        channel.OnClose         += OnDataChannelClosed;

        // Issue our challenge as soon as the channel is open.
        if (channel.ReadyState == "open") IssueChallenge();
        else channel.OnOpen += IssueChallenge;
    }

    void IssueChallenge()
    {
        if (_channel is null || _ourSignKey is null) return;
        if (_ourChallengeNonce is not null) return; // already issued
        _ourChallengeNonce = WebRtcChallenge.GenerateNonce();
        _channel.Send(WebRtcChallenge.PackRequest(_ourChallengeNonce));
    }

    async void OnBinaryMessage(byte[] frame)
    {
        if (frame is null || _channel is null) return;
        try
        {
            if (!IsConnected)
            {
                await HandleVerificationFrame(frame);
                return;
            }
            var msg = WebRtcDataFraming.Parse(frame);
            MessageReceived?.Invoke(msg);
        }
        catch (Exception ex)
        {
            // Bad frame from peer is a teardown event. We don't trust
            // streams that violate the wire contract.
            _verifyTcs?.TrySetException(new InvalidOperationException("Bad frame from peer: " + ex.Message, ex));
            await DisconnectAsync();
        }
    }

    async Task HandleVerificationFrame(byte[] frame)
    {
        if (_channel is null || _ourSignKey is null || _watchVerifyKey is null) return;

        if (frame.Length == WebRtcChallenge.ChallengeRequestLength && !_peerChallengeAnswered)
        {
            // Peer sent us their challenge nonce. Sign + respond.
            var theirNonce = frame;
            var sig = await _crypto.Sign(_ourSignKey, WebRtcChallenge.SignedDomain(theirNonce));
            _channel.Send(WebRtcChallenge.PackResponse(theirNonce, sig));
            _peerChallengeAnswered = true;
            CheckBothComplete();
        }
        else if (frame.Length == WebRtcChallenge.ChallengeResponseLength && !_ourChallengeVerified)
        {
            // Peer responded to our earlier challenge. Verify their
            // signature against the nonce we issued.
            var (echoedNonce, sig) = WebRtcChallenge.ParseResponse(frame);
            if (_ourChallengeNonce is null || !echoedNonce.AsSpan().SequenceEqual(_ourChallengeNonce))
            {
                _verifyTcs?.TrySetException(new InvalidOperationException("Peer echoed wrong nonce in challenge response."));
                return;
            }
            var ok = await _crypto.Verify(_watchVerifyKey, WebRtcChallenge.SignedDomain(echoedNonce), sig);
            if (!ok)
            {
                _verifyTcs?.TrySetException(new InvalidOperationException("Peer's challenge signature did not verify."));
                return;
            }
            _ourChallengeVerified = true;
            CheckBothComplete();
        }
    }

    void CheckBothComplete()
    {
        if (_ourChallengeVerified && _peerChallengeAnswered)
            _verifyTcs?.TrySetResult(true);
    }

    void OnDataChannelClosed()
    {
        if (IsConnected)
        {
            _ = DisconnectAsync();
        }
        else
        {
            _verifyTcs?.TrySetException(new InvalidOperationException("Data channel closed before mutual verification completed."));
        }
    }

    void OnPeerDisconnected(string remotePeerId)
    {
        if (_remotePeerId != remotePeerId) return;
        _ = DisconnectAsync();
    }

    // Ed25519 SPKI envelope = 12-byte ASN.1 prefix + 32-byte raw key.
    static readonly byte[] _ed25519SpkiPrefix =
    {
        0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00,
    };
    static byte[] WrapRawPubKey(byte[] raw)
    {
        var spki = new byte[_ed25519SpkiPrefix.Length + raw.Length];
        Buffer.BlockCopy(_ed25519SpkiPrefix, 0, spki, 0, _ed25519SpkiPrefix.Length);
        Buffer.BlockCopy(raw, 0, spki, _ed25519SpkiPrefix.Length, raw.Length);
        return spki;
    }
}
