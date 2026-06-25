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

        /// <summary>Read tracker messages until one carries an OFFER from a remote peer (a peer
        /// offering to connect to us), and return its SDP. Outs the offerer's peer_id and the
        /// offer_id (raw latin1 strings) so the caller can address an answer back. Null on timeout.</summary>
        public string WaitForOffer(out string offererPeerId, out string offerId, int timeoutMs)
        {
            offererPeerId = null;
            offerId = null;
            long deadline = DateTime.UtcNow.Ticks + (long)timeoutMs * 10000;
            while (DateTime.UtcNow.Ticks < deadline)
            {
                string msg = _ws.ReceiveText(timeoutMs);
                if (msg == null)
                    continue;
                // Want an incoming OFFER, not an answer to one of our offers.
                if (msg.IndexOf("\"offer\"") < 0 || msg.IndexOf("\"answer\"") >= 0)
                    continue;
                string sdp = ExtractSdpAfter(msg, "\"offer\"");
                if (sdp == null)
                    continue;
                string pid = ExtractField(msg, "peer_id");
                string oid = ExtractField(msg, "offer_id");
                if (pid == null || oid == null)
                    continue;
                offererPeerId = pid;
                offerId = oid;
                return sdp;
            }
            return null;
        }

        /// <summary>Send our ANSWER back to the peer that offered, addressed by their peer_id +
        /// the offer_id, so the tracker relays it to them. toPeerId/offerId are the raw latin1
        /// strings from <see cref="WaitForOffer"/>.</summary>
        public bool SendAnswer(byte[] roomKey, byte[] ourPeerId, string toPeerId, string offerId, string answerSdp)
        {
            var sb = new StringBuilder();
            sb.Append("{\"action\":\"announce\",\"info_hash\":\"");
            AppendBinary(sb, roomKey);
            sb.Append("\",\"peer_id\":\"");
            AppendBinary(sb, ourPeerId);
            sb.Append("\",\"to_peer_id\":\"");
            AppendStr(sb, toPeerId);
            sb.Append("\",\"answer\":{\"type\":\"answer\",\"sdp\":\"");
            AppendJsonEscaped(sb, answerSdp);
            sb.Append("\"},\"offer_id\":\"");
            AppendStr(sb, offerId);
            sb.Append("\"}");
            return _ws.SendText(sb.ToString());
        }

        // Append a latin1 string (an id from WaitForOffer, one char per byte) escaped.
        static void AppendStr(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
                AppendEscapedChar(sb, s[i]);
        }

        // Find the "sdp":"..." value that appears after the marker (e.g. "answer"/"offer"), unescaped.
        static string ExtractSdpAfter(string msg, string marker)
        {
            int m = msg.IndexOf(marker);
            if (m < 0) return null;
            int s = msg.IndexOf("\"sdp\"", m);
            if (s < 0) return null;
            int colon = msg.IndexOf(':', s);
            if (colon < 0) return null;
            int q = msg.IndexOf('"', colon + 1);
            if (q < 0) return null;
            return Unescape(msg, q + 1);
        }

        // Extract a top-level JSON string field value ("field":"<value>"), unescaped. Null if absent.
        // Note: "peer_id" won't match inside "to_peer_id" (the leading quote differs).
        static string ExtractField(string msg, string field)
        {
            int f = msg.IndexOf("\"" + field + "\"");
            if (f < 0) return null;
            int colon = msg.IndexOf(':', f);
            if (colon < 0) return null;
            int q = msg.IndexOf('"', colon + 1);
            if (q < 0) return null;
            return Unescape(msg, q + 1);
        }

        // Unescape a JSON string from `start` (first char after the opening quote) to the closing quote.
        static string Unescape(string msg, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < msg.Length; i++)
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
            else if (c < 0x20 || c > 0x7E)
            {
                // control chars AND latin1 high bytes (0x7F-0xFF) -> \u00XX. The tracker uses latin1
                // char-per-byte for binary ids, so a peer_id/offer_id with high bytes must escape.
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
