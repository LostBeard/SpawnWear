using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Cryptography.DotNet;
using SpawnWear.Bridge.Pairing;
using SpawnWear.Bridge.WebRtc;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// End-to-end integration tests that drive the complete Phase 7a -> 7b
/// production path. Each test runs the REAL <see cref="PairingFlow"/>
/// to produce a real <see cref="PairingRecord"/>, then runs the REAL
/// <see cref="WebRtcChallenge"/> primitives against that record's
/// stored keys to perform a mutual-auth handshake the way the watch
/// will do it post-WebRTC-data-channel-open.
///
/// This is the bridge test the existing pairing-only and webrtc-only
/// suites do NOT cover: the trust anchor stored during Phase 7a pairing
/// must be the same key material that authenticates the Phase 7b
/// WebRTC challenge, in both directions. If a refactor breaks the
/// pairing/record/challenge interlock, these tests fail.
///
/// Real cryptography on both sides via <see cref="DotNetCrypto"/>;
/// real wire bytes; no mocks beyond <see cref="HookedFakeTransport"/>
/// (which simulates the watch's BLE-side signing during pairing).
/// </summary>
public class PairingWebRtcIntegrationTests
{
    static readonly byte[] _ed25519SpkiPrefix =
        { 0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00 };

    static byte[] RawFromSpki(byte[] spki) => spki[^32..];

    static byte[] SpkiFromRaw(byte[] raw)
    {
        var spki = new byte[_ed25519SpkiPrefix.Length + raw.Length];
        Buffer.BlockCopy(_ed25519SpkiPrefix, 0, spki, 0, _ed25519SpkiPrefix.Length);
        Buffer.BlockCopy(raw, 0, spki, _ed25519SpkiPrefix.Length, raw.Length);
        return spki;
    }

    sealed class InMemoryPairingStore : IPairingStore
    {
        readonly Dictionary<string, PairingRecord> _byHex = new();
        public IReadOnlyList<PairingRecord> List() => _byHex.Values.ToList();
        public PairingRecord? Find(byte[] watchPubKey) =>
            _byHex.TryGetValue(Hex(watchPubKey), out var r) ? r : null;
        public void Save(PairingRecord record) => _byHex[Hex(record.WatchPubKey)] = record;
        public void Remove(byte[] watchPubKey) => _byHex.Remove(Hex(watchPubKey));
        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    }

    sealed class HookedFakeTransport : ITransport
    {
        readonly byte[] _watchPub;
        readonly Func<byte[], Task<byte[]>> _onWrite;
        public HookedFakeTransport(byte[] watchPub, Func<byte[], Task<byte[]>> onWrite)
        {
            _watchPub = watchPub;
            _onWrite = onWrite;
        }
        public bool IsConnected => true;
        public string? PeerName => "HookedFakeWatch";
#pragma warning disable CS0067
        public event Action<bool>? ConnectionChanged;
        public event Action<TransportMessage>? MessageReceived;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(TransportMessage message, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<byte[]> ReadWatchPublicKeyAsync(CancellationToken ct = default) =>
            Task.FromResult(_watchPub);
        public Task<byte[]> ExchangePairingHandshakeAsync(byte[] companionWritePayload, CancellationToken ct = default) =>
            _onWrite(companionWritePayload);
    }

    /// <summary>Bundle of everything the integration tests need after a
    /// pair completes: the real <see cref="PairingRecord"/> the flow
    /// produced, plus the watch-side keypair the simulator used to
    /// sign during pairing (so it can also sign WebRTC challenges
    /// later).</summary>
    sealed class PairedSession
    {
        public required IPortableCrypto Crypto { get; init; }
        public required PairingRecord Record { get; init; }
        public required PortableEd25519Key WatchPrivateKey { get; init; }
    }

    /// <summary>Run the full pairing flow with a watch simulator so each
    /// integration test starts from a real PairingRecord backed by
    /// real keys on both sides. The watch sim's private key is
    /// returned so the same identity can sign WebRTC challenges later
    /// (just like real silicon would, since on a real watch the BLE
    /// pairing key IS the WebRTC challenge-signing key).</summary>
    static async Task<PairedSession> PairAsync(string friendlyName = "test watch")
    {
        var crypto = new DotNetCrypto();
        var watchKey = await crypto.GenerateEd25519Key();
        byte[] watchPubRaw = RawFromSpki(await crypto.ExportPublicKeySpki(watchKey));

        var transport = new HookedFakeTransport(watchPubRaw, async sentPayload =>
        {
            var (companionPubRaw, roomKey, _) = PairingHandshakeWire.ParseCompanionWrite(sentPayload);
            var dom = PairingHandshakeWire.SignedDomainWatchToCompanion(companionPubRaw, roomKey, watchPubRaw);
            return await crypto.Sign(watchKey, dom);
        });

        var flow = new PairingFlow(crypto, new InMemoryPairingStore());
        var record = await flow.PairAsync(transport, friendlyName);

        return new PairedSession
        {
            Crypto = crypto,
            Record = record,
            WatchPrivateKey = watchKey,
        };
    }

    [Fact]
    public async Task Pair_then_watch_signed_challenge_verifies_under_stored_WatchPubKey()
    {
        // The canonical Phase 7b post-pairing handshake. After WebRTC
        // data channel opens, Companion picks a 32B nonce, sends it,
        // watch signs SignedDomain(nonce) with its Ed25519 private key,
        // ships back PackResponse(echo, sig). Companion ParseResponse,
        // verifies the signature with WatchPubKey from the PairingRecord.
        // If this round-trip fails, post-pair WebRTC trust is broken.
        var session = await PairAsync();

        // Companion: issue challenge.
        byte[] nonce = WebRtcChallenge.GenerateNonce();
        byte[] request = WebRtcChallenge.PackRequest(nonce);
        Assert.Equal(WebRtcChallenge.ChallengeRequestLength, request.Length);

        // Watch: sign the SignedDomain of the nonce we just received.
        byte[] watchSig = await session.Crypto.Sign(
            session.WatchPrivateKey, WebRtcChallenge.SignedDomain(request));
        byte[] response = WebRtcChallenge.PackResponse(request, watchSig);
        Assert.Equal(WebRtcChallenge.ChallengeResponseLength, response.Length);

        // Companion: parse + verify under stored WatchPubKey.
        var (echoedNonce, parsedSig) = WebRtcChallenge.ParseResponse(response);
        Assert.Equal(nonce, echoedNonce);    // application-level replay check
        using var verifyKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(session.Record.WatchPubKey));
        bool verified = await session.Crypto.Verify(
            verifyKey, WebRtcChallenge.SignedDomain(echoedNonce), parsedSig);

        Assert.True(verified, "Watch's challenge signature must verify under the WatchPubKey stored at pair time.");
    }

    [Fact]
    public async Task Pair_then_companion_signed_challenge_verifies_under_stored_OurPubKey()
    {
        // Reverse direction: watch challenges Companion. Companion uses
        // OurPrivKey from the PairingRecord (re-imported from PKCS8),
        // signs the nonce, sends back. Watch verifies using OurPubKey
        // also from the record. This proves OurPrivKey survives the
        // record round-trip with sign-capable fidelity AND that
        // OurPubKey matches.
        var session = await PairAsync();

        // Watch sim: issue challenge.
        byte[] nonce = WebRtcChallenge.GenerateNonce();

        // Companion: re-import our own keypair from the record. This is
        // exactly what WebRtcTransport.cs does at startup.
        using var ourSignKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(session.Record.OurPubKey),
            session.Record.OurPrivKey);
        byte[] companionSig = await session.Crypto.Sign(ourSignKey, WebRtcChallenge.SignedDomain(nonce));
        byte[] response = WebRtcChallenge.PackResponse(nonce, companionSig);

        // Watch sim: verify using OurPubKey from the same record.
        var (echoedNonce, parsedSig) = WebRtcChallenge.ParseResponse(response);
        using var watchVerifiesCompanionKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(session.Record.OurPubKey));
        bool verified = await session.Crypto.Verify(
            watchVerifiesCompanionKey, WebRtcChallenge.SignedDomain(echoedNonce), parsedSig);

        Assert.True(verified, "Companion's signature with stored OurPrivKey must verify under stored OurPubKey.");
    }

    [Fact]
    public async Task Imposter_watch_with_different_keypair_cannot_pass_paired_challenge()
    {
        // Security property: pair watch A. An attacker watch B with its
        // own keypair tries to authenticate to companion by signing
        // companion's nonce with B's key. Companion verifies with A's
        // pubkey from the record. Must fail - or BLE-pairing trust is
        // worthless.
        var legitSession = await PairAsync("Aubs's watch");

        // Imposter: separate Ed25519 keypair, NOT the one paired.
        using var imposterKey = await legitSession.Crypto.GenerateEd25519Key();

        byte[] nonce = WebRtcChallenge.GenerateNonce();
        byte[] imposterSig = await legitSession.Crypto.Sign(imposterKey, WebRtcChallenge.SignedDomain(nonce));
        byte[] response = WebRtcChallenge.PackResponse(nonce, imposterSig);

        var (echoedNonce, parsedSig) = WebRtcChallenge.ParseResponse(response);
        using var legitVerifyKey = await legitSession.Crypto.ImportEd25519Key(
            SpkiFromRaw(legitSession.Record.WatchPubKey));
        bool verified = await legitSession.Crypto.Verify(
            legitVerifyKey, WebRtcChallenge.SignedDomain(echoedNonce), parsedSig);

        Assert.False(verified, "Imposter signature MUST NOT verify under paired watch's pubkey.");
    }

    [Fact]
    public async Task Tampered_nonce_in_response_breaks_signature_verification()
    {
        // Integrity property: the response nonce is echoed for replay
        // detection but a man-in-the-middle could rewrite it. Verify
        // that flipping even one byte of the echoed nonce makes the
        // signature stop verifying (because Verify hashes
        // SignedDomain(nonce) and the nonce is now different than
        // what the watch actually signed).
        var session = await PairAsync();

        byte[] nonce = WebRtcChallenge.GenerateNonce();
        byte[] watchSig = await session.Crypto.Sign(
            session.WatchPrivateKey, WebRtcChallenge.SignedDomain(nonce));
        byte[] response = WebRtcChallenge.PackResponse(nonce, watchSig);

        // Tamper: flip one bit in the echoed nonce field (offset 0..31).
        response[5] ^= 0x01;

        var (echoedNonce, parsedSig) = WebRtcChallenge.ParseResponse(response);
        using var verifyKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(session.Record.WatchPubKey));
        bool verified = await session.Crypto.Verify(
            verifyKey, WebRtcChallenge.SignedDomain(echoedNonce), parsedSig);

        Assert.False(verified, "Tampered echoed-nonce MUST break signature verification.");
    }

    [Fact]
    public async Task Tampered_signature_in_response_breaks_verification()
    {
        // Integrity property: flipping any byte of the signature field
        // (offset 32..95) MUST cause verification to fail. Otherwise
        // an attacker who sees a valid response could forge variants.
        var session = await PairAsync();

        byte[] nonce = WebRtcChallenge.GenerateNonce();
        byte[] watchSig = await session.Crypto.Sign(
            session.WatchPrivateKey, WebRtcChallenge.SignedDomain(nonce));
        byte[] response = WebRtcChallenge.PackResponse(nonce, watchSig);

        // Tamper: flip one bit in the signature field.
        response[40] ^= 0x80;

        var (echoedNonce, parsedSig) = WebRtcChallenge.ParseResponse(response);
        using var verifyKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(session.Record.WatchPubKey));
        bool verified = await session.Crypto.Verify(
            verifyKey, WebRtcChallenge.SignedDomain(echoedNonce), parsedSig);

        Assert.False(verified, "Tampered signature MUST break verification.");
    }

    [Fact]
    public async Task Two_paired_watches_have_independent_trust_anchors()
    {
        // Multi-watch property: pair watch A and watch B (separate
        // PairingRecords with different WatchPubKey). A challenge
        // signed by A's key MUST verify under A's stored pubkey AND
        // MUST NOT verify under B's stored pubkey, and vice versa.
        // No cross-talk between trust anchors.
        var sessionA = await PairAsync("Watch A");
        var sessionB = await PairAsync("Watch B");

        Assert.NotEqual(sessionA.Record.WatchPubKey, sessionB.Record.WatchPubKey);

        byte[] nonce = WebRtcChallenge.GenerateNonce();

        // Watch A signs.
        byte[] aSig = await sessionA.Crypto.Sign(
            sessionA.WatchPrivateKey, WebRtcChallenge.SignedDomain(nonce));

        // Verify A's response under both A's and B's pubkeys.
        using var aVerify = await sessionA.Crypto.ImportEd25519Key(SpkiFromRaw(sessionA.Record.WatchPubKey));
        using var bVerify = await sessionB.Crypto.ImportEd25519Key(SpkiFromRaw(sessionB.Record.WatchPubKey));

        bool aValidatesA = await sessionA.Crypto.Verify(aVerify, WebRtcChallenge.SignedDomain(nonce), aSig);
        bool bValidatesA = await sessionB.Crypto.Verify(bVerify, WebRtcChallenge.SignedDomain(nonce), aSig);

        Assert.True(aValidatesA, "A's signature MUST verify under A's pubkey.");
        Assert.False(bValidatesA, "A's signature MUST NOT verify under B's pubkey.");
    }

    [Fact]
    public async Task Re_pair_invalidates_old_companion_keypair_for_authentication()
    {
        // Security property: re-pairing the same watch generates a fresh
        // Companion-side keypair (PairingFlow makes a new one every call).
        // A challenge signed with the OLD OurPrivKey MUST NOT verify
        // under the NEW OurPubKey. This is what makes "revoke old
        // companion by re-pairing" actually revoke anything.
        var crypto = new DotNetCrypto();
        var store = new InMemoryPairingStore();

        // Same watch identity across both pair attempts.
        using var watchKey = await crypto.GenerateEd25519Key();
        byte[] watchPubRaw = RawFromSpki(await crypto.ExportPublicKeySpki(watchKey));

        Func<HookedFakeTransport> makeTransport = () =>
            new HookedFakeTransport(watchPubRaw, async sentPayload =>
            {
                var (companionPubRaw, roomKey, _) = PairingHandshakeWire.ParseCompanionWrite(sentPayload);
                var dom = PairingHandshakeWire.SignedDomainWatchToCompanion(companionPubRaw, roomKey, watchPubRaw);
                return await crypto.Sign(watchKey, dom);
            });

        var flow = new PairingFlow(crypto, store);
        var firstRecord = await flow.PairAsync(makeTransport(), "first companion");
        var secondRecord = await flow.PairAsync(makeTransport(), "second companion");

        // OurPubKey + OurPrivKey changed across the re-pair.
        Assert.NotEqual(firstRecord.OurPubKey, secondRecord.OurPubKey);
        Assert.NotEqual(firstRecord.OurPrivKey, secondRecord.OurPrivKey);

        // Try to authenticate using the OLD private key against the NEW
        // pubkey -> must fail.
        byte[] nonce = WebRtcChallenge.GenerateNonce();
        using var oldSignKey = await crypto.ImportEd25519Key(
            SpkiFromRaw(firstRecord.OurPubKey),
            firstRecord.OurPrivKey);
        byte[] oldSig = await crypto.Sign(oldSignKey, WebRtcChallenge.SignedDomain(nonce));

        using var newVerifyKey = await crypto.ImportEd25519Key(SpkiFromRaw(secondRecord.OurPubKey));
        bool verified = await crypto.Verify(newVerifyKey, WebRtcChallenge.SignedDomain(nonce), oldSig);

        Assert.False(verified, "Old companion private key MUST NOT authenticate under the post-re-pair OurPubKey.");
    }

    [Fact]
    public async Task Echoed_nonce_mismatch_lets_caller_detect_response_to_a_different_challenge()
    {
        // Replay-detection property: even if a signature is valid, if the
        // echoed nonce doesn't match the challenge nonce the caller
        // issued, this is either a stale response or a cross-challenge
        // replay. Production code MUST compare the echoed nonce against
        // the issued nonce and reject mismatches. This test pins that
        // the parser exposes the echoed nonce so the caller has the
        // information to perform that check.
        var session = await PairAsync();

        byte[] issuedNonce = WebRtcChallenge.GenerateNonce();
        byte[] differentNonce = WebRtcChallenge.GenerateNonce();
        Assert.NotEqual(issuedNonce, differentNonce);

        // Watch signs and packs a response to differentNonce (NOT the
        // one we issued).
        byte[] sig = await session.Crypto.Sign(
            session.WatchPrivateKey, WebRtcChallenge.SignedDomain(differentNonce));
        byte[] response = WebRtcChallenge.PackResponse(differentNonce, sig);

        var (echoedNonce, parsedSig) = WebRtcChallenge.ParseResponse(response);

        // Signature is mathematically valid for the echoed nonce
        // (because the watch DID sign that nonce) but caller can detect
        // the mismatch.
        using var verifyKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(session.Record.WatchPubKey));
        bool sigValid = await session.Crypto.Verify(
            verifyKey, WebRtcChallenge.SignedDomain(echoedNonce), parsedSig);
        bool echoMatchesIssued = echoedNonce.SequenceEqual(issuedNonce);

        Assert.True(sigValid, "Signature is valid for the nonce that was actually signed.");
        Assert.False(echoMatchesIssued, "Echoed nonce does NOT match issued nonce - caller can reject.");
    }

    [Fact]
    public async Task GenerateNonce_produces_unique_values_across_many_calls()
    {
        // Replay-protection-at-source: the random nonce generator must
        // not collide. Even though Ed25519 is collision-resistant in
        // signature space, reusing nonces would let an attacker replay
        // a captured response. 32 bytes (256 bits) of randomness gives
        // negligible collision probability; verify that >0 collisions
        // is essentially impossible across a reasonable test count.
        await Task.CompletedTask;
        const int count = 1000;
        var seen = new HashSet<string>(count);
        for (int i = 0; i < count; i++)
        {
            var n = WebRtcChallenge.GenerateNonce();
            Assert.Equal(WebRtcChallenge.NonceLength, n.Length);
            string h = Convert.ToHexString(n);
            Assert.True(seen.Add(h), $"GenerateNonce returned a duplicate at iteration {i}: {h}");
        }
        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public async Task Stored_record_keys_survive_in_memory_round_trip_unchanged()
    {
        // Persistence property: the bytes the flow writes into a
        // PairingRecord are the same bytes a re-import yields back.
        // localStorage on the Companion side base64s the byte arrays;
        // I:\spawnwear-pair.bin on the watch stores raw. Both must
        // round-trip with no corruption or the next session can't
        // authenticate. Simulate by storing then reloading via the
        // store interface (sufficient for the in-memory case;
        // localStorage is exercised in the Companion Playwright
        // suite).
        var session = await PairAsync("Aubs's watch");
        var store = new InMemoryPairingStore();
        store.Save(session.Record);

        var reloaded = store.Find(session.Record.WatchPubKey);
        Assert.NotNull(reloaded);

        Assert.Equal(session.Record.WatchPubKey, reloaded!.Value.WatchPubKey);
        Assert.Equal(session.Record.OurPubKey,   reloaded.Value.OurPubKey);
        Assert.Equal(session.Record.OurPrivKey,  reloaded.Value.OurPrivKey);
        Assert.Equal(session.Record.RoomKey,     reloaded.Value.RoomKey);
        Assert.Equal(session.Record.FriendlyName, reloaded.Value.FriendlyName);

        // The reloaded record can still drive a successful WebRTC challenge.
        byte[] nonce = WebRtcChallenge.GenerateNonce();
        byte[] watchSig = await session.Crypto.Sign(
            session.WatchPrivateKey, WebRtcChallenge.SignedDomain(nonce));
        using var verifyKey = await session.Crypto.ImportEd25519Key(
            SpkiFromRaw(reloaded.Value.WatchPubKey));
        Assert.True(await session.Crypto.Verify(
            verifyKey, WebRtcChallenge.SignedDomain(nonce), watchSig),
            "Reloaded record's WatchPubKey must still verify the watch's signatures.");
    }
}
