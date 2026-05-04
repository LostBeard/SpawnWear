namespace SpawnWear.Bridge.Pairing;

/// <summary>
/// Wire-format encoder/decoder for the BLE pairing handshake. Crypto-free —
/// just the byte packing. Sign/verify happens at the call site via
/// <see cref="SpawnDev.BlazorJS.Cryptography.IPortableCrypto"/> against the
/// <see cref="SignedDomainCompanionToWatch"/> and
/// <see cref="SignedDomainWatchToCompanion"/> outputs of this class.
///
/// Layout per <c>Plans/phase7-webrtc-handoff.md</c>:
///
/// Companion → Watch (write to PairingHandshakeUuid, 116 bytes):
///   <code>
///   offset 0  : companionPubKey  [32 bytes]
///   offset 32 : roomKey          [20 bytes]
///   offset 52 : signature        [64 bytes]   = sign(prev 52 bytes)
///   </code>
///
/// Watch → Companion (notify on PairingHandshakeUuid, 64 bytes):
///   <code>
///   offset 0  : signature        [64 bytes]
///                                = sign(companionPubKey || roomKey || watchPubKey)
///                                = sign(SignedDomainWatchToCompanion(...))
///   </code>
///
/// Both sides verify their counterpart's signature against the matching
/// signed-domain bytes before persisting the pairing.
/// </summary>
public static class PairingHandshakeWire
{
    /// <summary>Build the 52-byte domain that the COMPANION signs and
    /// embeds in the BLE write to the watch. The watch verifies this
    /// signature against the same bytes (which it can reconstruct
    /// independently from the unsigned prefix of the write).</summary>
    public static byte[] SignedDomainCompanionToWatch(byte[] companionPubKey, byte[] roomKey)
    {
        Validate(companionPubKey, PairingHandshake.PubKeyLength,  nameof(companionPubKey));
        Validate(roomKey,         PairingHandshake.RoomKeyLength, nameof(roomKey));

        var dom = new byte[PairingHandshake.PubKeyLength + PairingHandshake.RoomKeyLength];
        Buffer.BlockCopy(companionPubKey, 0, dom, 0, PairingHandshake.PubKeyLength);
        Buffer.BlockCopy(roomKey, 0, dom, PairingHandshake.PubKeyLength, PairingHandshake.RoomKeyLength);
        return dom;
    }

    /// <summary>Build the 84-byte domain that the WATCH signs in its
    /// notify response. The companion verifies this signature against
    /// the same bytes (it knows companionPubKey + roomKey from its own
    /// write, and watchPubKey from the prior PairingPubKeyUuid read).</summary>
    public static byte[] SignedDomainWatchToCompanion(byte[] companionPubKey, byte[] roomKey, byte[] watchPubKey)
    {
        Validate(companionPubKey, PairingHandshake.PubKeyLength,  nameof(companionPubKey));
        Validate(roomKey,         PairingHandshake.RoomKeyLength, nameof(roomKey));
        Validate(watchPubKey,     PairingHandshake.PubKeyLength,  nameof(watchPubKey));

        var dom = new byte[PairingHandshake.PubKeyLength * 2 + PairingHandshake.RoomKeyLength];
        int o = 0;
        Buffer.BlockCopy(companionPubKey, 0, dom, o, PairingHandshake.PubKeyLength); o += PairingHandshake.PubKeyLength;
        Buffer.BlockCopy(roomKey,         0, dom, o, PairingHandshake.RoomKeyLength); o += PairingHandshake.RoomKeyLength;
        Buffer.BlockCopy(watchPubKey,     0, dom, o, PairingHandshake.PubKeyLength);
        return dom;
    }

    /// <summary>Pack the 116-byte BLE write the companion sends to the
    /// watch's <c>PairingHandshakeUuid</c> characteristic. Caller has
    /// already produced <paramref name="signature"/> by signing
    /// <see cref="SignedDomainCompanionToWatch"/>.</summary>
    public static byte[] PackCompanionWrite(byte[] companionPubKey, byte[] roomKey, byte[] signature)
    {
        Validate(companionPubKey, PairingHandshake.PubKeyLength,    nameof(companionPubKey));
        Validate(roomKey,         PairingHandshake.RoomKeyLength,   nameof(roomKey));
        Validate(signature,       PairingHandshake.SignatureLength, nameof(signature));

        var buf = new byte[PairingHandshake.CompanionToWatchLength];
        int o = 0;
        Buffer.BlockCopy(companionPubKey, 0, buf, o, PairingHandshake.PubKeyLength);    o += PairingHandshake.PubKeyLength;
        Buffer.BlockCopy(roomKey,         0, buf, o, PairingHandshake.RoomKeyLength);   o += PairingHandshake.RoomKeyLength;
        Buffer.BlockCopy(signature,       0, buf, o, PairingHandshake.SignatureLength);
        return buf;
    }

    /// <summary>Inverse of <see cref="PackCompanionWrite"/>. The watch
    /// firmware calls this when handling the BLE write to extract the
    /// three fields for verification + persistence.</summary>
    public static (byte[] companionPubKey, byte[] roomKey, byte[] signature) ParseCompanionWrite(byte[] payload)
    {
        Validate(payload, PairingHandshake.CompanionToWatchLength, nameof(payload));

        var pub = new byte[PairingHandshake.PubKeyLength];
        var rk  = new byte[PairingHandshake.RoomKeyLength];
        var sig = new byte[PairingHandshake.SignatureLength];

        int o = 0;
        Buffer.BlockCopy(payload, o, pub, 0, PairingHandshake.PubKeyLength);    o += PairingHandshake.PubKeyLength;
        Buffer.BlockCopy(payload, o, rk,  0, PairingHandshake.RoomKeyLength);   o += PairingHandshake.RoomKeyLength;
        Buffer.BlockCopy(payload, o, sig, 0, PairingHandshake.SignatureLength);

        return (pub, rk, sig);
    }

    static void Validate(byte[] arg, int expected, string name)
    {
        if (arg is null) throw new ArgumentNullException(name);
        if (arg.Length != expected)
            throw new ArgumentException($"{name} must be exactly {expected} bytes, got {arg.Length}.", name);
    }
}
