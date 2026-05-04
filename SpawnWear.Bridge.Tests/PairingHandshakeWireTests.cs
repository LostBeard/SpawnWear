using SpawnWear.Bridge.Pairing;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Wire-format tests for the BLE pairing handshake. Pure byte packing -
/// no crypto - so they run deterministically and lock the layout that
/// firmware + browser need to agree on.
/// </summary>
public class PairingHandshakeWireTests
{
    static byte[] FillPattern(int len, byte start)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)(start + i);
        return b;
    }

    [Fact]
    public void SignedDomainCompanionToWatch_concatenates_pub_then_room()
    {
        var pub = FillPattern(32, 0x10);
        var rk  = FillPattern(20, 0x80);
        var dom = PairingHandshakeWire.SignedDomainCompanionToWatch(pub, rk);

        Assert.Equal(52, dom.Length);
        for (int i = 0; i < 32; i++) Assert.Equal(pub[i], dom[i]);
        for (int i = 0; i < 20; i++) Assert.Equal(rk[i],  dom[32 + i]);
    }

    [Fact]
    public void SignedDomainWatchToCompanion_concatenates_companion_room_watch()
    {
        var compPub  = FillPattern(32, 0x10);
        var rk       = FillPattern(20, 0x80);
        var watchPub = FillPattern(32, 0x40);
        var dom = PairingHandshakeWire.SignedDomainWatchToCompanion(compPub, rk, watchPub);

        Assert.Equal(84, dom.Length);
        for (int i = 0; i < 32; i++) Assert.Equal(compPub[i],  dom[i]);
        for (int i = 0; i < 20; i++) Assert.Equal(rk[i],       dom[32 + i]);
        for (int i = 0; i < 32; i++) Assert.Equal(watchPub[i], dom[52 + i]);
    }

    [Fact]
    public void PackCompanionWrite_lays_out_pub_room_signature()
    {
        var pub = FillPattern(32, 0x10);
        var rk  = FillPattern(20, 0x80);
        var sig = FillPattern(64, 0xC0);
        var buf = PairingHandshakeWire.PackCompanionWrite(pub, rk, sig);

        Assert.Equal(PairingHandshake.CompanionToWatchLength, buf.Length);
        Assert.Equal(116, buf.Length);

        for (int i = 0; i < 32; i++) Assert.Equal(pub[i], buf[i]);
        for (int i = 0; i < 20; i++) Assert.Equal(rk[i],  buf[32 + i]);
        for (int i = 0; i < 64; i++) Assert.Equal(sig[i], buf[52 + i]);
    }

    [Fact]
    public void ParseCompanionWrite_round_trips_PackCompanionWrite()
    {
        var pub = FillPattern(32, 0x10);
        var rk  = FillPattern(20, 0x80);
        var sig = FillPattern(64, 0xC0);
        var buf = PairingHandshakeWire.PackCompanionWrite(pub, rk, sig);

        var (gotPub, gotRk, gotSig) = PairingHandshakeWire.ParseCompanionWrite(buf);

        Assert.Equal(pub, gotPub);
        Assert.Equal(rk,  gotRk);
        Assert.Equal(sig, gotSig);
    }

    [Theory]
    [InlineData(31)]   // pubkey too short
    [InlineData(33)]   // pubkey too long
    public void SignedDomainCompanionToWatch_rejects_wrong_pubkey_length(int len)
    {
        var pub = new byte[len];
        var rk  = new byte[20];
        Assert.Throws<ArgumentException>(() => PairingHandshakeWire.SignedDomainCompanionToWatch(pub, rk));
    }

    [Fact]
    public void SignedDomainCompanionToWatch_rejects_wrong_roomkey_length()
    {
        var pub = new byte[32];
        var rk  = new byte[16]; // would be GUID length, NOT 20-byte info_hash length
        Assert.Throws<ArgumentException>(() => PairingHandshakeWire.SignedDomainCompanionToWatch(pub, rk));
    }

    [Fact]
    public void ParseCompanionWrite_rejects_wrong_total_length()
    {
        var bad = new byte[100]; // not 116
        Assert.Throws<ArgumentException>(() => PairingHandshakeWire.ParseCompanionWrite(bad));
    }

    [Fact]
    public void Layout_constants_match_concatenated_field_lengths()
    {
        // Sanity: if any constant drifts, this test catches it.
        Assert.Equal(32, PairingHandshake.PubKeyLength);
        Assert.Equal(20, PairingHandshake.RoomKeyLength);
        Assert.Equal(64, PairingHandshake.SignatureLength);
        Assert.Equal(116, PairingHandshake.CompanionToWatchLength);
        Assert.Equal(64,  PairingHandshake.WatchToCompanionLength);
    }
}
