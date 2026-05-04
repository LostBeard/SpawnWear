using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SpawnWear.Companion.Tests;

/// <summary>
/// Smoke tests that load every Companion page and assert the main heading is
/// visible without the Blazor error UI surfacing. Catches a wide class of
/// page-render regressions cheaply: a missing Razor file, a typo in
/// @inject, an unhandled exception during initialization. ANY page that
/// stops rendering its h1 will turn this red.
///
/// Each test reuses the shared <see cref="TestBase"/> Playwright lifecycle
/// (browser per class, context+page per test).
/// </summary>
[TestFixture]
public class NavigationTests : TestBase
{
    [Test, Category("Smoke")]
    public async Task Home_Renders()
        => await AssertPageRendersAsync("/", "SpawnWear Companion");

    [Test, Category("Smoke")]
    public async Task Stats_Renders()
        => await AssertPageRendersAsync("/stats", "Telemetry");

    [Test, Category("Smoke")]
    public async Task Wifi_Renders()
        => await AssertPageRendersAsync("/wifi", "WiFi setup");

    [Test, Category("Smoke")]
    public async Task Mirror_Renders()
        => await AssertPageRendersAsync("/mirror", "Screen mirror");

    [Test, Category("Smoke")]
    public async Task Apps_Renders()
        => await AssertPageRendersAsync("/apps", "Drop apps");

    [Test, Category("Smoke")]
    public async Task Console_Renders()
        => await AssertPageRendersAsync("/console", "Debug console");

    [Test, Category("Smoke")]
    public async Task About_Renders()
        => await AssertPageRendersAsync("/about", "About");

    /// <summary>Click each NavMenu link in turn and verify the corresponding
    /// page renders. Mirrors what a user actually does, so a broken
    /// route, missing nav-link href, or stuck navigation handler shows up.</summary>
    [Test, Category("Smoke")]
    public async Task NavMenu_LinksToEveryPage()
    {
        // We're already on Home from PerTestSetup. Click each nav item and
        // confirm the destination page renders its expected h1.
        var navTargets = new (string LinkText, string ExpectedHeading)[]
        {
            ("Stats",   "Telemetry"),
            ("WiFi",    "WiFi setup"),
            ("Mirror",  "Screen mirror"),
            ("Apps",    "Drop apps"),
            ("Console", "Debug console"),
            ("About",   "About"),
            ("Home",    "SpawnWear Companion"),
        };

        foreach (var (linkText, heading) in navTargets)
        {
            await Page.GetByRole(AriaRole.Link, new() { Name = linkText, Exact = true }).ClickAsync();
            await Page.GetByRole(AriaRole.Heading, new() { Name = heading })
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await AssertNoBlazorErrorAsync();
        }
    }

    async Task AssertPageRendersAsync(string route, string expectedHeading)
    {
        // Already on / from PerTestSetup. Navigate to the target page.
        var url = BaseUrl.TrimEnd('/') + route;
        var resp = await Page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
        Assert.That(resp, Is.Not.Null, $"GotoAsync({url}) returned null response");
        Assert.That(resp!.Ok, $"GotoAsync({url}) returned status {resp.Status}");

        await Page.GetByRole(AriaRole.Heading, new() { Name = expectedHeading })
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });

        await AssertNoBlazorErrorAsync();
    }

    /// <summary>The blazor-error-ui div is hidden by default and only becomes
    /// visible when an unhandled exception surfaces. Asserting it stays
    /// hidden catches startup / lifecycle errors that would otherwise just
    /// log to console and let the test pass with a broken page.</summary>
    async Task AssertNoBlazorErrorAsync()
    {
        bool errorVisible = await Page.Locator("#blazor-error-ui").IsVisibleAsync();
        if (errorVisible)
        {
            var errText = await Page.Locator("#blazor-error-ui").InnerTextAsync();
            Assert.Fail($"Blazor error UI is visible on {Page.Url}: {errText}");
        }
    }
}
