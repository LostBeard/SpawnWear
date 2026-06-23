using System;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;

namespace SpawnWear.Services
{
    /// <summary>
    /// Minimal RFC 6455 WebSocket CLIENT for the watch (Phase 7b). nanoFramework ships no
    /// WebSocket-client package for ESP32, so we roll the thin slice we need on top of the
    /// socket + TLS primitives the firmware already has: TCP <see cref="Socket"/> + a TLS
    /// <see cref="SslStream"/> (mbedTLS), the HTTP Upgrade handshake, and masked text-frame
    /// send / unmasked-frame receive. Just enough to speak the bittorrent-tracker JSON
    /// signaling protocol to wss://hub.spawndev.com:44365/announce.
    ///
    /// Text-only, single-threaded, blocking. Not a general-purpose implementation.
    /// </summary>
    public sealed class SwWebSocket : IDisposable
    {
        Socket _socket;
        SslStream _tls;
        readonly Random _rng = new Random();
        bool _open;

        public bool IsOpen => _open;

        /// <summary>Connect to wss://host:port/path and complete the WebSocket upgrade.</summary>
        public bool Connect(string host, int port, string path, string origin)
        {
            try
            {
                // 1. TCP.
                var hostEntry = Dns.GetHostEntry(host);
                var ip = hostEntry.AddressList[0];
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.Connect(new IPEndPoint(ip, port));

                // 2. TLS (relax cert validation - the hub may use a non-public cert; the
                //    WebRTC layer carries the real security, the tracker is just a meeting point).
                _tls = new SslStream(_socket);
                _tls.SslVerification = SslVerification.NoVerification;
                _tls.AuthenticateAsClient(host, SslProtocols.Tls12);

                // 3. HTTP Upgrade handshake.
                var keyBytes = new byte[16];
                _rng.NextBytes(keyBytes);
                string key = Convert.ToBase64String(keyBytes);
                string req =
                    "GET " + path + " HTTP/1.1\r\n" +
                    "Host: " + host + ":" + port + "\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: " + key + "\r\n" +
                    "Sec-WebSocket-Version: 13\r\n" +
                    "Origin: " + origin + "\r\n\r\n";
                var reqBytes = Encoding.UTF8.GetBytes(req);
                _tls.Write(reqBytes, 0, reqBytes.Length);

                // 4. Read the response headers; require "101".
                string resp = ReadHttpResponseHead();
                if (resp == null || resp.IndexOf(" 101 ") < 0)
                    return false;

                _open = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Send a UTF-8 text message as a single masked WebSocket frame.</summary>
        public bool SendText(string text)
        {
            if (!_open)
                return false;
            try
            {
                var payload = Encoding.UTF8.GetBytes(text);
                int len = payload.Length;

                // header: FIN + opcode 0x1 (text); mask bit set; length; 4-byte mask.
                byte[] header;
                if (len <= 125)
                {
                    header = new byte[2];
                    header[1] = (byte)(0x80 | len);
                }
                else if (len <= 0xFFFF)
                {
                    header = new byte[4];
                    header[1] = 0x80 | 126;
                    header[2] = (byte)(len >> 8);
                    header[3] = (byte)(len & 0xFF);
                }
                else
                {
                    header = new byte[10];
                    header[1] = 0x80 | 127;
                    for (int i = 0; i < 8; i++)
                        header[9 - i] = (byte)((len >> (8 * i)) & 0xFF);
                }
                header[0] = 0x81; // FIN + text

                var mask = new byte[4];
                _rng.NextBytes(mask);

                var frame = new byte[header.Length + 4 + len];
                Array.Copy(header, 0, frame, 0, header.Length);
                Array.Copy(mask, 0, frame, header.Length, 4);
                int body = header.Length + 4;
                for (int i = 0; i < len; i++)
                    frame[body + i] = (byte)(payload[i] ^ mask[i & 3]);

                _tls.Write(frame, 0, frame.Length);
                return true;
            }
            catch
            {
                _open = false;
                return false;
            }
        }

        /// <summary>Block until one text message arrives (handling ping/pong + fragmentation
        /// minimally) and return it, or null on timeout/close. Server frames are not masked.</summary>
        public string ReceiveText(int timeoutMs)
        {
            if (!_open)
                return null;
            try
            {
                _socket.ReceiveTimeout = timeoutMs;
                while (true)
                {
                    int b0 = ReadByte();
                    if (b0 < 0)
                        return null;
                    int opcode = b0 & 0x0F;

                    int b1 = ReadByte();
                    if (b1 < 0)
                        return null;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;
                    if (len == 126)
                        len = (ReadByte() << 8) | ReadByte();
                    else if (len == 127)
                    {
                        len = 0;
                        for (int i = 0; i < 8; i++)
                            len = (len << 8) | (uint)ReadByte();
                    }

                    byte[] maskKey = null;
                    if (masked)
                    {
                        maskKey = new byte[4];
                        ReadFull(maskKey, (int)4);
                    }

                    var payload = new byte[(int)len];
                    ReadFull(payload, (int)len);
                    if (masked)
                        for (int i = 0; i < len; i++)
                            payload[i] = (byte)(payload[i] ^ maskKey[i & 3]);

                    if (opcode == 0x8) // close
                    {
                        _open = false;
                        return null;
                    }
                    if (opcode == 0x9) // ping -> pong (opcode 0xA), masked
                    {
                        SendControl(0x8A, payload);
                        continue;
                    }
                    if (opcode == 0xA) // pong
                        continue;
                    // 0x1 text (or 0x0 continuation / 0x2 binary) - return as UTF-8 text.
                    return new string(Encoding.UTF8.GetChars(payload));
                }
            }
            catch
            {
                return null;
            }
        }

        void SendControl(byte opcodeByte, byte[] payload)
        {
            try
            {
                int len = payload == null ? 0 : payload.Length;
                if (len > 125)
                    len = 125;
                var mask = new byte[4];
                _rng.NextBytes(mask);
                var frame = new byte[2 + 4 + len];
                frame[0] = opcodeByte;
                frame[1] = (byte)(0x80 | len);
                Array.Copy(mask, 0, frame, 2, 4);
                for (int i = 0; i < len; i++)
                    frame[6 + i] = (byte)(payload[i] ^ mask[i & 3]);
                _tls.Write(frame, 0, frame.Length);
            }
            catch { }
        }

        string ReadHttpResponseHead()
        {
            // Read until "\r\n\r\n".
            var sb = new StringBuilder();
            int matched = 0;
            _socket.ReceiveTimeout = 10000;
            for (int i = 0; i < 4096; i++)
            {
                int c = ReadByte();
                if (c < 0)
                    return null;
                sb.Append((char)c);
                if ((matched == 0 && c == '\r') || (matched == 1 && c == '\n') ||
                    (matched == 2 && c == '\r') || (matched == 3 && c == '\n'))
                    matched++;
                else
                    matched = (c == '\r') ? 1 : 0;
                if (matched == 4)
                    break;
            }
            return sb.ToString();
        }

        readonly byte[] _one = new byte[1];
        int ReadByte()
        {
            int n = _tls.Read(_one, 0, 1);
            return n <= 0 ? -1 : _one[0];
        }

        void ReadFull(byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _tls.Read(buf, got, count - got);
                if (n <= 0)
                    throw new Exception("ws read closed");
                got += n;
            }
        }

        public void Dispose()
        {
            _open = false;
            try { _tls?.Dispose(); } catch { }
            try { _socket?.Close(); } catch { }
            _tls = null;
            _socket = null;
        }
    }
}
