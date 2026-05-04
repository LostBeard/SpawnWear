using SpawnDev.BlazorJS.Cryptography;
using SpawnWear.Bridge.WebRtc;

namespace SpawnWear.Bridge.Tests;

public class BridgeWebRtcOptionsTests
{
    [Fact]
    public void Defaults_point_at_hub_spawndev_com_44365()
    {
        var opts = new BridgeWebRtcOptions();
        Assert.Equal("wss://hub.spawndev.com:44365/announce", opts.AnnounceUrl);
        Assert.Contains("stun:hub.spawndev.com:3478", opts.StunUrls);
        Assert.Equal("max-bundle", opts.BundlePolicy);
        Assert.Equal("all", opts.IceTransportPolicy);
        Assert.Empty(opts.TurnServers);
    }

    [Fact]
    public void ToPeerConnectionConfig_combines_stun_then_turn()
    {
        var opts = new BridgeWebRtcOptions
        {
            StunUrls = new[] { "stun:test1.example.com", "stun:test2.example.com" },
            TurnServers = new[]
            {
                new SpawnDev.RTC.RTCIceServerConfig("turn:relay.example.com:3478")
                {
                    Username = "user", Credential = "pass",
                },
            },
            BundlePolicy = "balanced",
            IceTransportPolicy = "relay",
        };

        var cfg = opts.ToPeerConnectionConfig();

        Assert.NotNull(cfg.IceServers);
        Assert.Equal(3, cfg.IceServers!.Length);
        // STUN first, then TURN
        Assert.Equal("stun:test1.example.com",     cfg.IceServers[0].Urls[0]);
        Assert.Equal("stun:test2.example.com",     cfg.IceServers[1].Urls[0]);
        Assert.Equal("turn:relay.example.com:3478", cfg.IceServers[2].Urls[0]);
        Assert.Equal("user", cfg.IceServers[2].Username);
        Assert.Equal("pass", cfg.IceServers[2].Credential);
        Assert.Equal("balanced", cfg.BundlePolicy);
        Assert.Equal("relay", cfg.IceTransportPolicy);
    }
}

public class WebRtcTransportFactoryTests
{
    [Fact]
    public void GeneratesPeerId_is_20_bytes_with_SW_prefix()
    {
        var factory = new WebRtcTransportFactory(null, new DotNetCrypto());
        Assert.Equal(20, factory.PeerId.Length);
        // "-SW0001-" prefix
        var prefix = System.Text.Encoding.ASCII.GetString(factory.PeerId, 0, 8);
        Assert.Equal("-SW0001-", prefix);
    }

    [Fact]
    public void Two_factories_get_distinct_peer_ids_when_unspecified()
    {
        var f1 = new WebRtcTransportFactory(null, new DotNetCrypto());
        var f2 = new WebRtcTransportFactory(null, new DotNetCrypto());
        // Random tail past the 8-byte prefix should differ
        var tail1 = f1.PeerId[8..];
        var tail2 = f2.PeerId[8..];
        Assert.NotEqual(tail1, tail2);
    }

    [Fact]
    public void Provided_peer_id_is_used_verbatim()
    {
        var seed = new byte[20];
        for (int i = 0; i < 20; i++) seed[i] = (byte)i;
        var factory = new WebRtcTransportFactory(null, new DotNetCrypto(), peerId: seed);
        Assert.Equal(seed, factory.PeerId);
    }

    [Fact]
    public void Null_options_uses_defaults()
    {
        var factory = new WebRtcTransportFactory(null, new DotNetCrypto());
        // Smoke: factory still constructible with null options.
        Assert.NotNull(factory.PeerId);
    }
}
