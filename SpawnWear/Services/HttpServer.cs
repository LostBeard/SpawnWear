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
            byte[] buf = new byte[1024];
            int n = client.Receive(buf, 0, buf.Length, SocketFlags.None);
            if (n <= 0) return;
            string req = Encoding.UTF8.GetString(buf, 0, n);
            string firstLine = req.Split('\n')[0].Trim();
            string path = "/";
            if (firstLine.StartsWith("GET "))
            {
                int sp = firstLine.IndexOf(' ', 4);
                if (sp > 4) path = firstLine.Substring(4, sp - 4);
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
            else
            {
                ServeNotFound(client);
            }
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
