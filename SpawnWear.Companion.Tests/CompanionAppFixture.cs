using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

        // If something is already listening on Port it's almost certainly an
        // orphan dev server from a previous `dotnet test` run that didn't
        // clean up (NUnit doesn't dispose static IAsyncDisposable). Reusing
        // that orphan means tests run against a STALE Companion.dll - the
        // build from the prior run, not the current source. Kill any owner
        // we can identify and start fresh, which is cheap enough.
        await KillExistingPortOwnerAsync(ct).ConfigureAwait(false);

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

    /// <summary>If anything is listening on <see cref="Port"/>, find the owning
    /// process and stop it. Idempotent - if the port is free, this is a no-op.
    /// We only kill when we can resolve a process; we don't bulk-kill by port.
    /// </summary>
    async Task KillExistingPortOwnerAsync(CancellationToken ct)
    {
        try
        {
            // Quick TCP-listener check first: if no one's listening, skip.
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            bool anyOnPort = false;
            foreach (var l in listeners)
            {
                if (l.Port == Port) { anyOnPort = true; break; }
            }
            if (!anyOnPort) return;

            // Resolve PID via netstat - cross-platform via dotnet would need
            // GetExtendedTcpTable on Windows; netstat -ano is simpler and
            // equally reliable on this dev machine.
            var ps = new Process
            {
                StartInfo =
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            ps.Start();
            string output = await ps.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await ps.WaitForExitAsync(ct).ConfigureAwait(false);

            // Parse lines: "  TCP    0.0.0.0:5290    0.0.0.0:0    LISTENING    35600"
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{Port}") || !line.Contains("LISTENING")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[^1], out int pid)) continue;
                try
                {
                    var p = Process.GetProcessById(pid);
                    Console.WriteLine($"[CompanionAppFixture] Killing orphan dev server pid={pid} ({p.ProcessName}) on :{Port}");
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CompanionAppFixture] Could not kill pid={pid}: {ex.Message}");
                }
            }
            // Tiny grace for the OS to release the port socket.
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CompanionAppFixture] KillExistingPortOwner non-fatal: {ex.Message}");
        }
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
