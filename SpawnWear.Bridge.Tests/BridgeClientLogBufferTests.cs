using System.Text;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Real-world regression coverage for <see cref="BridgeClient"/>'s
/// debug-log ring buffer (the <c>_recentLogLines</c> field reached
/// through <see cref="BridgeClient.GetRecentLogLines"/> and
/// <see cref="BridgeClient.ClearRecentLogLines"/>).
///
/// The buffer exists to solve a specific Companion-app pain point:
/// the user pairs the watch on the Home page (firmware writes
/// <c>"[Pair] Saved 116 bytes to I:\\spawnwear-pair.bin"</c> through
/// the <see cref="ChannelIds.DebugLog"/> channel), then navigates to
/// the Console tab. Console.razor mounts AFTER the line arrived, so
/// it can't receive it via the live <see cref="BridgeClient.DebugLogReceived"/>
/// event - that already fired. Console.razor's <c>OnInitialized</c>
/// instead calls <c>GetRecentLogLines()</c> to backfill its display.
///
/// Every test below drives the real production path: a real
/// <see cref="TransportMessage"/> with channel id <c>"log"</c> and
/// real UTF-8 payload bytes, pushed through <see cref="FakeTransport"/>
/// into the real <c>OnMessageReceived</c> handler. The ring buffer
/// state is observed only through the public API. No mocks; no
/// reaching into private state.
/// </summary>
public class BridgeClientLogBufferTests
{
    static async Task<(BridgeClient client, FakeTransport transport)> NewBridge()
    {
        var client = new BridgeClient();
        var transport = new FakeTransport();
        await client.UseTransportAsync(transport);
        return (client, transport);
    }

    static TransportMessage LogMessage(string text) =>
        new(ChannelIds.DebugLog, Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task DebugLog_payload_is_captured_in_recent_log_buffer_for_late_subscribers()
    {
        // Real-world scenario: a [Pair] log line arrives BEFORE Console.razor
        // mounts. The line must still be visible when the page calls
        // GetRecentLogLines on its OnInitialized.
        var (client, transport) = await NewBridge();

        transport.Push(LogMessage("[Pair] Saved 116 bytes to I:\\spawnwear-pair.bin"));

        var lines = client.GetRecentLogLines();
        Assert.Single(lines);
        Assert.Equal("[Pair] Saved 116 bytes to I:\\spawnwear-pair.bin", lines[0]);
    }

    [Fact]
    public async Task DebugLog_payload_also_fires_DebugLogReceived_for_live_subscribers()
    {
        // Real-world scenario: Console.razor IS mounted when a log line
        // arrives. It must still reach both the live event (for immediate
        // UI append) AND the ring buffer (so a sibling subscriber that
        // mounts later still sees it).
        var (client, transport) = await NewBridge();
        var liveReceived = new List<string>();
        client.DebugLogReceived += line => liveReceived.Add(line);

        transport.Push(LogMessage("[WiFi] Connected to MyNetwork"));

        Assert.Single(liveReceived);
        Assert.Equal("[WiFi] Connected to MyNetwork", liveReceived[0]);
        Assert.Equal(new[] { "[WiFi] Connected to MyNetwork" }, client.GetRecentLogLines());
    }

    [Fact]
    public async Task Late_mounting_page_replays_lines_in_arrival_order()
    {
        // Real-world scenario: the user kicks off WiFi scan + pair flow on
        // Home, then jumps to Console after several lines have streamed in.
        // The Console must render them in the order they arrived (a foreach
        // over GetRecentLogLines is exactly how Console.razor builds the
        // backlog ListGroup).
        var (client, transport) = await NewBridge();

        transport.Push(LogMessage("[WiFi] Scanning..."));
        transport.Push(LogMessage("[WiFi] Found 4 networks"));
        transport.Push(LogMessage("[Pair] Awaiting companion"));
        transport.Push(LogMessage("[Pair] Saved 116 bytes to I:\\spawnwear-pair.bin"));

        var lines = client.GetRecentLogLines();
        Assert.Equal(new[]
        {
            "[WiFi] Scanning...",
            "[WiFi] Found 4 networks",
            "[Pair] Awaiting companion",
            "[Pair] Saved 116 bytes to I:\\spawnwear-pair.bin",
        }, lines);
    }

    [Fact]
    public async Task Recent_log_buffer_caps_at_500_and_evicts_oldest_first()
    {
        // Real-world scenario: a sustained-output operation (BLE bonded
        // re-pair retry loop, firmware self-test sweep) emits hundreds of
        // log lines while Console isn't mounted. When the user finally
        // opens Console, the buffer MUST still be bounded - otherwise the
        // PWA leaks memory until the tab is closed. The current code
        // caps at 500 and evicts FIFO; both are observable via the public
        // API.
        var (client, transport) = await NewBridge();

        for (int i = 0; i < 600; i++)
            transport.Push(LogMessage($"line {i:D4}"));

        var lines = client.GetRecentLogLines();
        Assert.Equal(500, lines.Length);
        // Oldest 100 (lines 0..99) evicted; survivors are lines 100..599
        // in arrival order.
        Assert.Equal("line 0100", lines[0]);
        Assert.Equal("line 0599", lines[^1]);
    }

    [Fact]
    public async Task ClearRecentLogLines_empties_buffer_but_does_not_break_future_capture()
    {
        // Real-world scenario: user clicks the Console "Clear" button to
        // wipe the screen. The ring buffer also has to clear (otherwise
        // the next page-mount restores everything they just cleared) -
        // and capture must keep working for new arrivals.
        var (client, transport) = await NewBridge();
        transport.Push(LogMessage("old line A"));
        transport.Push(LogMessage("old line B"));

        client.ClearRecentLogLines();
        Assert.Empty(client.GetRecentLogLines());

        transport.Push(LogMessage("new line after clear"));
        Assert.Equal(new[] { "new line after clear" }, client.GetRecentLogLines());
    }

    [Fact]
    public async Task Other_channel_payloads_do_not_leak_into_the_log_buffer()
    {
        // Real-world scenario: the wire is multiplexed - battery / IMU /
        // wifi-status / button / rtc / log all share the same notify pipe.
        // Only payloads on the "log" channel must end up in the log
        // buffer. If unrelated channels polluted the buffer, the Console
        // tab would show garbled binary alongside legitimate log strings.
        var (client, transport) = await NewBridge();

        // Real channel payloads (mirroring the firmware schemas exercised
        // by BridgeClientDecoderTests).
        transport.Push(new TransportMessage(ChannelIds.Battery,
            new byte[] { 87, 0b0000_0011, 0xE8, 0x10, 0xCE, 0xFF }));
        transport.Push(new TransportMessage(ChannelIds.Button,
            new byte[] { 1, 0 }));
        transport.Push(new TransportMessage(ChannelIds.RtcTime,
            new byte[] { 0xE7, 0x07, 5, 4, 12, 30, 0, 0 }));

        Assert.Empty(client.GetRecentLogLines());

        // A real log line still gets through afterwards.
        transport.Push(LogMessage("[Pair] keypair loaded"));
        Assert.Equal(new[] { "[Pair] keypair loaded" }, client.GetRecentLogLines());
    }

    [Fact]
    public async Task Multi_line_payload_is_stored_as_one_entry_matching_wire_framing()
    {
        // Real-world scenario: firmware sometimes batches multiple lines
        // into a single notify (a stack trace, an enumerated drive list).
        // The wire framing is one TransportMessage per write - so one
        // entry in the ring buffer, not one entry per '\n'. Console.razor
        // is responsible for splitting on display. This test pins the
        // contract that the buffer preserves the framing the firmware
        // chose, rather than re-splitting underneath.
        var (client, transport) = await NewBridge();
        var multiLine =
            "[Drive] I: type=3 size=-1\r\n" +
            "[Drive]   I: has 0 dirs + 1 files\r\n" +
            "[Drive]   FILE I:\\spawnwear-pair.bin";

        transport.Push(LogMessage(multiLine));

        var lines = client.GetRecentLogLines();
        Assert.Single(lines);
        Assert.Equal(multiLine, lines[0]);
    }

    [Fact]
    public async Task GetRecentLogLines_returns_an_independent_snapshot_callers_cannot_corrupt()
    {
        // Real-world scenario: a Razor page cached the result of
        // GetRecentLogLines and mutated its local copy (sort, slice,
        // null-out) for display. That MUST NOT leak back into the
        // bridge's buffer; otherwise Page A's display sort would corrupt
        // Page B's view of the same buffer. The buffer is a snapshot
        // contract - mutating the returned array is always safe.
        var (client, transport) = await NewBridge();
        transport.Push(LogMessage("first"));
        transport.Push(LogMessage("second"));

        var snapshot = client.GetRecentLogLines();
        Array.Reverse(snapshot);
        snapshot[0] = "TAMPERED";

        var fresh = client.GetRecentLogLines();
        Assert.Equal(new[] { "first", "second" }, fresh);
    }
}
