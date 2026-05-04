namespace SpawnWear.Bridge.Pairing;

/// <summary>
/// Persistent storage of paired-watch material. One entry per paired
/// watch (a Companion can pair to multiple watches; a watch can pair
/// to multiple Companions, though only one paired-Companion pair is
/// stored at a time on the watch side - see
/// <c>Plans/phase7-webrtc-handoff.md</c>).
///
/// On Companion: backed by <c>localStorage</c>.
/// On Watch: backed by nanoFramework non-volatile storage.
///
/// Phase 7 stub - shape is locked, implementation lands when Phase 7
/// proper begins. Keeping the contract here means every Phase 7
/// commit has somewhere to drop in.
/// </summary>
public interface IPairingStore
{
    /// <summary>List every paired-watch entry the store knows about.</summary>
    IReadOnlyList<PairingRecord> List();

    /// <summary>Look up a single record by the watch's Ed25519 public
    /// key. Returns null if no pairing for that key has been saved.</summary>
    PairingRecord? Find(byte[] watchPubKey);

    /// <summary>Persist a new pairing record. Idempotent on
    /// <see cref="PairingRecord.WatchPubKey"/>: if a record already
    /// exists for that watch, this overwrites it (= re-pair flow).</summary>
    void Save(PairingRecord record);

    /// <summary>Remove a paired-watch record. Used when the user
    /// "forgets" a watch from the Companion's settings.</summary>
    void Remove(byte[] watchPubKey);
}

/// <summary>
/// One Ed25519-paired watch's persistent state. Exact byte layout
/// is documented in <c>Plans/phase7-webrtc-handoff.md</c>.
/// </summary>
public readonly record struct PairingRecord(
    byte[] WatchPubKey,        // 32 bytes
    byte[] OurPubKey,          // 32 bytes
    byte[] OurPrivKey,         // 32 bytes (Companion-side; on watch side this is unused — the watch's privkey is stored separately)
    byte[] RoomId,             // 16 bytes
    DateTimeOffset PairedAt,
    string? FriendlyName       // optional human label, e.g. "Aubs's watch"
);
