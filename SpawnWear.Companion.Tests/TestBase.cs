using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SpawnWear.Companion.Tests;

/// <summary>
/// Base for Playwright tests that point at the live SpawnWear.Companion at
/// <see cref="CompanionAppFixture.BaseUrl"/>. One <see cref="IBrowser"/> per
/// test class, one <see cref="IBrowserContext"/> + <see cref="IPage"/> per
/// test. Headless by default; override <see cref="RequiresHeaded"/> for
/// tests that need a real Chromium UI surface (BLE, hand-tracking, etc.).
/// </summary>
public abstract class TestBase
{
    static readonly CompanionAppFixture s_fixture = new();
    protected string BaseUrl => s_fixture.BaseUrl;

    IPlaywright? _pw;
    IBrowser? _browser;
    IBrowserContext? _ctx;
    protected IPage Page { get; private set; } = null!;
    protected List<string> ConsoleMessages { get; } = new();

    protected virtual bool RequiresHeaded => false;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        // Re-enable reflection-based JSON for Playwright's internal serializer
        // when running under .NET 10's stricter file-script defaults. Harmless
        // when the switch is already on.
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", true);

        await s_fixture.EnsureStartedAsync();
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new() { Headless = !RequiresHeaded });
    }

    [SetUp]
    public async Task PerTestSetup()
    {
        ConsoleMessages.Clear();
        _ctx = await _browser!.NewContextAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
        Page = await _ctx.NewPageAsync();
        Page.Console += (_, msg) => ConsoleMessages.Add(msg.Text);
        Page.PageError += (_, err) => Console.WriteLine($"[Page error] {err}");

        var resp = await Page.GotoAsync(BaseUrl, new() { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
        if (resp == null || !resp.Ok)
            throw new InvalidOperationException($"Goto {BaseUrl} returned status={resp?.Status}");

        // Blazor WASM bundle takes a beat to download + boot. Wait for the hero h1.
        await Page.GetByRole(AriaRole.Heading, new() { Name = "SpawnWear Companion" })
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60000 });
    }

    [TearDown]
    public async Task PerTestTeardown()
    {
        if (_ctx != null) { await _ctx.CloseAsync(); _ctx = null; }
    }

    [OneTimeTearDown]
    public async Task OneTimeTeardown()
    {
        if (_browser != null) { await _browser.CloseAsync(); _browser = null; }
        _pw?.Dispose(); _pw = null;
    }
}
