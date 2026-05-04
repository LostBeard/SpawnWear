using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.RTC.Signaling;
using SpawnWear.Bridge.Pairing;

namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// Builds a <see cref="WebRtcTransport"/> for a given
/// <see cref="PairingRecord"/>. Owns the signaling-client lifecycle
/// (one shared <see cref="TrackerSignalingClient"/> per
/// <c>(announceUrl, peerId)</c> pair via the library's pool); the
/// transport is per-pairing.
///
/// Companion / Bridge.Desktop consumers wire this as a DI singleton
/// alongside <see cref="BridgeWebRtcOptions"/>; pages call
/// <see cref="CreateTransport"/> to get a transport ready for
/// <see cref="BridgeClient.UseTransportAsync"/>.
/// </summary>
public class WebRtcTransportFactory
{
    readonly BridgeWebRtcOptions _options;
    readonly IPortableCrypto _crypto;
    readonly byte[] _peerId;

    /// <param name="options">Hub URL + ICE config. <c>null</c> = library defaults (hub.spawndev.com).</param>
    /// <param name="crypto">Cross-platform Ed25519. Required.</param>
    /// <param name="peerId">20-byte BitTorrent-style peer id for the
    /// signaling-tracker pool. Stable per browser-context / per-app
    /// install; consumers persist it alongside the pairing material.
    /// Auto-generated on first call when null.</param>
    public WebRtcTransportFactory(BridgeWebRtcOptions? options, IPortableCrypto crypto, byte[]? peerId = null)
    {
        _options = options ?? new BridgeWebRtcOptions();
        _crypto = crypto;
        _peerId = peerId ?? GenerateRandomPeerId();
    }

    /// <summary>The 20-byte peer id this factory uses on the
    /// signaling tracker. Persist this alongside pairing material
    /// so the same peer id is used across sessions (some trackers
    /// rate-limit per-peer-id).</summary>
    public byte[] PeerId => _peerId;

    /// <summary>The active signaling client (shared across every
    /// transport this factory makes that targets the same announce URL).</summary>
    public ISignalingClient Signaling => TrackerSignalingClient.GetOrCreate(_options.AnnounceUrl, _peerId);

    /// <summary>Construct a <see cref="WebRtcTransport"/> ready to
    /// connect to <paramref name="pairing"/>'s watch through the
    /// configured hub. Caller is responsible for wiring the resulting
    /// transport into a <see cref="BridgeClient"/> via
    /// <c>UseTransportAsync</c> and calling <c>ConnectAsync</c>.</summary>
    public WebRtcTransport CreateTransport(PairingRecord pairing)
    {
        return new WebRtcTransport(
            signaling: Signaling,
            crypto:    _crypto,
            pairing:   pairing,
            rtcConfig: _options.ToPeerConnectionConfig());
    }

    static byte[] GenerateRandomPeerId()
    {
        var b = new byte[20];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        // BitTorrent-style "client prefix" — first 8 bytes identify the
        // application. Use "-SW0001-" so trackers logs can tell SpawnWear
        // from other clients sharing the same tracker.
        var prefix = System.Text.Encoding.ASCII.GetBytes("-SW0001-");
        Buffer.BlockCopy(prefix, 0, b, 0, prefix.Length);
        return b;
    }
}
