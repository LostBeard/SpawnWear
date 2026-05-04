#:package Microsoft.Playwright@1.52.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Headless-Chromium readability test for the Companion. Navigates to a running
// dev server (default http://localhost:5290), takes a full-page screenshot, and
// reads computed CSS color for the elements that were invisible in TJ's report
// (.stat-label, .stat-value, .stat-sub, .tagline, .muted). Catches the bootstrap
// `--bs-card-color` regression by measuring text color against card background
// luminance.
//
// Usage:
//   dotnet run tools/companion-readability-test.cs                     # http://localhost:5290
//   dotnet run tools/companion-readability-test.cs http://localhost:7191
//
// The script writes screenshots to tools/companion-readability-screenshots/
// and prints PASS/FAIL based on a luminance-contrast threshold.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

// Belt-and-suspenders: also flip the AppContext switch in case the property
// directive doesn't propagate through the dotnet-run host. Either path on its
// own should be enough; both lets the script keep working if the .NET 10 host
// changes how `#:property` is honored.
AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

string url = args.Length > 0 ? args[0] : "http://localhost:5290/";
string outDir = "tools/companion-readability-screenshots";
Directory.CreateDirectory(outDir);

Console.WriteLine($"=== Companion readability check against {url} ===");

using var pw = await Playwright.CreateAsync();
await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
var ctx = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
var page = await ctx.NewPageAsync();

page.Console += (_, e) => Console.WriteLine($"  [browser] {e.Type}: {e.Text}");

await page.GotoAsync(url);
// Blazor WASM takes a beat to boot. Wait for the home-page hero to render.
await page.WaitForSelectorAsync("h1.tagline, .tagline, h1", new() { Timeout = 30000 });
// Give CSS + render a moment to settle.
await page.WaitForTimeoutAsync(500);

string screenshot = Path.Combine(outDir, "home.png");
await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
Console.WriteLine($"  screenshot -> {screenshot}");

// Read computed colors for the elements TJ called out as invisible.
async Task<(string color, double luminance)> ProbeAsync(string selector)
{
    var color = await page.EvaluateAsync<string>(@"
        sel => {
            const el = document.querySelector(sel);
            if (!el) return 'NOT_FOUND';
            const cs = getComputedStyle(el);
            return cs.color;
        }
    ", selector);
    double lum = ColorToLuminance(color);
    return (color, lum);
}

// Stage relative-luminance helper - WCAG-style. Returns 0..1 for typical rgb()/rgba() strings.
static double ColorToLuminance(string c)
{
    if (string.IsNullOrEmpty(c) || c == "NOT_FOUND") return -1;
    int rs = c.IndexOf('('), re = c.IndexOf(')');
    if (rs < 0 || re < rs) return -1;
    var parts = c.Substring(rs + 1, re - rs - 1).Split(',');
    if (parts.Length < 3) return -1;
    double Channel(string s) {
        var v = double.Parse(s.Trim()) / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
    double r = Channel(parts[0]), g = Channel(parts[1]), b = Channel(parts[2]);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

string[] selectors = new[] { ".tagline", ".stat-label", ".stat-value", ".stat-sub", ".muted", ".badge" };
int passed = 0, failed = 0, skipped = 0;
const double MinLuminance = 0.30; // body color #d8dae4 is ~0.71; anything under 0.30 is too dark to read on #1f2330 cards.

foreach (var sel in selectors)
{
    var (color, lum) = await ProbeAsync(sel);
    string verdict;
    if (color == "NOT_FOUND")
    {
        // Selector legitimately absent on this page (e.g. `.muted` only renders
        // when there are saved pairings). Don't fail on that - flag and skip.
        verdict = "SKIP";
        skipped++;
    }
    else if (lum >= MinLuminance)
    {
        verdict = "PASS";
        passed++;
    }
    else
    {
        verdict = "FAIL";
        failed++;
    }
    Console.WriteLine($"  {verdict}  {sel,-15} computed color={color}  luminance={lum:0.000}");
}

Console.WriteLine();
Console.WriteLine($"=== Result: {passed} pass, {failed} fail, {skipped} skip ===");
return failed == 0 ? 0 : 1;
