using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.BlazorJS.Cryptography.DotNet;
using SpawnWear.Bridge.Pairing;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// End-to-end test of <see cref="PairingFlow"/> with a real Ed25519
/// implementation on both sides. The test plays both roles: it runs
/// PairingFlow as the COMPANION, and a parallel "watch simulator"
/// signs the response with a separate keypair so verification can
/// pass like it would against a real watch.
///
/// This exercises every byte of the wire format + every crypto
/// operation that Phase 7's Companion side will actually run. If the
/// real watch firmware later signs the same domain bytes we
/// construct here, the handshake will round-trip end-to-end on
/// silicon.
/// </summary>
public class PairingFlowTests
{
    sealed class InMemoryPairingStore : IPairingStore
    {
        readonly Dictionary<string, PairingRecord> _byHex = new();
        public IReadOnlyList<PairingRecord> List() => _byHex.Values.ToList();
        public PairingRecord? Find(byte[] watchPubKey) => _byHex.TryGetValue(Hex(watchPubKey), out var r) ? r : null;
        public void Save(PairingRecord record) => _byHex[Hex(record.WatchPubKey)] = record;
        public void Remove(byte[] watchPubKey) => _byHex.Remove(Hex(watchPubKey));
        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    }

    static readonly byte[] _ed25519SpkiPrefix = { 0x30, 0x2A, 0x30, 0x05, 0x06, 0x03, 0x2B, 0x65, 0x70, 0x03, 0x21, 0x00 };

    static byte[] RawFromSpki(byte[] spki) => spki[^32..];

    static byte[] SpkiFromRaw(byte[] raw)
    {
        var spki = new byte[_ed25519SpkiPrefix.Length + raw.Length];
        Buffer.BlockCopy(_ed25519SpkiPrefix, 0, spki, 0, _ed25519SpkiPrefix.Length);
        Buffer.BlockCopy(raw, 0, spki, _ed25519SpkiPrefix.Length, raw.Length);
        return spki;
    }

    [Fact]
    public async Task PairAsync_round_trips_ed25519_signatures_and_persists_record()
    {
        var crypto = new DotNetCrypto();
        var store = new InMemoryPairingStore();
        var transport = new FakeTransport();

        // Watch generates its keypair before being paired-to. Real watch does
        // this once on first boot; we mimic by generating it here.
        using var watchKey = await crypto.GenerateEd25519Key();
        byte[] watchPubSpki = await crypto.ExportPublicKeySpki(watchKey);
        byte[] watchPubRaw = RawFromSpki(watchPubSpki);
        transport.FakeWatchPubKey = watchPubRaw;

        // FakeTransport: when PairingFlow calls ExchangePairingHandshakeAsync
        // we need to inspect its 116-byte write payload, simulate the watch
        // signing the WatchToCompanion domain, and feed back the 64-byte
        // response. We do that by setting FakeHandshakeResponse before the
        // call - but we don't know the companion pubkey + room key yet
        // (PairingFlow generates them internally). So we use a hook.
        var capturedRequest = new TaskCompletionSource<byte[]>();
        var responseProvider = new TaskCompletionSource<byte[]>();
        // Override: the simple FakeTransport doesn't support a callback hook,
        // so we use a custom subclass for this test.
        var smartTransport = new HookedFakeTransport(transport.FakeWatchPubKey, async sentPayload =>
        {
            capturedRequest.TrySetResult(sentPayload);
            // Parse the companion's write to get its pubkey + room key, then
            // sign WatchToCompanion(companionPub || roomKey || watchPub) with
            // the watch's key and return that signature.
            var (companionPubRaw, roomKey, _companionSig) = PairingHandshakeWire.ParseCompanionWrite(sentPayload);
            var watchSignedDomain = PairingHandshakeWire.SignedDomainWatchToCompanion(companionPubRaw, roomKey, watchPubRaw);
            return await crypto.Sign(watchKey, watchSignedDomain);
        });

        var flow = new PairingFlow(crypto, store);
        var record = await flow.PairAsync(smartTransport, friendlyName: "Aubs's watch");

        // Record is well-formed.
        Assert.Equal(watchPubRaw, record.WatchPubKey);
        Assert.Equal(32, record.OurPubKey.Length);
        Assert.True(record.OurPrivKey.Length > 32, "PKCS8 envelope is more than 32 bytes.");
        Assert.Equal(20, record.RoomKey.Length);
        Assert.Equal("Aubs's watch", record.FriendlyName);

        // Persisted.
        Assert.NotNull(store.Find(watchPubRaw));

        // The companion's write was actually sent.
        Assert.True(capturedRequest.Task.IsCompleted);
        var sent = await capturedRequest.Task;
        Assert.Equal(PairingHandshake.CompanionToWatchLength, sent.Length);

        // Sanity: the companion's own signature in the write verifies under
        // its own pubkey when reconstructed.
        var (sentCompanionPub, sentRoomKey, sentCompanionSig) = PairingHandshakeWire.ParseCompanionWrite(sent);
        var dom = PairingHandshakeWire.SignedDomainCompanionToWatch(sentCompanionPub, sentRoomKey);
        using var verifyKey = await crypto.ImportEd25519Key(SpkiFromRaw(sentCompanionPub));
        Assert.True(await crypto.Verify(verifyKey, dom, sentCompanionSig),
            "Companion's own signature in the BLE write should verify.");
    }

    [Fact]
    public async Task PairAsync_throws_if_watch_signature_does_not_verify()
    {
        var crypto = new DotNetCrypto();
        var store = new InMemoryPairingStore();

        using var watchKey = await crypto.GenerateEd25519Key();
        byte[] watchPubSpki = await crypto.ExportPublicKeySpki(watchKey);
        byte[] watchPubRaw = RawFromSpki(watchPubSpki);

        // Hooked transport returns a deliberately-wrong signature.
        var smartTransport = new HookedFakeTransport(watchPubRaw, _ =>
            Task.FromResult(new byte[64]));   // all zeros, definitely not a valid signature

        var flow = new PairingFlow(crypto, store);
        await Assert.ThrowsAsync<PairingException>(async () =>
            await flow.PairAsync(smartTransport));

        // Nothing persisted on failure.
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task PairAsync_throws_if_watch_returns_wrong_pubkey_length()
    {
        var crypto = new DotNetCrypto();
        var store = new InMemoryPairingStore();

        var smartTransport = new HookedFakeTransport(new byte[20] /* not 32 */, _ => Task.FromResult(new byte[64]));

        var flow = new PairingFlow(crypto, store);
        await Assert.ThrowsAsync<PairingException>(async () =>
            await flow.PairAsync(smartTransport));
    }

    /// <summary>FakeTransport variant that lets a test plug in a callback
    /// to dynamically build the handshake response based on the captured
    /// companion write.</summary>
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
        public Task<byte[]> ReadWatchPublicKeyAsync(CancellationToken ct = default) => Task.FromResult(_watchPub);
        public Task<byte[]> ExchangePairingHandshakeAsync(byte[] companionWritePayload, CancellationToken ct = default) =>
            _onWrite(companionWritePayload);
    }
}
