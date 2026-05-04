namespace SpawnWear.Bridge.Pairing;

/// <summary>
/// Wire-format helpers for the BLE pairing handshake described in
/// <c>Plans/phase7-webrtc-handoff.md</c>. Both sides exchange Ed25519
/// public keys + agree on a room id + verify signatures across the
/// PairingHandshakeUuid characteristic.
///
/// Phase 7 stub. Layout constants here; signature verification + key
/// generation arrive in the Phase 7 implementation commit so consumers
/// can reference the shapes today without crypto on the path.
/// </summary>
public static class PairingHandshake
{
    /// <summary>Ed25519 public key length (RFC 8032).</summary>
    public const int PubKeyLength = 32;

    /// <summary>Ed25519 private key seed length.</summary>
    public const int PrivKeyLength = 32;

    /// <summary>Ed25519 signature length.</summary>
    public const int SignatureLength = 64;

    /// <summary>Length of the room key agreed on during pairing.
    /// Matches SpawnDev.RTC's <c>RoomKey</c> (20 bytes; WebTorrent
    /// info_hash compatible so the same key shape works directly with
    /// the SpawnDev.RTC tracker / signaling layer).</summary>
    public const int RoomKeyLength = 20;

    /// <summary>Companion-to-watch handshake payload size:
    /// <see cref="PubKeyLength"/> + <see cref="RoomKeyLength"/>
    /// + <see cref="SignatureLength"/> = 116 bytes.</summary>
    public const int CompanionToWatchLength = PubKeyLength + RoomKeyLength + SignatureLength;

    /// <summary>Watch-to-companion handshake response size:
    /// <see cref="SignatureLength"/> only - the watch signs
    /// <c>(companionPubKey || roomId || watchPubKey)</c> and returns
    /// just the signature; the companion already has all three inputs
    /// to verify against.</summary>
    public const int WatchToCompanionLength = SignatureLength;
}

/// <summary>
/// Domain of the message a watch signs when it returns the handshake
/// acknowledgement. Concatenation of three inputs the companion can
/// reconstruct independently to verify.
/// </summary>
public readonly record struct PairingResponseDomain(
    byte[] CompanionPubKey,    // PairingHandshake.PubKeyLength
    byte[] RoomKey,            // PairingHandshake.RoomKeyLength
    byte[] WatchPubKey         // PairingHandshake.PubKeyLength
);
