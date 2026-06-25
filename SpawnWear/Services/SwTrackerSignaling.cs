using System;
using System.Text;

namespace SpawnWear.Services
{
    /// <summary>
    /// Watch-side WebTorrent-tracker signaling client (Phase 7b milestone 3). Speaks the
    /// bittorrent-tracker JSON-over-WebSocket protocol to wss://hub.spawndev.com:44365/announce
    /// (matching SpawnDev.RTC's <c>TrackerSignalingClient</c>) using our hand-rolled
    /// <see cref="SwWebSocket"/>. The watch is the OFFERER: it announces its libpeer offer SDP
    /// into a room (info_hash = the 20-byte RoomKey, latin1) and waits for a peer's answer.
    ///
    /// <para>Wire strings (info_hash / peer_id / offer_id) are latin1 binary strings - one char
    /// per byte. This client assumes those byte values are printable ASCII (32-126) so the JSON
    /// needs no \u escaping; full latin1 (bytes 128-255) escaping is a production follow-up.</para>
    /// </summary>
    public sealed class SwTrackerSignaling : IDisposable
    {
        readonly SwWebSocket _ws = new SwWebSocket();

        public bool Connect()
            => _ws.Connect("hub.spawndev.com", 44365, "/announce", "https://hub.spawndev.com");

        /// <summary>Announce our offer into the room. roomKey/peerId/offerId are raw bytes
        /// (assumed printable-ASCII for the test); offerSdp is the libpeer offer SDP.</summary>
        public bool AnnounceOffer(byte[] roomKey, byte[] peerId, byte[] offerId, string offerSdp)
        {
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"announce\",\"info_hash\":\"");
            AppendBinary(sb, roomKey);
            sb.Append("\",\"peer_id\":\"");
            AppendBinary(sb, peerId);
            sb.Append("\",\"uploaded\":0,\"downloaded\":0,\"left\":1,\"event\":\"started\",\"numwant\":1,\"offers\":[{\"offer\":{\"type\":\"offer\",\"sdp\":\"");
            AppendJsonEscaped(sb, offerSdp);
            sb.Append("\"},\"offer_id\":\"");
            AppendBinary(sb, offerId);
            sb.Append("\"}]}");
            return _ws.SendText(sb.ToString());
        }

        /// <summary>Read tracker messages until one carries an "answer" for our offer_id, and
        /// return its SDP. Returns null on timeout. offerId is the same bytes passed to
        /// <see cref="AnnounceOffer"/>.</summary>
        public string WaitForAnswer(byte[] offerId, int timeoutMs)
        {
            string offerIdStr = BinaryToString(offerId);
            long deadline = DateTime.UtcNow.Ticks + (long)timeoutMs * 10000;
            while (DateTime.UtcNow.Ticks < deadline)
            {
                string msg = _ws.ReceiveText(timeoutMs);
                if (msg == null)
                    continue;
                // We only care about messages that carry an answer for our offer.
                if (msg.IndexOf("\"answer\"") < 0)
                    continue;
                // Confirm it's for OUR offer_id (best-effort substring match).
                string offerIdJson = JsonEscape(offerIdStr);
                if (msg.IndexOf(offerIdJson) < 0 && msg.IndexOf(offerIdStr) < 0)
                    continue;
                string sdp = ExtractSdpAfter(msg, "\"answer\"");
                if (sdp != null)
                    return sdp;
            }
            return null;
        }

        // Find the "sdp":"..." value that appears after the marker (e.g. "answer"), unescaped.
        static string ExtractSdpAfter(string msg, string marker)
        {
            int m = msg.IndexOf(marker);
            if (m < 0)
                return null;
            int s = msg.IndexOf("\"sdp\"", m);
            if (s < 0)
                return null;
            int colon = msg.IndexOf(':', s);
            if (colon < 0)
                return null;
            int q = msg.IndexOf('"', colon + 1);
            if (q < 0)
                return null;
            var sb = new StringBuilder();
            for (int i = q + 1; i < msg.Length; i++)
            {
                char c = msg[i];
                if (c == '\\' && i + 1 < msg.Length)
                {
                    char n = msg[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == '"') sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else if (n == '/') sb.Append('/');
                    else if (n == 'u' && i + 4 < msg.Length)
                    {
                        int code = HexVal(msg[i + 1]) * 4096 + HexVal(msg[i + 2]) * 256 + HexVal(msg[i + 3]) * 16 + HexVal(msg[i + 4]);
                        sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append(n);
                }
                else if (c == '"')
                    break;
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }

        static void AppendBinary(StringBuilder sb, byte[] bytes)
        {
            // latin1 binary string, one char per byte (ASCII-only assumption for the test).
            for (int i = 0; i < bytes.Length; i++)
                AppendEscapedChar(sb, (char)bytes[i]);
        }

        static string BinaryToString(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                chars[i] = (char)bytes[i];
            return new string(chars);
        }

        static void AppendJsonEscaped(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
                AppendEscapedChar(sb, s[i]);
        }

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder();
            AppendJsonEscaped(sb, s);
            return sb.ToString();
        }

        static void AppendEscapedChar(StringBuilder sb, char c)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20)
            {
                sb.Append("\\u00");
                sb.Append(HexChar((c >> 4) & 0xF));
                sb.Append(HexChar(c & 0xF));
            }
            else
                sb.Append(c);
        }

        static char HexChar(int v) => (char)(v < 10 ? '0' + v : 'a' + (v - 10));

        public void Dispose()
        {
            try { _ws.Dispose(); } catch { }
        }
    }
}
