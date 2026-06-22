using SpawnDev.BlazorJS.Cryptography;

namespace SpawnWear.Bridge.Pairing;

/// <summary>
/// Browser-side orchestration of the BLE pairing handshake. Combines
/// <see cref="ITransport"/>'s pairing helpers with
/// <see cref="IPortableCrypto"/>'s Ed25519 sign/verify and an
/// <see cref="IPairingStore"/> for persistence.
///
/// One-call entry point: <see cref="PairAsync"/> takes a transport
/// already past the BLE picker (the user has selected the watch) and
/// runs the handshake end-to-end, returning the saved
/// <see cref="PairingRecord"/>.
///
/// Crypto: Ed25519 throughout. Companion's keypair is generated on
/// first call (per browser-context); subsequent calls re-use it for
/// any new watch the user pairs to. Watch's pubkey is fetched fresh
/// per pair so a re-paired watch (whose flash got wiped, or a new
/// device with the same friendly name) works without manual cleanup.
/// </summary>
public class PairingFlow
{
    readonly IPortableCrypto _crypto;
    readonly IPairingStore _store;

    public PairingFlow(IPortableCrypto crypto, IPairingStore store)
    {
        _crypto = crypto;
        _store = store;
    }

    /// <summary>Pair a connected transport's watch. Round trip:
    /// <list type="number">
    ///   <item>Read the watch's Ed25519 public key.</item>
    ///   <item>Generate / load this companion's Ed25519 keypair.</item>
    ///   <item>Pick a fresh 20-byte room key.</item>
    ///   <item>Sign companion's pubkey + room key, pack 116-byte payload.</item>
    ///   <item>Write to BLE; receive the watch's 64-byte signature.</item>
    ///   <item>Verify the watch's signature against the expected
    ///         signed-domain bytes.</item>
    ///   <item>Persist the resulting <see cref="PairingRecord"/>.</item>
    /// </list>
    /// </summary>
    /// <exception cref="PairingException">Thrown if the watch returns
    /// the wrong number of bytes, or its signature doesn't verify.</exception>
    public async Task<PairingRecord> PairAsync(ITransport transport, string pairingCode, string? friendlyName = null, CancellationToken ct = default)
    {
        // Level 2 MITM defense: the 6-digit code the user read off the watch screen and
        // typed in is folded into BOTH signed domains (never sent on the wire). The watch
        // verifies the companion's signature against its OWN code, so a relay/attacker that
        // could not see the screen cannot produce a signature the watch accepts.
        byte[] code = PairingHandshake.CodeToBytes(pairingCode);

        // 1. Watch's public key.
        byte[] watchPubRaw = await transport.ReadWatchPublicKeyAsync(ct);
        if (watchPubRaw.Length != PairingHandshake.PubKeyLength)
            throw new PairingException($"Watch public key was {watchPubRaw.Length} bytes, expected {PairingHandshake.PubKeyLength}.");

        // 2. Our keypair. Reuse if we've paired before; generate fresh otherwise.
        // For now we always generate a fresh keypair per pairing - which means
        // the user gets one keypair per (companion, watch) pair. Phase 7
        // refinement may consolidate to one keypair per Companion if that
        // matches TJ's intent better.
        using var ourKey = await _crypto.GenerateEd25519Key();
        byte[] ourPubSpki  = await _crypto.ExportPublicKeySpki(ourKey);
        byte[] ourPrivPkcs8 = await _crypto.ExportPrivateKeyPkcs8(ourKey);

        byte[] ourPubRaw = ExtractRawEd25519PubKey(ourPubSpki);

        // 3. Fresh room key. Random 20 bytes; matches SpawnDev.RTC RoomKey shape.
        var roomKey = new byte[PairingHandshake.RoomKeyLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(roomKey);

        // 4. Build domain to sign + sign + pack.
        byte[] signedDomain = PairingHandshakeWire.SignedDomainCompanionToWatch(ourPubRaw, roomKey, code);
        byte[] signature = await _crypto.Sign(ourKey, signedDomain);
        if (signature.Length != PairingHandshake.SignatureLength)
            throw new PairingException($"Companion Ed25519 signature was {signature.Length} bytes, expected {PairingHandshake.SignatureLength}.");
        byte[] writePayload = PairingHandshakeWire.PackCompanionWrite(ourPubRaw, roomKey, signature);

        // 5. Send + receive watch's signature.
        byte[] watchSignature = await transport.ExchangePairingHandshakeAsync(writePayload, ct);
        if (watchSignature.Length != PairingHandshake.SignatureLength)
            throw new PairingException($"Watch handshake response was {watchSignature.Length} bytes, expected {PairingHandshake.SignatureLength}.");

        // 6. Verify.
        byte[] watchSignedDomain = PairingHandshakeWire.SignedDomainWatchToCompanion(ourPubRaw, roomKey, watchPubRaw, code);
        byte[] watchPubSpki = WrapRawEd25519PubKey(watchPubRaw);
        using var watchVerifyKey = await _crypto.ImportEd25519Key(watchPubSpki);
        bool ok = await _crypto.Verify(watchVerifyKey, watchSignedDomain, watchSignature);
        if (!ok)
            throw new PairingException("Watch's pairing signature did not verify - aborting pairing.");

        // 7. Persist.
        var record = new PairingRecord(
            WatchPubKey: watchPubRaw,
            OurPubKey:   ourPubRaw,
            OurPrivKey:  ourPrivPkcs8,        // PKCS8 envelope; LocalStoragePairingStore base64s it
            RoomKey:     roomKey,
            PairedAt:    DateTimeOffset.UtcNow,
            FriendlyName: friendlyName);
        _store.Save(record);
        return record;
    }

    // Ed25519 SPKI = 12-byte ASN.1 prefix + 32-byte raw key.
    static byte[] ExtractRawEd25519PubKey(byte[] spki)
    {
        if (spki.Length < 12 + PairingHandshake.PubKeyLength)
            throw new PairingException($"Ed25519 SPKI was {spki.Length} bytes, expected at least {12 + PairingHandshake.PubKeyLength}.");
        var raw = new byte[PairingHandshake.PubKeyLength];
        Buffer.BlockCopy(spki, spki.Length - PairingHandshake.PubKeyLength, raw, 0, PairingHandshake.PubKeyLength);
        return raw;
    }

    // Wrap a raw 32-byte Ed25519 key into the SPKI envelope so we can
    // import it via IPortableCrypto.ImportEd25519Key for verify-only use.
    // SPKI prefix shape: SEQUENCE { SEQUENCE { OID 1.3.101.112 }, BIT STRING (0 unused) { 32 bytes } }
    static readonly byte[] _ed25519SpkiPrefix =
    {
        0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00,
    };

    static byte[] WrapRawEd25519PubKey(byte[] raw)
    {
        if (raw.Length != PairingHandshake.PubKeyLength)
            throw new PairingException($"Raw Ed25519 pubkey must be exactly {PairingHandshake.PubKeyLength} bytes.");
        var spki = new byte[_ed25519SpkiPrefix.Length + raw.Length];
        Buffer.BlockCopy(_ed25519SpkiPrefix, 0, spki, 0, _ed25519SpkiPrefix.Length);
        Buffer.BlockCopy(raw, 0, spki, _ed25519SpkiPrefix.Length, raw.Length);
        return spki;
    }
}

/// <summary>Thrown when the pairing handshake fails. Distinct from
/// transport-level exceptions so consumers can show a clean
/// "pairing failed; try again" message.</summary>
public class PairingException : Exception
{
    public PairingException(string message) : base(message) { }
    public PairingException(string message, Exception inner) : base(message, inner) { }
}
