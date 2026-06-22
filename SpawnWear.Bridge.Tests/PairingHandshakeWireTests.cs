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

    // Level 2 pairing code (6 ASCII digits) folded into the signed domains.
    static readonly byte[] Code6 = PairingHandshake.CodeToBytes("123456");

    [Fact]
    public void SignedDomainCompanionToWatch_concatenates_pub_room_code()
    {
        var pub = FillPattern(32, 0x10);
        var rk  = FillPattern(20, 0x80);
        var dom = PairingHandshakeWire.SignedDomainCompanionToWatch(pub, rk, Code6);

        Assert.Equal(58, dom.Length);
        for (int i = 0; i < 32; i++) Assert.Equal(pub[i],   dom[i]);
        for (int i = 0; i < 20; i++) Assert.Equal(rk[i],    dom[32 + i]);
        for (int i = 0; i < 6;  i++) Assert.Equal(Code6[i], dom[52 + i]);
    }

    [Fact]
    public void SignedDomainWatchToCompanion_concatenates_companion_room_watch_code()
    {
        var compPub  = FillPattern(32, 0x10);
        var rk       = FillPattern(20, 0x80);
        var watchPub = FillPattern(32, 0x40);
        var dom = PairingHandshakeWire.SignedDomainWatchToCompanion(compPub, rk, watchPub, Code6);

        Assert.Equal(90, dom.Length);
        for (int i = 0; i < 32; i++) Assert.Equal(compPub[i],  dom[i]);
        for (int i = 0; i < 20; i++) Assert.Equal(rk[i],       dom[32 + i]);
        for (int i = 0; i < 32; i++) Assert.Equal(watchPub[i], dom[52 + i]);
        for (int i = 0; i < 6;  i++) Assert.Equal(Code6[i],    dom[84 + i]);
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
        Assert.Throws<ArgumentException>(() => PairingHandshakeWire.SignedDomainCompanionToWatch(pub, rk, Code6));
    }

    [Fact]
    public void SignedDomainCompanionToWatch_rejects_wrong_roomkey_length()
    {
        var pub = new byte[32];
        var rk  = new byte[16]; // would be GUID length, NOT 20-byte info_hash length
        Assert.Throws<ArgumentException>(() => PairingHandshakeWire.SignedDomainCompanionToWatch(pub, rk, Code6));
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
        Assert.Equal(6,   PairingHandshake.CodeLength);
    }

    [Fact]
    public void CodeToBytes_encodes_six_ascii_digits()
    {
        var b = PairingHandshake.CodeToBytes("042739");
        Assert.Equal(6, b.Length);
        Assert.Equal(new byte[] { (byte)'0', (byte)'4', (byte)'2', (byte)'7', (byte)'3', (byte)'9' }, b);
    }

    [Theory]
    [InlineData("12345")]    // too short
    [InlineData("1234567")]  // too long
    [InlineData("12 456")]   // contains a space
    [InlineData("abcdef")]   // non-digit
    public void CodeToBytes_rejects_malformed_code(string code)
    {
        Assert.Throws<ArgumentException>(() => PairingHandshake.CodeToBytes(code));
    }
}
