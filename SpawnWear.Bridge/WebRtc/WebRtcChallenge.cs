namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// Wire-format helpers for the post-WebRTC-handshake mutual verification
/// challenge described in <c>Plans/phase7-webrtc-handoff.md</c>. After
/// the WebRTC data channel opens, BOTH peers send a fresh 32-byte
/// nonce and expect a signed response from the other side. Each side
/// verifies with the OTHER's stored Ed25519 public key (cached in the
/// <see cref="Pairing.PairingRecord"/> from BLE pairing).
///
/// If verification fails on either side, both peers tear the connection
/// down before any application data flows.
///
/// Crypto-free — just the byte packing. Sign/verify happens at the call
/// site via <c>IPortableCrypto</c>.
///
/// Layout:
///
/// Either peer → other peer (challenge request):
///   <code>
///   offset 0  : nonce            [32 bytes random]
///   total     : 32 bytes
///   </code>
///
/// Other peer → first peer (challenge response):
///   <code>
///   offset 0  : nonce            [32 bytes; echoed back]
///   offset 32 : signature        [64 bytes; sign(nonce) with own privkey]
///   total     : 96 bytes
///   </code>
///
/// The receiver verifies the 64-byte signature against the 32-byte
/// nonce using the sender's Ed25519 pubkey from the
/// <see cref="Pairing.PairingRecord"/>. The echoed nonce lets the
/// sender confirm the response corresponds to the challenge they
/// just issued (no replay across challenges).
/// </summary>
public static class WebRtcChallenge
{
    /// <summary>Length of the random nonce in a challenge / response.</summary>
    public const int NonceLength = 32;

    /// <summary>Length of an Ed25519 signature.</summary>
    public const int SignatureLength = 64;

    /// <summary>Length of a challenge request: just a nonce.</summary>
    public const int ChallengeRequestLength = NonceLength;

    /// <summary>Length of a challenge response: nonce + signature.</summary>
    public const int ChallengeResponseLength = NonceLength + SignatureLength;

    /// <summary>Generate a fresh cryptographically random 32-byte nonce.
    /// Use this for the challenge request before sending; remember the
    /// bytes so you can verify the matching response.</summary>
    public static byte[] GenerateNonce()
    {
        var n = new byte[NonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(n);
        return n;
    }

    /// <summary>Pack a challenge request — just returns the nonce wrapped
    /// for shape consistency. Caller already has the bytes.</summary>
    public static byte[] PackRequest(byte[] nonce)
    {
        Validate(nonce, NonceLength, nameof(nonce));
        var copy = new byte[NonceLength];
        Buffer.BlockCopy(nonce, 0, copy, 0, NonceLength);
        return copy;
    }

    /// <summary>Pack a challenge response: <c>[nonce:32][signature:64]</c>.</summary>
    public static byte[] PackResponse(byte[] nonce, byte[] signature)
    {
        Validate(nonce,     NonceLength,     nameof(nonce));
        Validate(signature, SignatureLength, nameof(signature));
        var buf = new byte[ChallengeResponseLength];
        Buffer.BlockCopy(nonce, 0, buf, 0, NonceLength);
        Buffer.BlockCopy(signature, 0, buf, NonceLength, SignatureLength);
        return buf;
    }

    /// <summary>Inverse of <see cref="PackResponse"/>.</summary>
    public static (byte[] nonce, byte[] signature) ParseResponse(byte[] payload)
    {
        Validate(payload, ChallengeResponseLength, nameof(payload));
        var n = new byte[NonceLength];
        var s = new byte[SignatureLength];
        Buffer.BlockCopy(payload, 0,           n, 0, NonceLength);
        Buffer.BlockCopy(payload, NonceLength, s, 0, SignatureLength);
        return (n, s);
    }

    /// <summary>The bytes the responder signs. Currently equal to the
    /// 32-byte nonce. Leaving this as a method rather than passing
    /// <c>nonce</c> straight to <c>IPortableCrypto.Sign</c> at call
    /// sites makes it cheap to extend the signed domain later (e.g.
    /// to bind it to <c>roomKey</c> or <c>peerPubKey</c>) without
    /// changing every call site.</summary>
    public static byte[] SignedDomain(byte[] nonce)
    {
        Validate(nonce, NonceLength, nameof(nonce));
        var copy = new byte[NonceLength];
        Buffer.BlockCopy(nonce, 0, copy, 0, NonceLength);
        return copy;
    }

    static void Validate(byte[] arg, int expected, string name)
    {
        if (arg is null) throw new ArgumentNullException(name);
        if (arg.Length != expected)
            throw new ArgumentException($"{name} must be exactly {expected} bytes, got {arg.Length}.", name);
    }
}
