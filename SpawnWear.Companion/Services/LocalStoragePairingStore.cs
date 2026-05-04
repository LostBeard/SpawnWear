using System.Text.Json;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnWear.Bridge.Pairing;

namespace SpawnWear.Companion.Services;

/// <summary>
/// Browser-side <see cref="IPairingStore"/> backed by
/// <c>window.localStorage</c>. One key per paired watch, prefixed with
/// <c>spawnwear.pair.</c> followed by the watch's public-key hex.
///
/// Per-origin (the user's localhost dev server has a different store
/// than a hosted Companion build at <c>spawnwear.example.com</c>).
/// Cleared by the user via the browser's site-data tools - that
/// invalidates every pairing the user has done from this Companion
/// instance, requiring them to re-pair via BLE.
///
/// PairingRecord byte[] fields are base64-encoded for JSON-friendly
/// storage. The privkey is stored alongside the rest; if we ever want
/// to upgrade to a non-extractable WebCrypto key handle, the storage
/// shape changes - tracked in Plans/phase7-webrtc-handoff.md.
/// </summary>
public class LocalStoragePairingStore : IPairingStore
{
    const string KeyPrefix = "spawnwear.pair.";

    readonly BlazorJSRuntime _js;
    readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LocalStoragePairingStore(BlazorJSRuntime js)
    {
        _js = js;
    }

    public IReadOnlyList<PairingRecord> List()
    {
        var results = new List<PairingRecord>();
        try
        {
            using var window = _js.Get<Window>("window");
            using var ls = window.LocalStorage;
            if (ls is null) return results;
            var keys = ls.GetItemKeys();
            foreach (var key in keys)
            {
                if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                var raw = ls.GetItem(key);
                if (string.IsNullOrEmpty(raw)) continue;
                if (TryDeserialize(raw, out var rec))
                    results.Add(rec);
            }
        }
        catch { /* private mode / sandboxed / similar - return what we have */ }
        return results;
    }

    public PairingRecord? Find(byte[] watchPubKey)
    {
        if (watchPubKey is null || watchPubKey.Length != PairingHandshake.PubKeyLength)
            return null;
        try
        {
            using var window = _js.Get<Window>("window");
            using var ls = window.LocalStorage;
            if (ls is null) return null;
            var raw = ls.GetItem(KeyPrefix + Convert.ToHexString(watchPubKey).ToLowerInvariant());
            if (string.IsNullOrEmpty(raw)) return null;
            return TryDeserialize(raw, out var rec) ? rec : null;
        }
        catch { return null; }
    }

    public void Save(PairingRecord record)
    {
        if (record.WatchPubKey is null || record.WatchPubKey.Length != PairingHandshake.PubKeyLength)
            throw new ArgumentException("WatchPubKey must be 32 bytes.", nameof(record));
        try
        {
            using var window = _js.Get<Window>("window");
            using var ls = window.LocalStorage;
            if (ls is null) throw new InvalidOperationException("localStorage unavailable.");
            var key = KeyPrefix + Convert.ToHexString(record.WatchPubKey).ToLowerInvariant();
            var json = JsonSerializer.Serialize(Persisted.From(record), _opts);
            ls.SetItem(key, json);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Wrap underlying JS exceptions in something the consumer can handle.
            throw new InvalidOperationException("Failed to persist pairing record: " + ex.Message, ex);
        }
    }

    public void Remove(byte[] watchPubKey)
    {
        if (watchPubKey is null || watchPubKey.Length != PairingHandshake.PubKeyLength) return;
        try
        {
            using var window = _js.Get<Window>("window");
            using var ls = window.LocalStorage;
            if (ls is null) return;
            ls.RemoveItem(KeyPrefix + Convert.ToHexString(watchPubKey).ToLowerInvariant());
        }
        catch { /* swallow */ }
    }

    bool TryDeserialize(string json, out PairingRecord record)
    {
        record = default;
        try
        {
            var p = JsonSerializer.Deserialize<Persisted>(json, _opts);
            if (p is null) return false;
            record = p.ToRecord();
            return true;
        }
        catch { return false; }
    }

    /// <summary>JSON-friendly representation of <see cref="PairingRecord"/>.
    /// Bytes encoded as base64; DateTimeOffset round-trips natively.</summary>
    sealed class Persisted
    {
        public string? WatchPubKey  { get; set; }
        public string? OurPubKey    { get; set; }
        public string? OurPrivKey   { get; set; }
        public string? RoomKey      { get; set; }
        public DateTimeOffset PairedAt { get; set; }
        public string? FriendlyName { get; set; }

        public static Persisted From(PairingRecord r) => new()
        {
            WatchPubKey  = Convert.ToBase64String(r.WatchPubKey),
            OurPubKey    = Convert.ToBase64String(r.OurPubKey),
            OurPrivKey   = Convert.ToBase64String(r.OurPrivKey),
            RoomKey      = Convert.ToBase64String(r.RoomKey),
            PairedAt     = r.PairedAt,
            FriendlyName = r.FriendlyName,
        };

        public PairingRecord ToRecord() => new(
            WatchPubKey: Convert.FromBase64String(WatchPubKey ?? ""),
            OurPubKey:   Convert.FromBase64String(OurPubKey   ?? ""),
            OurPrivKey:  Convert.FromBase64String(OurPrivKey  ?? ""),
            RoomKey:     Convert.FromBase64String(RoomKey     ?? ""),
            PairedAt:    PairedAt,
            FriendlyName: FriendlyName);
    }
}
