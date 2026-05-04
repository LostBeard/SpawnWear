using System.Net;
using System.Net.Http;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Tests for the HTTP-side Bridge surface (<see cref="WatchHttp"/>).
/// Uses an in-memory <see cref="HttpMessageHandler"/> stub so the
/// suite stays deterministic without standing up a real server. Real
/// production HttpClient + real watch are exercised in manual silicon
/// tests; these lock the wire-format parsing.
/// </summary>
public class WatchHttpTests
{
    sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<HttpRequestMessage> Sent { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    static (WatchHttp http, StubHandler stub) NewClient()
    {
        var stub = new StubHandler();
        var client = new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") };
        return (new WatchHttp(client), stub);
    }

    static byte[] BuildScreenshot(int w, int h, ushort fillRgb565 = 0x07E0 /* solid green */)
    {
        var hdr = System.Text.Encoding.ASCII.GetBytes($"w={w} h={h}\n");
        var pixels = new byte[w * h * 2];
        byte hi = (byte)(fillRgb565 >> 8);
        byte lo = (byte)(fillRgb565 & 0xFF);
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 2]     = hi;
            pixels[i * 2 + 1] = lo;
        }
        var combined = new byte[hdr.Length + pixels.Length];
        Buffer.BlockCopy(hdr, 0, combined, 0, hdr.Length);
        Buffer.BlockCopy(pixels, 0, combined, hdr.Length, pixels.Length);
        return combined;
    }

    [Fact]
    public async Task GetScreenshotAsync_decodes_a_solid_green_panel()
    {
        // 4x3 panel of solid-green RGB565 should decode to 4*3 = 12 RGBA pixels.
        var (http, stub) = NewClient();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(BuildScreenshot(4, 3, 0x07E0)),
        };

        var shot = await http.GetScreenshotAsync("http://192.168.1.171");

        Assert.Equal(4, shot.Width);
        Assert.Equal(3, shot.Height);
        Assert.Equal(4 * 3 * 4, shot.Rgba.Length);

        // Solid green expansion: RGB565 0x07E0 -> g6 = 0x3F = 63
        // r5 = 0, b5 = 0, alpha = 255.
        // g8 = (63 << 2) | (63 >> 4) = 252 | 3 = 255.
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(0,   shot.Rgba[i * 4]);     // R
            Assert.Equal(255, shot.Rgba[i * 4 + 1]); // G - should be saturated
            Assert.Equal(0,   shot.Rgba[i * 4 + 2]); // B
            Assert.Equal(255, shot.Rgba[i * 4 + 3]); // A
        }
    }

    [Fact]
    public async Task GetScreenshotAsync_appends_cache_buster_query_param()
    {
        var (http, stub) = NewClient();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(BuildScreenshot(2, 2)),
        };
        await http.GetScreenshotAsync("http://192.168.1.171");

        Assert.Single(stub.Sent);
        var url = stub.Sent[0].RequestUri!.ToString();
        Assert.Contains("/screenshot.bin?t=", url);
    }

    [Fact]
    public async Task GetScreenshotAsync_strips_trailing_slash_in_watch_url()
    {
        var (http, stub) = NewClient();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(BuildScreenshot(2, 2)),
        };
        await http.GetScreenshotAsync("http://192.168.1.171/");

        var url = stub.Sent[0].RequestUri!.ToString();
        Assert.StartsWith("http://192.168.1.171/screenshot.bin", url);
        Assert.DoesNotContain("//screenshot.bin", url);
    }

    [Fact]
    public async Task GetScreenshotAsync_rejects_bad_header_marker()
    {
        var (http, stub) = NewClient();
        // Header doesn't start with 'w=' - some wrong / truncated response.
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("bad payload")),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await http.GetScreenshotAsync("http://192.168.1.171"));
    }

    [Fact]
    public async Task GetScreenshotAsync_rejects_truncated_pixels()
    {
        // Header claims 4x3 = 12 px = 24 bytes of payload, but we only ship 4 px.
        var bytes = System.Text.Encoding.ASCII.GetBytes("w=4 h=3\n");
        var truncated = new byte[bytes.Length + 8];
        Buffer.BlockCopy(bytes, 0, truncated, 0, bytes.Length);

        var (http, stub) = NewClient();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(truncated),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await http.GetScreenshotAsync("http://192.168.1.171"));
    }

    [Fact]
    public async Task GetScreenshotAsync_throws_on_empty_url()
    {
        var (http, _) = NewClient();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await http.GetScreenshotAsync(""));
    }

    [Fact]
    public async Task PostAppAsync_returns_watch_reply_on_2xx()
    {
        var (http, stub) = NewClient();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("OK: COUNTER\n"),
        };
        var reply = await http.PostAppAsync("http://192.168.1.171", new byte[]{ 0x01, 0x02 });
        Assert.Equal("OK: COUNTER", reply);
        Assert.Single(stub.Sent);
        Assert.Equal(HttpMethod.Post, stub.Sent[0].Method);
        Assert.EndsWith("/loadapp", stub.Sent[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PostAppAsync_throws_on_4xx_with_watch_body_in_message()
    {
        var (http, stub) = NewClient();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("400 Bad Request\r\n\r\nMissing Content-Length"),
        };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await http.PostAppAsync("http://192.168.1.171", new byte[]{ 0x01 }));
        Assert.Contains("Missing Content-Length", ex.Message);
    }

    [Fact]
    public async Task PostAppAsync_throws_on_empty_url_or_bytes()
    {
        var (http, _) = NewClient();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await http.PostAppAsync("", new byte[]{ 0x01 }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await http.PostAppAsync("http://192.168.1.171", Array.Empty<byte>()));
    }
}
