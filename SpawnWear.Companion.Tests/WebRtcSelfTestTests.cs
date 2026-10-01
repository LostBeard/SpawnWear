using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SpawnWear.Companion.Tests;

/// <summary>
/// Real end-to-end WebRTC: clicks the Home page's "Run WebRTC self-test" button so the
/// browser Companion (SpawnDev.RTC browser backend + SpawnDev.SpawnJS.Cryptography WebCrypto
/// Ed25519) connects over hub.spawndev.com to a real desktop peer
/// (<c>SpawnWear.Bridge.Desktop -- watch</c>, SipSorcery backend + DotNetCrypto), completes the
/// mutual Ed25519 challenge, and gets its ping echoed back over the datachannel.
///
/// This is the browser interop path the smoke tests never reach: page renders prove SpawnJS
/// boots, this proves RTCPeerConnection / datachannel / WebCrypto marshalling actually work.
/// Needs internet (the hub). No watch needed - the self-test room is not the watch's room.
/// </summary>
[Category("Network")]
public class WebRtcSelfTestTests : TestBase
{
    Process? _peer;
    readonly TaskCompletionSource _peerJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _peerVerified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<string> _peerReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [OneTimeSetUp]
    public async Task StartDesktopPeer()
    {
        var projectDir = ResolveProjectDirectory("SpawnWear.Bridge.Desktop");
        _peer = new Process
        {
            StartInfo =
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectDir}\" -- watch",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectDir,
            },
        };
        _peer.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine($"[DesktopPeer] {e.Data}");
            if (e.Data.Contains("[watch] joining the self-test room")) _peerJoined.TrySetResult();
            if (e.Data.Contains("[watch] CONNECTED + mutually verified")) _peerVerified.TrySetResult();
            if (e.Data.Contains("[watch] recv channel=")) _peerReceived.TrySetResult(e.Data);
        };
        _peer.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[DesktopPeer ERR] {e.Data}"); };
        _peer.Start();
        _peer.BeginOutputReadLine();
        _peer.BeginErrorReadLine();

        // dotnet run builds first; the peer prints "joining" right before it announces to the hub.
        await _peerJoined.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    [OneTimeTearDown]
    public async Task StopDesktopPeer()
    {
        if (_peer is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(); } catch { }
        }
        _peer?.Dispose();
        _peer = null;
    }

    [Test]
    public async Task SelfTest_ConnectsVerifiesAndEchoes()
    {
        await Page.GetByRole(AriaRole.Button, new() { Name = "Run WebRTC self-test" }).ClickAsync();

        // The page sets a SUCCESS or FAILED status line when the attempt finishes (45 s connect + 10 s echo budget).
        var status = Page.Locator("div.muted", new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^(SUCCESS|FAILED)") });
        await status.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 70000 });
        var text = await status.InnerTextAsync();
        Console.WriteLine($"[SelfTest] status: {text}");

        Assert.That(text, Does.StartWith("SUCCESS"), text);
        Assert.That(text, Does.Contain("echo: ping from browser"), text);

        // The desktop side must agree: it verified the browser's Ed25519 challenge and received the ping.
        await _peerVerified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var recv = await _peerReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(recv, Does.Contain("payload='ping from browser'"), recv);
    }

    static string ResolveProjectDirectory(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var csproj = Path.Combine(dir.FullName, name, name + ".csproj");
            if (File.Exists(csproj)) return Path.GetDirectoryName(csproj)!;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find {name}/{name}.csproj walking up from {AppContext.BaseDirectory}");
    }
}
