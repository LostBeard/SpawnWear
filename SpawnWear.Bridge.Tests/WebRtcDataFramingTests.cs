using SpawnWear.Bridge.WebRtc;

namespace SpawnWear.Bridge.Tests;

public class WebRtcDataFramingTests
{
    [Fact]
    public void Pack_then_Parse_round_trips_a_typical_battery_message()
    {
        var msg = new TransportMessage(ChannelIds.Battery,
            new byte[]{ 87, 0x03, 0xE8, 0x10, 0xCE, 0xFF });
        var frame = WebRtcDataFraming.Pack(msg);

        // 1 (idLen) + 7 ("battery") + 2 (payloadLen) + 6 (payload) = 16
        Assert.Equal(16, frame.Length);
        Assert.Equal((byte)7, frame[0]); // idLen
        Assert.Equal((byte)6, frame[8]); // payloadLen low
        Assert.Equal((byte)0, frame[9]); // payloadLen high

        var got = WebRtcDataFraming.Parse(frame);
        Assert.Equal(msg.ChannelId, got.ChannelId);
        Assert.Equal(msg.Payload, got.Payload);
    }

    [Fact]
    public void Pack_handles_empty_payload_for_signal_only_messages()
    {
        var msg = new TransportMessage("ping", Array.Empty<byte>());
        var frame = WebRtcDataFraming.Pack(msg);
        Assert.Equal(1 + 4 + 2 + 0, frame.Length);
        var got = WebRtcDataFraming.Parse(frame);
        Assert.Equal("ping", got.ChannelId);
        Assert.Empty(got.Payload);
    }

    [Fact]
    public void Pack_handles_max_size_channelId()
    {
        var idChars = new string('x', WebRtcDataFraming.MaxChannelIdLength);
        var msg = new TransportMessage(idChars, new byte[]{ 1 });
        var frame = WebRtcDataFraming.Pack(msg);
        Assert.Equal(1 + 255 + 2 + 1, frame.Length);
        Assert.Equal((byte)255, frame[0]);

        var got = WebRtcDataFraming.Parse(frame);
        Assert.Equal(idChars, got.ChannelId);
    }

    [Fact]
    public void Pack_rejects_channelId_too_long()
    {
        var idChars = new string('x', WebRtcDataFraming.MaxChannelIdLength + 1);
        var msg = new TransportMessage(idChars, new byte[]{ 1 });
        Assert.Throws<ArgumentException>(() => WebRtcDataFraming.Pack(msg));
    }

    [Fact]
    public void Pack_rejects_payload_too_long()
    {
        var msg = new TransportMessage("x", new byte[WebRtcDataFraming.MaxPayloadLength + 1]);
        Assert.Throws<ArgumentException>(() => WebRtcDataFraming.Pack(msg));
    }

    [Fact]
    public void Pack_rejects_empty_channelId()
    {
        var msg = new TransportMessage("", new byte[]{ 1 });
        Assert.Throws<ArgumentException>(() => WebRtcDataFraming.Pack(msg));
    }

    [Fact]
    public void Parse_rejects_too_short_header()
    {
        Assert.Throws<ArgumentException>(() => WebRtcDataFraming.Parse(new byte[]{ 0x05, 0x00 }));
    }

    [Fact]
    public void Parse_rejects_zero_length_channelId()
    {
        // Frame: idLen=0, payloadLen=0
        Assert.Throws<ArgumentException>(() => WebRtcDataFraming.Parse(new byte[]{ 0, 0, 0 }));
    }

    [Fact]
    public void Parse_rejects_truncated_payload()
    {
        // Header says 100-byte payload but we only ship 5.
        var idBytes = System.Text.Encoding.UTF8.GetBytes("imu");
        var frame = new byte[1 + idBytes.Length + 2 + 5];
        frame[0] = (byte)idBytes.Length;
        Buffer.BlockCopy(idBytes, 0, frame, 1, idBytes.Length);
        frame[1 + idBytes.Length]     = 100; // low
        frame[1 + idBytes.Length + 1] =   0; // high
        // Only 5 payload bytes follow.
        Assert.Throws<ArgumentException>(() => WebRtcDataFraming.Parse(frame));
    }

    [Fact]
    public void Round_trip_handles_unicode_channelId()
    {
        var msg = new TransportMessage("メッセージ", new byte[]{ 0xAA, 0xBB });
        var frame = WebRtcDataFraming.Pack(msg);
        var got = WebRtcDataFraming.Parse(frame);
        Assert.Equal("メッセージ", got.ChannelId);
        Assert.Equal(new byte[]{ 0xAA, 0xBB }, got.Payload);
    }
}
