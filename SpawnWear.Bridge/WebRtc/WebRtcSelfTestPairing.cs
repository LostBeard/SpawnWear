using SpawnWear.Bridge.Pairing;

namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// A FIXED, embedded test pairing used to bring up the Phase 7 WebRTC path before the watch
/// firmware can be a peer. Both ends - the browser Companion and the .NET
/// <c>SpawnWear.Bridge.Desktop</c> "watch" peer - build their role-specific
/// <see cref="PairingRecord"/> from the same constants, so they share a room key + each
/// other's Ed25519 public keys without a real BLE pairing.
///
/// <para><b>Dev / demo only.</b> These keys are checked into source, so this is NOT a
/// production identity - it exists purely to prove browser &lt;-&gt; .NET WebRTC interop over
/// the hub (representative of the eventual browser &lt;-&gt; libpeer watch path). Real pairings
/// come from the BLE handshake and live in localStorage / the watch's flash.</para>
/// </summary>
public static class WebRtcSelfTestPairing
{
    // Generated once via `dotnet run --project SpawnWear.Bridge.Desktop -- genpair`.
    const string CompanionPubB64  = "HRuY0ydXxLpvLb+u42+ip4vIKLyt3hN+742QveydwGo=";
    const string CompanionPrivB64 = "MC4CAQAwBQYDK2VwBCIEILWr6nfKtDoZ3651YdAxI6/YJO4BIK0YxtwLW0DdIm5W";
    const string WatchPubB64      = "JbbgSd/rXW48T1arOQx5Wu+EpmZLagHp9Vx3NBOg2Ts=";
    const string WatchPrivB64     = "MC4CAQAwBQYDK2VwBCIEIEQ6MEGP3kcsaDi1lif9aaM/e+AgHss1SNe99w2yiAHi";
    const string RoomKeyB64       = "jYNwsQuvnNOjO6SW6t4CwgCTmlo=";

    static byte[] CompanionPub  => Convert.FromBase64String(CompanionPubB64);
    static byte[] CompanionPriv => Convert.FromBase64String(CompanionPrivB64);
    static byte[] WatchPub      => Convert.FromBase64String(WatchPubB64);
    static byte[] WatchPriv     => Convert.FromBase64String(WatchPrivB64);
    static byte[] RoomKey       => Convert.FromBase64String(RoomKeyB64);

    /// <summary>Browser/companion-role record: Our = companion, the peer (Watch) = the .NET peer.</summary>
    public static PairingRecord CompanionRecord() => new(
        WatchPubKey: WatchPub, OurPubKey: CompanionPub, OurPrivKey: CompanionPriv,
        RoomKey: RoomKey, PairedAt: DateTimeOffset.UnixEpoch, FriendlyName: "WebRTC self-test peer");

    /// <summary>.NET-peer/watch-role record: Our = the .NET peer, the peer (Watch) = the companion.</summary>
    public static PairingRecord WatchRecord() => new(
        WatchPubKey: CompanionPub, OurPubKey: WatchPub, OurPrivKey: WatchPriv,
        RoomKey: RoomKey, PairedAt: DateTimeOffset.UnixEpoch, FriendlyName: "WebRTC self-test companion");
}
