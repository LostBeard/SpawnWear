namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// Consumer-configurable settings for <see cref="WebRtcTransportFactory"/>.
/// Defaults point at <c>hub.spawndev.com</c> (TJ's hub running
/// <c>SpawnDev.RTC.Server</c>); override for self-hosted or staging
/// deployments.
/// </summary>
public sealed class BridgeWebRtcOptions
{
    /// <summary>WebTorrent-protocol tracker / signaling endpoint. The
    /// SpawnDev.RTC tracker speaks the BitTorrent-WebSocket wire
    /// format, so any compatible tracker works.</summary>
    public string AnnounceUrl { get; set; } = "wss://hub.spawndev.com/announce";

    /// <summary>STUN server URLs for NAT traversal. Sensible default
    /// is the hub's own STUN endpoint (single party in the trust
    /// path) plus Google's public fallback.</summary>
    public string[] StunUrls { get; set; } =
    {
        "stun:hub.spawndev.com:3478",
        "stun:stun.l.google.com:19302",
    };

    /// <summary>Optional TURN servers for relay fallback (when peers
    /// can't reach each other directly). The SpawnDev.RTC.Server
    /// model is "ephemeral creds gated by who's announced in the
    /// room" — the consumer fills these per-session after announcing
    /// the room. Empty by default; the hub may grant them on demand.</summary>
    public SpawnDev.RTC.RTCIceServerConfig[] TurnServers { get; set; } =
        System.Array.Empty<SpawnDev.RTC.RTCIceServerConfig>();

    /// <summary>Bundle policy for the underlying RTCPeerConnection.
    /// "max-bundle" is the WebRTC-recommended default for data-only
    /// connections.</summary>
    public string BundlePolicy { get; set; } = "max-bundle";

    /// <summary>"all" (try direct then relay) or "relay" (force TURN
    /// relay - useful for testing or deployments where direct paths
    /// are forbidden).</summary>
    public string IceTransportPolicy { get; set; } = "all";

    /// <summary>Build the equivalent <see cref="SpawnDev.RTC.RTCPeerConnectionConfig"/>
    /// from these options.</summary>
    public SpawnDev.RTC.RTCPeerConnectionConfig ToPeerConnectionConfig()
    {
        var ice = new System.Collections.Generic.List<SpawnDev.RTC.RTCIceServerConfig>();
        foreach (var url in StunUrls)
            ice.Add(new SpawnDev.RTC.RTCIceServerConfig(url));
        ice.AddRange(TurnServers);
        return new SpawnDev.RTC.RTCPeerConnectionConfig
        {
            IceServers          = ice.ToArray(),
            BundlePolicy        = BundlePolicy,
            IceTransportPolicy  = IceTransportPolicy,
        };
    }
}
