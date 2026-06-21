using System.Net.Http;

namespace SpawnWear.Bridge;

/// <summary>
/// HTTP-side Bridge surface. The watch's <c>HttpServer</c> exposes a
/// handful of WiFi-only endpoints that BLE alone can't carry (large
/// binary payloads). This class wraps those endpoints with typed
/// methods so consumers don't have to juggle URL fragments + cache
/// busters + payload framing themselves.
///
/// The watch URL is supplied per-call rather than stored, because
/// (a) consumers may want to mirror multiple watches in the same
/// session, and (b) the URL can change as the watch reconnects to a
/// different network. Get the latest from
/// <see cref="BridgeClient.WifiStatusChanged"/>.
/// </summary>
public class WatchHttp
{
    readonly HttpClient _http;

    public WatchHttp(HttpClient http) { _http = http; }

    /// <summary>Width-in-pixels + height-in-pixels + RGBA8 pixel
    /// buffer (top-left origin, 4 bytes per pixel). Already decoded
    /// from the watch's RGB565 BE wire format; ready to push into a
    /// canvas <c>ImageData</c>.</summary>
    public readonly record struct Screenshot(int Width, int Height, byte[] Rgba);

    /// <summary>Fetch the watch's current framebuffer as RGBA8.
    /// Pulls <c>http://&lt;watchUrl&gt;/screenshot.bin</c>, parses
    /// the <c>"w=W h=H\n"</c> ASCII header, decodes the RGB565 BE
    /// pixel stream into RGBA8, returns ready for canvas draw.</summary>
    public async Task<Screenshot> GetScreenshotAsync(string watchUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(watchUrl))
            throw new InvalidOperationException("watchUrl is empty.");

        // Cache-bust per request - the watch's HttpServer sets
        // Cache-Control: no-cache, but stop browsers from being clever.
        var url = watchUrl.TrimEnd('/') + "/screenshot.bin?t=" +
                  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bytes = await _http.GetByteArrayAsync(url, ct);

        if (bytes.Length < 8 || bytes[0] != (byte)'w')
            throw new InvalidOperationException("Bad screenshot header (expected 'w=W h=H\\n').");

        int nl = System.Array.IndexOf(bytes, (byte)'\n');
        if (nl < 4 || nl > 64)
            throw new InvalidOperationException("Screenshot header newline out of range.");

        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, nl);
        var (w, h) = ParseDim(header);

        int pxOffset = nl + 1;
        int pxCount = w * h;
        if (bytes.Length < pxOffset + pxCount * 2)
            throw new InvalidOperationException("Truncated pixel payload.");

        // RGB565 BE -> RGBA8. Managed buffer; consumer pushes via
        // ImageData / Uint8ClampedArray. ~820 KB at 410x502; cheap
        // enough not to need zero-copy.
        var rgba = new byte[pxCount * 4];
        for (int i = 0; i < pxCount; i++)
        {
            int v = (bytes[pxOffset + i * 2] << 8) | bytes[pxOffset + i * 2 + 1];
            int r5 = (v >> 11) & 0x1F;
            int g6 = (v >> 5)  & 0x3F;
            int b5 =  v        & 0x1F;
            rgba[i * 4    ] = (byte)((r5 << 3) | (r5 >> 2));
            rgba[i * 4 + 1] = (byte)((g6 << 2) | (g6 >> 4));
            rgba[i * 4 + 2] = (byte)((b5 << 3) | (b5 >> 2));
            rgba[i * 4 + 3] = 255;
        }
        return new Screenshot(w, h, rgba);
    }

    /// <summary>POST a SpawnWear app <c>.pe</c> assembly to the
    /// watch's <c>/loadapp</c> endpoint. Watch loads via reflection
    /// and pushes onto the screen stack. Returns the watch's text
    /// reply (e.g. <c>"OK: COUNTER"</c> or
    /// <c>"no ISpawnApp implementer in assembly"</c>).</summary>
    public async Task<string> PostAppAsync(string watchUrl, byte[] peBytes, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(watchUrl))
            throw new InvalidOperationException("watchUrl is empty.");
        if (peBytes is null || peBytes.Length == 0)
            throw new InvalidOperationException("peBytes is empty.");

        var url = watchUrl.TrimEnd('/') + "/loadapp";
        using var content = new ByteArrayContent(peBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}");
        return body.Trim();
    }

    /// <summary>One installed app on the watch's SD-backed library, as
    /// returned by <c>GET /apps</c>.</summary>
    public readonly record struct AppEntry(string Name, long Size);

    static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>List the apps installed on the watch's SD card
    /// (<c>GET /apps</c>). Returns name + size for each. Empty list if
    /// none are installed; throws if the watch is unreachable or the
    /// SD card isn't mounted (the route returns 503).</summary>
    public async Task<IReadOnlyList<AppEntry>> ListAppsAsync(string watchUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(watchUrl))
            throw new InvalidOperationException("watchUrl is empty.");
        var url = watchUrl.TrimEnd('/') + "/apps?t=" +
                  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}");
        var apps = System.Text.Json.JsonSerializer.Deserialize<AppEntry[]>(body, JsonOpts);
        return apps ?? System.Array.Empty<AppEntry>();
    }

    /// <summary>Install a SpawnWear app <c>.pe</c> to the watch's SD card
    /// under the given logical name (<c>POST /apps/install?name=</c>). The
    /// app persists across reboots but is NOT launched. Returns the watch's
    /// text reply (e.g. <c>"OK installed Dice (2144 bytes)"</c>).</summary>
    public async Task<string> InstallAppAsync(string watchUrl, string name, byte[] peBytes, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(watchUrl))
            throw new InvalidOperationException("watchUrl is empty.");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");
        if (peBytes is null || peBytes.Length == 0)
            throw new InvalidOperationException("peBytes is empty.");

        var url = watchUrl.TrimEnd('/') + "/apps/install?name=" + Uri.EscapeDataString(name);
        using var content = new ByteArrayContent(peBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}");
        return body.Trim();
    }

    /// <summary>Launch an installed app by name (<c>POST /apps/launch?name=</c>).
    /// The watch loads it from SD, activates it, records it as the last app
    /// (so it re-activates on next boot), and switches the screen to it.
    /// Returns the watch's text reply (e.g. <c>"OK: DICE"</c>).</summary>
    public async Task<string> LaunchAppAsync(string watchUrl, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(watchUrl))
            throw new InvalidOperationException("watchUrl is empty.");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var url = watchUrl.TrimEnd('/') + "/apps/launch?name=" + Uri.EscapeDataString(name);
        using var content = new ByteArrayContent(System.Array.Empty<byte>());
        using var resp = await _http.PostAsync(url, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}");
        return body.Trim();
    }

    /// <summary>Uninstall an app by name (<c>DELETE /apps/&lt;name&gt;</c>),
    /// removing its <c>.pe</c> from the SD card. Returns the watch's text
    /// reply (e.g. <c>"OK uninstalled Dice"</c>).</summary>
    public async Task<string> UninstallAppAsync(string watchUrl, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(watchUrl))
            throw new InvalidOperationException("watchUrl is empty.");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is empty.");

        var url = watchUrl.TrimEnd('/') + "/apps/" + Uri.EscapeDataString(name);
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}");
        return body.Trim();
    }

    static (int W, int H) ParseDim(string header)
    {
        int w = 0, h = 0;
        foreach (var tok in header.Split(' '))
        {
            var t = tok.Trim();
            if (t.StartsWith("w=") && int.TryParse(t[2..], out var ww)) w = ww;
            else if (t.StartsWith("h=") && int.TryParse(t[2..], out var hh)) h = hh;
        }
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("Bad screenshot header dims: " + header);
        return (w, h);
    }
}
