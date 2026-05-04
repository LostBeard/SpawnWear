namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// Wire-format helpers for transporting <see cref="TransportMessage"/>
/// over a WebRTC data channel. The data channel itself frames each
/// <c>send()</c> call as one binary message, but a single channel
/// carries multiple SpawnWear logical channels (battery, imu, debug
/// log, etc.) — so we add a small inline header that identifies which
/// SpawnWear channel each binary frame belongs to.
///
/// Layout per binary message on the data channel:
///
/// <code>
/// offset 0    : channelIdLen      [u8]            (1-255)
/// offset 1    : channelId         [UTF-8 bytes, channelIdLen long]
/// offset 1+L  : payloadLen        [u16 little-endian]
/// offset 3+L  : payload           [payloadLen bytes]
/// </code>
///
/// Where <c>L</c> is <c>channelIdLen</c>. Total binary size:
/// <c>3 + L + payloadLen</c>. The 16-bit payload length cap (65535
/// bytes) is well above any single notify SpawnWear sends today;
/// when we ship larger payloads (audio chunks, screenshot frames),
/// the consumer chunks at the SpawnWear-channel layer rather than
/// expanding this header.
///
/// Crypto-free; the data channel itself runs over DTLS-SRTP so the
/// frame is encrypted on the wire. Identity verification happens
/// once at data-channel-open via <see cref="WebRtcChallenge"/>; this
/// framing carries the ongoing TransportMessage stream after that.
/// </summary>
public static class WebRtcDataFraming
{
    /// <summary>Maximum length of the channel-id string in bytes
    /// (UTF-8 encoded). Constrained by the 1-byte length prefix.</summary>
    public const int MaxChannelIdLength = 255;

    /// <summary>Maximum payload length in bytes per frame. 65535 = u16 cap.</summary>
    public const int MaxPayloadLength = 65535;

    /// <summary>Pack a <see cref="TransportMessage"/> for sending over
    /// a WebRTC data channel.</summary>
    public static byte[] Pack(TransportMessage message)
    {
        if (string.IsNullOrEmpty(message.ChannelId))
            throw new ArgumentException("ChannelId is empty.", nameof(message));
        if (message.Payload is null)
            throw new ArgumentException("Payload is null.", nameof(message));

        var idBytes = System.Text.Encoding.UTF8.GetBytes(message.ChannelId);
        if (idBytes.Length > MaxChannelIdLength)
            throw new ArgumentException(
                $"ChannelId UTF-8 byte length {idBytes.Length} exceeds maximum {MaxChannelIdLength}.",
                nameof(message));
        if (message.Payload.Length > MaxPayloadLength)
            throw new ArgumentException(
                $"Payload length {message.Payload.Length} exceeds maximum {MaxPayloadLength}.",
                nameof(message));

        var buf = new byte[3 + idBytes.Length + message.Payload.Length];
        int o = 0;
        buf[o++] = (byte)idBytes.Length;
        Buffer.BlockCopy(idBytes, 0, buf, o, idBytes.Length);
        o += idBytes.Length;
        buf[o++] = (byte)(message.Payload.Length & 0xFF);
        buf[o++] = (byte)((message.Payload.Length >> 8) & 0xFF);
        Buffer.BlockCopy(message.Payload, 0, buf, o, message.Payload.Length);
        return buf;
    }

    /// <summary>Inverse of <see cref="Pack"/>. Throws on truncated or
    /// malformed frames so a single bad message tears down the
    /// connection rather than silently corrupting the stream.</summary>
    public static TransportMessage Parse(byte[] frame)
    {
        if (frame is null) throw new ArgumentNullException(nameof(frame));
        if (frame.Length < 3) throw new ArgumentException("Frame is shorter than the minimum 3-byte header.", nameof(frame));

        int o = 0;
        int idLen = frame[o++];
        if (idLen == 0) throw new ArgumentException("Frame's channelId length is zero.", nameof(frame));
        if (frame.Length < 1 + idLen + 2)
            throw new ArgumentException("Frame is truncated before payload length.", nameof(frame));

        var idBytes = new byte[idLen];
        Buffer.BlockCopy(frame, o, idBytes, 0, idLen);
        o += idLen;

        int payloadLen = frame[o] | (frame[o + 1] << 8);
        o += 2;
        if (frame.Length < o + payloadLen)
            throw new ArgumentException(
                $"Frame is truncated; header says {payloadLen}-byte payload but only {frame.Length - o} bytes remain.",
                nameof(frame));

        var payload = new byte[payloadLen];
        Buffer.BlockCopy(frame, o, payload, 0, payloadLen);

        var channelId = System.Text.Encoding.UTF8.GetString(idBytes);
        return new TransportMessage(channelId, payload);
    }
}
