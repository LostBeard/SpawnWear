using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SpawnWear.Companion.Tests;

/// <summary>
/// Spawns a dedicated <c>dotnet run</c> instance of <c>SpawnWear.Companion</c>
/// on a fixed port (default 5290) so Playwright tests have a known target.
/// One fixture instance per test run; reused across all test classes.
///
/// Lighter than NanoFrameTest1.Tests' BlazorAppFixture because for visual /
/// readability checks we don't need the static-publish + HTTPS-cert dance -
/// a plain http://localhost:5290 dev server is enough. If a future test needs
/// real Web Bluetooth or service-worker cert behavior, upgrade this to mirror
/// NanoFrameTest1.Tests' BlazorAppFixture pattern.
/// </summary>
public sealed class CompanionAppFixture : IAsyncDisposable
{
    const int Port = 5290;
    public string BaseUrl => $"http://localhost:{Port}/";

    static readonly HttpClient s_probe = new() { Timeout = TimeSpan.FromSeconds(3) };

    Process? _proc;
    bool _started;
    readonly object _lock = new();

    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }

        // If something is already listening on Port, treat that as the running
        // app (e.g. TJ has VS open serving the same project on a different
        // port - this fixture only manages :5290). Probe; if responds, reuse.
        if (await ProbeAsync(ct).ConfigureAwait(false))
        {
            Console.WriteLine($"[CompanionAppFixture] Reusing existing server at {BaseUrl}");
            return;
        }

        var projectDir = ResolveCompanionProjectDirectory();
        Console.WriteLine($"[CompanionAppFixture] Spawning dotnet run for {projectDir}");

        _proc = new Process
        {
            StartInfo =
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectDir}\" --no-launch-profile --urls {BaseUrl.TrimEnd('/')}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectDir,
            },
            EnableRaisingEvents = true,
        };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[Companion] {e.Data}"); };
        _proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[Companion ERR] {e.Data}"); };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // Poll until Kestrel binds + serves the index page.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await ProbeAsync(ct).ConfigureAwait(false))
            {
                Console.WriteLine($"[CompanionAppFixture] Up at {BaseUrl}");
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(
            $"SpawnWear.Companion failed to bind {BaseUrl} within 2 min. Check console output above.");
    }

    async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await s_probe.GetAsync(BaseUrl, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    static string ResolveCompanionProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var csproj = Path.Combine(dir.FullName, "SpawnWear.Companion", "SpawnWear.Companion.csproj");
            if (File.Exists(csproj))
                return Path.GetDirectoryName(csproj)!;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find SpawnWear.Companion/SpawnWear.Companion.csproj walking up from " +
            AppContext.BaseDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        if (_proc is { } p && !p.HasExited)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { }
        }
        _proc?.Dispose();
        _proc = null;
    }
}
