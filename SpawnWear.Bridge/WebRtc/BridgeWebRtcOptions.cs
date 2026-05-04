namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// Consumer-configurable settings for <see cref="WebRtcTransportFactory"/>.
/// Defaults point at <c>hub.spawndev.com</c> (TJ's hub running
/// <c>SpawnDev.RTC.Server</c> with bundled tracker + STUN + TURN);
/// override for self-hosted or staging deployments.
/// </summary>
public sealed class BridgeWebRtcOptions
{
    /// <summary>WebTorrent-protocol tracker / signaling endpoint.
    /// hub.spawndev.com runs on port 44365 (not the default 443) and
    /// serves the announce WebSocket at <c>/announce</c>. The
    /// SpawnDev.RTC tracker speaks the BitTorrent-WebSocket wire
    /// format, so any compatible tracker works.</summary>
    public string AnnounceUrl { get; set; } = "wss://hub.spawndev.com:44365/announce";

    /// <summary>STUN server URLs for NAT traversal. The hub's own
    /// STUN endpoint (3478) is a single party in the trust path;
    /// Google's stun.l is a public fallback if the hub is reachable
    /// but not its STUN port for some reason.</summary>
    public string[] StunUrls { get; set; } =
    {
        "stun:hub.spawndev.com:3478",
        "stun:stun.l.google.com:19302",
    };

    /// <summary>Optional TURN servers for relay fallback (~10-20%
    /// of real-world WebRTC connections need TURN to traverse
    /// symmetric NATs / corporate firewalls / mobile-carrier CGNAT).
    ///
    /// hub.spawndev.com runs the bundled SpawnDev.RTC.Server TURN
    /// listener with <b>tracker-gated ephemeral creds</b> — only
    /// peers currently announced to the signaling WebSocket can
    /// allocate relay sockets, so a stolen TURN credential alone is
    /// useless without an active tracker session under the matching
    /// peer id (RFC 8489 §9.2 + the tracker-room gating layer).
    ///
    /// Production wiring for tracker-gated TURN: the consumer mints
    /// the credential via
    /// <c>EphemeralTurnCredentials.Generate(sharedSecret, userId, lifetime)</c>
    /// just before constructing the peer connection, with
    /// <c>userId = Convert.ToHexString(WebRtcTransportFactory.PeerId)</c>.
    /// That requires the shared secret, which is a hub-deployment
    /// detail Phase 7 needs to settle (open question #6 in
    /// <c>Plans/phase7-webrtc-handoff.md</c>).
    ///
    /// Defaults to empty so the Companion can ship without TURN in
    /// the typical LAN-or-direct-internet case; consumers fill this
    /// per-session when their cred-minting flow is ready.</summary>
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
