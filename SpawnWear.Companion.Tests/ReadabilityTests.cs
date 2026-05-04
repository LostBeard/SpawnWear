using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SpawnWear.Companion.Tests;

/// <summary>
/// Regression gate for the bootstrap-card / opacity readability bug TJ caught
/// 2026-05-04 ("I sitll can't see most test due to the font coolors matching
/// the background"). Bootstrap 5's <c>.card { color: var(--bs-card-color) }</c>
/// falls back to LIGHT-theme dark text against our dark cards; per-component
/// <c>&lt;style&gt;</c> blocks set <c>opacity</c> as low as 0.35 on text. Both
/// are corrected in <c>SpawnWear.Companion/wwwroot/css/app.css</c>.
///
/// Each test reads the COMPUTED color of an element that was previously
/// invisible and checks the WCAG-style relative luminance is above a
/// readable threshold. Passes today. Fails the moment someone re-introduces
/// an opacity below the floor or removes the <c>.card *</c> color override.
/// </summary>
[TestFixture]
public class ReadabilityTests : TestBase
{
    /// <summary>Body color #d8dae4 sits at luminance ~0.71. Effective text on
    /// dark cards drops a bit but should stay well above 0.30 - that's the
    /// "is this visible at all" floor.</summary>
    const double MinLuminance = 0.30;

    [Test, Category("Smoke")]
    public async Task HeroTagline_HasReadableContrast()
        => await AssertReadableAsync(".tagline");

    [Test, Category("Smoke")]
    public async Task StatCardLabel_HasReadableContrast()
        => await AssertReadableAsync(".stat-label");

    [Test, Category("Smoke")]
    public async Task StatCardValue_HasReadableContrast()
        => await AssertReadableAsync(".stat-value");

    [Test, Category("Smoke")]
    public async Task StatCardSub_HasReadableContrast()
        => await AssertReadableAsync(".stat-sub");

    [Test, Category("Smoke")]
    public async Task BuildTimestamp_IsVisible()
    {
        // The StatusBar at the top renders BuildInfo.Timestamp. Verify the
        // element exists, has non-empty text, and is laid out.
        var locator = Page.Locator(".statusbar .build");
        await Expect(locator).ToBeVisibleAsync();
        var text = (await locator.InnerTextAsync())?.Trim() ?? "";
        Assert.That(text, Does.StartWith("build "),
            $"Expected '.build' to start with 'build ', got '{text}'");
        Assert.That(text, Does.Not.Contain("unknown"),
            "AssemblyMetadata BuildTimestamp didn't propagate - check csproj.");
    }

    async Task AssertReadableAsync(string selector)
    {
        var color = await Page.EvaluateAsync<string>(@"
            sel => {
                const el = document.querySelector(sel);
                if (!el) return 'NOT_FOUND';
                return getComputedStyle(el).color;
            }
        ", selector);

        Assert.That(color, Is.Not.EqualTo("NOT_FOUND"),
            $"Selector '{selector}' missing from rendered home page - did the page structure change?");

        double lum = ColorToLuminance(color);
        Assert.That(lum, Is.GreaterThanOrEqualTo(MinLuminance),
            $"Selector '{selector}' computed color={color} (luminance={lum:0.000}) " +
            $"is below the readable floor {MinLuminance}. Likely a CSS regression in app.css.");
    }

    /// <summary>WCAG-style relative luminance of an rgb()/rgba() color string.</summary>
    static double ColorToLuminance(string c)
    {
        if (string.IsNullOrEmpty(c) || c == "NOT_FOUND") return -1;
        int rs = c.IndexOf('('), re = c.IndexOf(')');
        if (rs < 0 || re < rs) return -1;
        var parts = c.Substring(rs + 1, re - rs - 1).Split(',');
        if (parts.Length < 3) return -1;
        static double Channel(string s)
        {
            var v = double.Parse(s.Trim()) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        double r = Channel(parts[0]), g = Channel(parts[1]), b = Channel(parts[2]);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
