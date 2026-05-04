using nanoFramework.UI;
using SpawnWear.AppContracts;
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
    ///   POST /loadapp          -> Body is a SpawnWear .pe assembly; loaded
    ///                              dynamically and pushed onto the screen stack
    ///   POST /touch            -> Body is 4 bytes [x_u16_LE][y_u16_LE]; injects
    ///                              a tap event into the active screen so the
    ///                              PWA Mirror page becomes a real remote
    ///   OPTIONS *              -> 204 No Content (CORS preflight)
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
        LoadedAppScreen _appLoader;
        ScreenNavigator _navigator;
        int _appLoaderScreenIndex = -1;

        /// <summary>Wire the LoadedAppScreen + navigator so /loadapp can
        /// activate dynamically-loaded apps. Called once at boot from
        /// Program.Main after the navigator is constructed.</summary>
        public void AttachAppLoader(LoadedAppScreen loader, ScreenNavigator nav, int loaderScreenIndex)
        {
            _appLoader = loader;
            _navigator = nav;
            _appLoaderScreenIndex = loaderScreenIndex;
        }

        /// <summary>Convenience: caller doesn't have to know the screen index
        /// just to attach. The matching navigator + index pair are wired
        /// from Program.Main when the LoadedAppScreen is created.</summary>
        public void AttachAppLoader(LoadedAppScreen loader)
        {
            _appLoader = loader;
        }

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

            if (method == "OPTIONS")
            {
                // CORS preflight from SpawnWear.Companion (any origin).
                ServeNoContent(client);
            }
            else if (path == "/" || path == "/index.html")
            {
                ServeHtml(client);
            }
            else if (path == "/screenshot.bin")
            {
                ServeScreenshot(client);
            }
            else if (path == "/loadapp" && method == "POST")
            {
                ServeLoadApp(client, reqHeader, buf, n, headerEnd);
            }
            else if (path == "/touch" && method == "POST")
            {
                ServeTouch(client, reqHeader, buf, n, headerEnd);
            }
            else
            {
                ServeNotFound(client);
            }
        }

        // POST /touch - body is 4 bytes [x_u16_LE][y_u16_LE]. Routes through
        // ScreenNavigator.HandleTap so the active screen sees it the same way
        // it would see a real FT3168 finger-up tap event.
        void ServeTouch(Socket client, string reqHeader, byte[] buf, int n, int headerEnd)
        {
            if (_navigator == null)
            {
                ServeText(client, "503 Service Unavailable\r\n\r\nNavigator not attached");
                return;
            }
            int contentLen = ParseContentLength(reqHeader);
            if (contentLen != 4)
            {
                ServeText(client, "400 Bad Request\r\n\r\nExpected 4 bytes [x_u16_LE][y_u16_LE], got " + contentLen);
                return;
            }
            byte[] body = ReadBody(client, contentLen, buf, n, headerEnd);
            int x = body[0] | (body[1] << 8);
            int y = body[2] | (body[3] << 8);
            // Bound check - panel is _panelWidth x _panelHeight. Anything
            // outside is ignored (a misbehaving client shouldn't crash the
            // navigator).
            if (x < 0 || x >= _panelWidth || y < 0 || y >= _panelHeight)
            {
                ServeText(client, "400 Bad Request\r\n\r\nTap (" + x + "," + y + ") out of " + _panelWidth + "x" + _panelHeight + " panel");
                return;
            }
            try { _navigator.HandleTap(x, y); }
            catch (System.Exception ex)
            {
                ServeText(client, "500 Internal Server Error\r\n\r\nHandleTap EX: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            ServeText(client, "OK tap=(" + x + "," + y + ")");
        }

        void ServeNoContent(Socket client)
        {
            string headers = "HTTP/1.1 204 No Content\r\nContent-Length: 0" + Cors + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
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

        // POST /loadapp - body is a raw nanoFramework .pe assembly that
        // exports a class implementing SpawnWear.AppContracts.ISpawnApp.
        // Finds the implementer, instantiates it, hands it to the firmware's
        // LoadedAppScreen, returns "OK: <app name>". The launcher's APP
        // tile then opens the running app.
        void ServeLoadApp(Socket client, string reqHeader, byte[] firstChunk, int firstLen, int headerEnd)
        {
            if (_appLoader == null)
            {
                ServeText(client, "503 Service Unavailable\r\n\r\nApp loader not attached");
                return;
            }
            int contentLength = ParseContentLength(reqHeader);
            if (contentLength <= 0)
            {
                ServeText(client, "400 Bad Request\r\n\r\nMissing Content-Length");
                return;
            }
            byte[] payload = ReadBody(client, contentLength, firstChunk, firstLen, headerEnd);

            string result;
            try
            {
                var asm = System.Reflection.Assembly.Load(payload);
                if (asm == null) { result = "ERROR: Assembly.Load returned null"; }
                else
                {
                    System.Type[] types;
                    try { types = asm.GetTypes(); } catch { types = new System.Type[0]; }
                    System.Type appType = null;
                    foreach (var t in types)
                    {
                        if (t == null || !t.IsClass || t.IsAbstract) continue;
                        var ifaces = t.GetInterfaces();
                        foreach (var i in ifaces)
                        {
                            if (i == typeof(ISpawnApp)) { appType = t; break; }
                        }
                        if (appType != null) break;
                    }
                    if (appType == null) { result = "ERROR: no ISpawnApp implementer in assembly"; }
                    else
                    {
                        // nanoFramework's mscorlib doesn't ship Activator;
                        // use the parameterless constructor directly.
                        var ctor = appType.GetConstructor(new System.Type[0]);
                        if (ctor == null) { result = "ERROR: app type has no parameterless constructor"; }
                        else
                        {
                            var instance = (ISpawnApp)ctor.Invoke(null);
                            if (_appLoader.SetApp(instance))
                            {
                                result = "OK: " + instance.Name;
                                Debug.WriteLine("[LoadApp] activated: " + instance.Name);
                            }
                            else
                            {
                                result = "ERROR: app refused activation";
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                result = "EXCEPTION: " + ex.GetType().Name + ": " + ex.Message;
                Debug.WriteLine("[LoadApp] " + result);
            }

            string body = result + "\r\n";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + bodyBytes.Length + Cors + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(bodyBytes, 0, bodyBytes.Length, SocketFlags.None);
        }

        // Permissive CORS so SpawnWear.Companion (Blazor WASM running on a
        // different origin / port) can fetch /screenshot.bin and POST /loadapp
        // without hitting browser same-origin block. Watch is a development /
        // LAN device — there's no auth boundary to defend.
        const string Cors = "\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type";

        byte[] ReadBody(Socket client, int contentLength, byte[] firstChunk, int firstLen, int headerEnd)
        {
            byte[] payload = new byte[contentLength];
            int already = (headerEnd > 0) ? (firstLen - headerEnd) : 0;
            if (already > 0) System.Array.Copy(firstChunk, headerEnd, payload, 0, already);
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
            return payload;
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
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + Cors + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(body, 0, body.Length, SocketFlags.None);
        }

        void ServeHtml(Socket client)
        {
            string body = "<!doctype html><html><head><title>SpawnWear</title>" +
                "<meta name='viewport' content='width=device-width, initial-scale=1'>" +
                "<style>body{background:#1a1a22;color:#eee;font-family:system-ui,sans-serif;margin:0;padding:16px;display:flex;flex-direction:column;align-items:center;gap:16px}canvas{border:1px solid #555;image-rendering:pixelated;max-width:90vw;height:auto}button{padding:10px 20px;font-size:15px;background:#2a2a36;color:#eee;border:1px solid #555;border-radius:6px;cursor:pointer}button:hover{background:#3a3a48}#drop{border:2px dashed #555;border-radius:8px;padding:24px;width:80%;max-width:380px;text-align:center;color:#aaa;cursor:pointer}#drop.over{border-color:#6cf;color:#6cf;background:#222}#out{font-family:monospace;font-size:13px;background:#000;padding:8px 12px;border-radius:4px;width:80%;max-width:400px;min-height:1.5em}</style>" +
                "</head><body>" +
                "<h2>SpawnWear</h2>" +
                "<button id='r'>Refresh screen</button>" +
                "<canvas id='c' width='410' height='502'></canvas>" +
                "<div id='drop'>Drop a SpawnWear app .pe here<br><small>or <a href='#' id='pick' style='color:#6cf'>browse</a></small><input type='file' id='f' accept='.pe' style='display:none'></div>" +
                "<div id='out'>idle</div>" +
                "<script>" +
                "const out=document.getElementById('out');const drop=document.getElementById('drop');const f=document.getElementById('f');" +
                "async function fetchShot(){out.textContent='fetching...';const r=await fetch('/screenshot.bin?t='+Date.now());const ab=await r.arrayBuffer();const b=new Uint8Array(ab);let nl=0;while(b[nl]!=10)nl++;const h=new TextDecoder().decode(b.slice(0,nl));const w=+h.match(/w=(\\d+)/)[1],ht=+h.match(/h=(\\d+)/)[1];const px=b.slice(nl+1);const c=document.getElementById('c');c.width=w;c.height=ht;const cx=c.getContext('2d'),img=cx.createImageData(w,ht);for(let i=0;i<w*ht;i++){const v=(px[i*2]<<8)|px[i*2+1];const r5=(v>>11)&0x1F,g6=(v>>5)&0x3F,b5=v&0x1F;img.data[i*4]=(r5<<3)|(r5>>2);img.data[i*4+1]=(g6<<2)|(g6>>4);img.data[i*4+2]=(b5<<3)|(b5>>2);img.data[i*4+3]=255}cx.putImageData(img,0,0);out.textContent='screen '+w+'x'+ht}" +
                "async function uploadApp(file){out.textContent='uploading '+file.name+' ('+file.size+' bytes)...';const buf=await file.arrayBuffer();const r=await fetch('/loadapp',{method:'POST',body:buf});const t=await r.text();out.textContent=t.trim();setTimeout(fetchShot,500)}" +
                "document.getElementById('r').onclick=fetchShot;" +
                "document.getElementById('pick').onclick=e=>{e.preventDefault();f.click()};" +
                "f.onchange=e=>{if(e.target.files[0])uploadApp(e.target.files[0])};" +
                "drop.ondragover=e=>{e.preventDefault();drop.classList.add('over')};" +
                "drop.ondragleave=()=>drop.classList.remove('over');" +
                "drop.ondrop=e=>{e.preventDefault();drop.classList.remove('over');if(e.dataTransfer.files[0])uploadApp(e.dataTransfer.files[0])};" +
                "fetchShot();" +
                "</script></body></html>";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + bodyBytes.Length + Cors + "\r\nConnection: close\r\n\r\n";
            client.Send(Encoding.UTF8.GetBytes(headers), 0, headers.Length, SocketFlags.None);
            client.Send(bodyBytes, 0, bodyBytes.Length, SocketFlags.None);
        }

        void ServeScreenshot(Socket client)
        {
            // Header: ASCII "w=W h=H\n", then panel*panel*2 raw RGB565 BE bytes.
            int totalPixels = _panelWidth * _panelHeight;
            byte[] hdr = Encoding.UTF8.GetBytes("w=" + _panelWidth + " h=" + _panelHeight + "\n");
            int contentLen = hdr.Length + totalPixels * 2;
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + contentLen + Cors + "\r\nConnection: close\r\nCache-Control: no-cache\r\n\r\n";
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
            string headers = "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: " + bodyBytes.Length + Cors + "\r\nConnection: close\r\n\r\n";
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
