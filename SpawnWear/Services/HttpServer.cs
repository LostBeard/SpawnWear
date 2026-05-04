using nanoFramework.UI;
using SpawnWear.UI;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SpawnWear.Services
{
    /// <summary>
    /// Minimal raw-socket HTTP listener for SpawnWear development access. Replaces
    /// the BOOT-button-base64-over-Debug.WriteLine screenshot path with a real
    /// HTTP endpoint anyone on the watch's WiFi network can hit.
    ///
    /// Routes:
    ///   GET /                  -> HTML page with embedded JS that fetches
    ///                              /screenshot.bin and renders it to a canvas
    ///   GET /screenshot.bin    -> Raw RGB565 big-endian bytes preceded by
    ///                              a small ASCII header line "w=W h=H\n" so
    ///                              the JS knows how to slice the canvas
    ///   anything else          -> 404
    ///
    /// Single-threaded: handles one connection at a time on a dedicated thread.
    /// Phase 7 (WebRTC) will replace this with the real network stack but the
    /// HTTP fallback stays useful for diagnostics.
    /// </summary>
    public class HttpServer
    {
        readonly int _port;
        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;
        Socket _listener;
        Thread _thread;
        bool _running;

        public HttpServer(Bitmap fb, int panelWidth, int panelHeight, int port = 80)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _port = port;
        }

        public void Start()
        {
            if (_running) return;
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // SO_REUSEADDR so the socket can rebind after a CLR-only restart
            // (the previous run's listener may still be in TIME_WAIT).
            try { _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); }
            catch { /* not all builds support ReuseAddress; bind may still succeed */ }
            try
            {
                _listener.Bind(new IPEndPoint(IPAddress.Any, _port));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Http] bind failed on " + _port + ": " + ex.Message + " - power cycle the watch to clear the stale socket");
                throw;
            }
            _listener.Listen(2);
            _running = true;
            _thread = new Thread(AcceptLoop);
            _thread.Start();
            Debug.WriteLine("[Http] listening on port " + _port);
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Close(); } catch { }
        }

        void AcceptLoop()
        {
            while (_running)
            {
                Socket client = null;
                try
                {
                    client = _listener.Accept();
                    HandleClient(client);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Http] accept EX " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    try { client?.Close(); } catch { }
                }
            }
        }

        void HandleClient(Socket client)
        {
            byte[] buf = new byte[2048];
            int n = client.Receive(buf, 0, buf.Length, SocketFlags.None);
            if (n <= 0) return;
            // Find end-of-headers (\r\n\r\n) so we can split header from body.
            int headerEnd = FindHeaderEnd(buf, n);
            int reqHeaderLen = headerEnd > 0 ? headerEnd : n;
            string reqHeader = Encoding.UTF8.GetString(buf, 0, reqHeaderLen);
            string firstLine = reqHeader.Split('\n')[0].Trim();
            string method = "GET";
            string path = "/";
            int firstSpace = firstLine.IndexOf(' ');
            if (firstSpace > 0)
            {
                method = firstLine.Substring(0, firstSpace);
                int sp = firstLine.IndexOf(' ', firstSpace + 1);
                if (sp > firstSpace) path = firstLine.Substring(firstSpace + 1, sp - firstSpace - 1);
            }
            // Strip the query string (?t=cache-buster, etc.) before route match.
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            Debug.WriteLine("[Http] " + firstLine + " -> path=" + path);

            if (path == "/" || path == "/index.html")
            {
                ServeHtml(client);
            }
            else if (path == "/screenshot.bin")
            {
                ServeScreenshot(client);
            }
            else if (path == "/loadpe" && method == "POST")
            {
                ServeLoadPe(client, reqHeader, buf, n, headerEnd);
            }
            else
            {
                ServeNotFound(client);
            }
        }

        static int FindHeaderEnd(byte[] buf, int n)
        {
            for (int i = 0; i < n - 3; i++)
            {
                if (buf[i] == 0x0D && buf[i+1] == 0x0A && buf[i+2] == 0x0D && buf[i+3] == 0x0A)
                    return i + 4;
            }
            return -1;
        }

        // POST /loadpe - body is a raw nanoFramework .pe assembly. Loads it via
        // Assembly.Load(byte[]), finds HelloWorldApp.HelloWorldPayload, invokes
        // Greet() via reflection, returns the result. Phase 8 SD-card-loadable
        // apps verification harness; proves the dynamic-load + invoke path
        // works on real silicon.
        void ServeLoadPe(Socket client, string reqHeader, byte[] firstChunk, int firstLen, int headerEnd)
        {
            int contentLength = ParseContentLength(reqHeader);
            if (contentLength <= 0)
            {
                ServeText(client, "400 Bad Request\r\n\r\nMissing or invalid Content-Length");
                return;
            }
            Debug.WriteLine("[LoadPe] receiving " + contentLength + " bytes");

            // Body bytes already in firstChunk after headerEnd.
            byte[] payload = new byte[contentLength];
            int already = (headerEnd > 0) ? (firstLen - headerEnd) : 0;
            if (already > 0) System.Array.Copy(firstChunk, headerEnd, payload, 0, already);

            // Read remaining body.
            int got = already;
            byte[] readBuf = new byte[1024];
            while (got < contentLength)
            {
                int r = client.Receive(readBuf, 0, readBuf.Length, SocketFlags.None);
                if (r <= 0) break;
                int copyN = (got + r > contentLength) ? (contentLength - got) : r;
                System.Array.Copy(readBuf, 0, payload, got, copyN);
                got += copyN;
            }
            Debug.WriteLine("[LoadPe] received " + got + "/" + contentLength + " bytes");

            string result;
            try
            {
                var asm = System.Reflection.Assembly.Load(payload);
                Debug.WriteLine("[LoadPe] Assembly.Load returned: " + (asm != null ? asm.FullName : "null"));
                if (asm == null) { result = "ERROR: Assembly.Load returned null"; }
                else
                {
                    var t = asm.GetType("HelloWorldApp.HelloWorldPayload");
                    Debug.WriteLine("[LoadPe] GetType returned: " + (t != null ? t.FullName : "null"));
                    if (t == null) { result = "ERROR: type HelloWorldApp.HelloWorldPayload not found"; }
                    else
                    {
                        var m = t.GetMethod("Greet");
                        Debug.WriteLine("[LoadPe] GetMethod Greet returned: " + (m != null ? m.Name : "null"));
                        if (m == null) { result = "ERROR: method Greet not found"; }
                        else
                        {
                            var ret = m.Invoke(null, null);
                            result = "OK: " + (ret != null ? ret.ToString() : "null");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                result = "EXCEPTION: " + ex.GetType().Name + ": " + ex.Message;
                Debug.WriteLine("[LoadPe] " + result);
            }

            string body = result + "\r\n";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(bodyBytes, 0, bodyBytes.Length, SocketFlags.None);
        }

        static int ParseContentLength(string reqHeader)
        {
            // Case-insensitive line scan.
            string[] lines = reqHeader.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length < 16) continue;
                string lower = line.ToLower();
                if (lower.StartsWith("content-length:"))
                {
                    string val = line.Substring(15).Trim();
                    int n;
                    return int.TryParse(val, out n) ? n : -1;
                }
            }
            return -1;
        }

        void ServeText(Socket client, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(body, 0, body.Length, SocketFlags.None);
        }

        void ServeHtml(Socket client)
        {
            string body = "<!doctype html>\r\n" +
                "<html><head><title>SpawnWear</title>\r\n" +
                "<meta name='viewport' content='width=device-width, initial-scale=1'>\r\n" +
                "<style>body{background:#222;color:#eee;font-family:sans-serif;text-align:center;margin:0;padding:16px} canvas{border:1px solid #555;image-rendering:pixelated;max-width:90vw;height:auto}</style>\r\n" +
                "</head><body>\r\n" +
                "<h2>SpawnWear screen</h2>\r\n" +
                "<button id='r' style='padding:12px 24px;font-size:16px;margin-bottom:12px'>Refresh</button>\r\n" +
                "<div><canvas id='c' width='410' height='502'></canvas></div>\r\n" +
                "<p id='s'>idle</p>\r\n" +
                "<script>\r\n" +
                "async function fetchShot(){\r\n" +
                " const s=document.getElementById('s');\r\n" +
                " s.textContent='fetching...';\r\n" +
                " const r=await fetch('/screenshot.bin?t='+Date.now());\r\n" +
                " const ab=await r.arrayBuffer();\r\n" +
                " const bytes=new Uint8Array(ab);\r\n" +
                " let nl=0; while(bytes[nl]!=10) nl++;\r\n" +
                " const hdr=new TextDecoder().decode(bytes.slice(0,nl));\r\n" +
                " const wm=hdr.match(/w=(\\d+)/), hm=hdr.match(/h=(\\d+)/);\r\n" +
                " const w=+wm[1], h=+hm[1];\r\n" +
                " const px=bytes.slice(nl+1);\r\n" +
                " const c=document.getElementById('c'); c.width=w; c.height=h;\r\n" +
                " const ctx=c.getContext('2d'); const img=ctx.createImageData(w,h);\r\n" +
                " for(let i=0;i<w*h;i++){\r\n" +
                "   const hi=px[i*2], lo=px[i*2+1]; const v=(hi<<8)|lo;\r\n" +
                "   const r5=(v>>11)&0x1F, g6=(v>>5)&0x3F, b5=v&0x1F;\r\n" +
                "   img.data[i*4+0]=(r5<<3)|(r5>>2);\r\n" +
                "   img.data[i*4+1]=(g6<<2)|(g6>>4);\r\n" +
                "   img.data[i*4+2]=(b5<<3)|(b5>>2);\r\n" +
                "   img.data[i*4+3]=255;\r\n" +
                " }\r\n" +
                " ctx.putImageData(img,0,0);\r\n" +
                " s.textContent='ok '+w+'x'+h+' '+px.length+' bytes';\r\n" +
                "}\r\n" +
                "document.getElementById('r').onclick=fetchShot;\r\n" +
                "fetchShot();\r\n" +
                "</script></body></html>\r\n";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(bodyBytes, 0, bodyBytes.Length, SocketFlags.None);
        }

        void ServeScreenshot(Socket client)
        {
            // Header: ASCII "w=W h=H\n", then panel*panel*2 raw RGB565 BE bytes.
            int totalPixels = _panelWidth * _panelHeight;
            byte[] hdr = Encoding.UTF8.GetBytes("w=" + _panelWidth + " h=" + _panelHeight + "\n");
            int contentLen = hdr.Length + totalPixels * 2;
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + contentLen + "\r\nConnection: close\r\nCache-Control: no-cache\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(hdr, 0, hdr.Length, SocketFlags.None);

            // Stream pixels in 1-row chunks (820 bytes per row).
            byte[] rowBuf = new byte[_panelWidth * 2];
            for (int y = 0; y < _panelHeight; y++)
            {
                for (int x = 0; x < _panelWidth; x++)
                {
                    var c = _fb.GetPixel(x, y);
                    ushort rgb565 = ToRgb565(c);
                    rowBuf[x * 2] = (byte)(rgb565 >> 8);
                    rowBuf[x * 2 + 1] = (byte)(rgb565 & 0xFF);
                }
                client.Send(rowBuf, 0, rowBuf.Length, SocketFlags.None);
            }
        }

        void ServeNotFound(Socket client)
        {
            string body = "404 not found\r\n";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers = "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(bodyBytes, 0, bodyBytes.Length, SocketFlags.None);
        }

        static ushort ToRgb565(System.Drawing.Color c)
        {
            int r = (c.R >> 3) & 0x1F;
            int g = (c.G >> 2) & 0x3F;
            int b = (c.B >> 3) & 0x1F;
            return (ushort)((r << 11) | (g << 5) | b);
        }
    }
}
