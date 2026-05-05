using System.Text;

namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Real-world wire-format tests for <see cref="BridgeClient"/>'s
/// outbound helpers. These are the methods Companion-app code actually
/// calls every time the user sets up WiFi, scans for networks, or
/// types a debug command - if any of them packs the wrong channel id
/// or wrong byte sequence, the watch silently ignores the message and
/// the user sees "nothing happened".
///
/// Each test inspects <see cref="FakeTransport.SentMessages"/> to
/// confirm the production code emits exactly the bytes the firmware
/// expects on the matching channel (see firmware's
/// <c>WatchProfileService</c> + <c>BleUuids.WifiCmd*</c>).
/// </summary>
public class BridgeClientSendPathTests
{
    static async Task<(BridgeClient client, FakeTransport transport)> NewBridge()
    {
        var client = new BridgeClient();
        var transport = new FakeTransport();
        await client.UseTransportAsync(transport);
        return (client, transport);
    }

    [Fact]
    public async Task SetWifiAsync_writes_credentials_then_connect_command()
    {
        // Real-world: user types SSID + password into the Wifi setup UI
        // and clicks Connect. Companion calls SetWifiAsync. Watch firmware
        // expects two writes - first SSID\nPassword UTF-8 to wifi.creds,
        // then 0x01 (WifiCmdConnect) to wifi.cmd. Order matters: connect
        // before creds = stale credentials reused.
        var (client, transport) = await NewBridge();

        await client.SetWifiAsync("MyHomeNetwork", "Hunter2!");

        Assert.Equal(2, transport.SentMessages.Count);

        var creds = transport.SentMessages[0];
        Assert.Equal(ChannelIds.WifiCredentials, creds.ChannelId);
        Assert.Equal("MyHomeNetwork\nHunter2!", Encoding.UTF8.GetString(creds.Payload));

        var cmd = transport.SentMessages[1];
        Assert.Equal(ChannelIds.WifiCommand, cmd.ChannelId);
        Assert.Equal(new byte[] { BleUuids.WifiCmdConnect }, cmd.Payload);
    }

    [Fact]
    public async Task SetWifiAsync_empty_ssid_throws_before_any_send()
    {
        // Real-world: empty-SSID write would brick the watch's saved-creds
        // slot (firmware stores whatever it got). The validation is meant
        // to throw BEFORE the BLE write happens.
        var (client, transport) = await NewBridge();

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetWifiAsync("", "anything"));
        Assert.Empty(transport.SentMessages);
    }

    [Fact]
    public async Task SetWifiAsync_ssid_containing_newline_throws()
    {
        // Real-world: newline is the framing delimiter between SSID and
        // password on the wire. An SSID containing a newline would split
        // wrong, the watch would parse part of the SSID as the password,
        // and try to connect to the wrong-named network. The validation
        // exists for this exact attack/typo class.
        var (client, transport) = await NewBridge();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SetWifiAsync("Bad\nSSID", "pw"));
        Assert.Empty(transport.SentMessages);
    }

    [Fact]
    public async Task SetWifiAsync_open_network_with_empty_password_sends_trailing_separator()
    {
        // Real-world: open WiFi networks have no password. The wire format
        // is still SSID\nPassword - the password is just empty. Firmware
        // splits on the first '\n' and treats the rest as the password.
        var (client, transport) = await NewBridge();

        await client.SetWifiAsync("CafeOpenWifi", "");

        Assert.Equal(2, transport.SentMessages.Count);
        Assert.Equal("CafeOpenWifi\n",
            Encoding.UTF8.GetString(transport.SentMessages[0].Payload));
    }

    [Fact]
    public async Task SetWifiAsync_null_password_is_normalized_to_empty()
    {
        // Real-world: the Razor binding for the password field can deliver
        // null when the user never focused the input. The helper has to
        // tolerate that without NullReferenceException - same wire format
        // as an empty password.
        var (client, transport) = await NewBridge();

        await client.SetWifiAsync("CafeOpenWifi", null!);

        Assert.Equal("CafeOpenWifi\n",
            Encoding.UTF8.GetString(transport.SentMessages[0].Payload));
    }

    [Fact]
    public async Task SetWifiAsync_preserves_utf8_in_ssid_and_password()
    {
        // Real-world: home networks frequently have non-ASCII in the SSID
        // ("Frühstück", emoji, etc). The wire format is UTF-8; the helper
        // must not coerce to ASCII (would corrupt the SSID and the watch
        // wouldn't find the network).
        var (client, transport) = await NewBridge();
        const string ssid = "Frühstück-WiFi";
        const string password = "pässwört123";

        await client.SetWifiAsync(ssid, password);

        var roundTrip = Encoding.UTF8.GetString(transport.SentMessages[0].Payload);
        Assert.Equal(ssid + "\n" + password, roundTrip);
    }

    [Fact]
    public async Task DisconnectWifiAsync_sends_single_byte_0x02_to_wifi_cmd()
    {
        // Real-world: user clicks "Disconnect" on the WiFi panel.
        // Firmware switches on the byte; 0x02 = disconnect (per
        // BleUuids.WifiCmdDisconnect).
        var (client, transport) = await NewBridge();

        await client.DisconnectWifiAsync();

        var msg = Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiCommand, msg.ChannelId);
        Assert.Equal(new byte[] { BleUuids.WifiCmdDisconnect }, msg.Payload);
        Assert.Equal((byte)0x02, msg.Payload[0]);
    }

    [Fact]
    public async Task ForgetWifiAsync_sends_single_byte_0x03_to_wifi_cmd()
    {
        // Real-world: "Forget this network" wipes the watch's saved creds.
        // 0x03 = forget. Distinct from disconnect (which leaves creds in
        // place).
        var (client, transport) = await NewBridge();

        await client.ForgetWifiAsync();

        var msg = Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiCommand, msg.ChannelId);
        Assert.Equal(new byte[] { BleUuids.WifiCmdForget }, msg.Payload);
        Assert.Equal((byte)0x03, msg.Payload[0]);
    }

    [Fact]
    public async Task ScanWifiAsync_sends_single_byte_to_wifi_scan_channel()
    {
        // Real-world: clicking "Scan" on the WiFi setup UI triggers an
        // async scan on the watch. Firmware ignores the body bytes (it's
        // an event trigger, not a parameterized call) but the BLE write
        // has to be non-empty - we send 0x01.
        var (client, transport) = await NewBridge();

        await client.ScanWifiAsync();

        var msg = Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiScan, msg.ChannelId);
        Assert.Equal(new byte[] { 0x01 }, msg.Payload);
    }

    [Fact]
    public async Task SendDebugCommandAsync_writes_utf8_command_to_log_cmd_channel()
    {
        // Real-world: the Console tab's input box pipes user-typed
        // commands ("drives", "ble status") to the watch's debug
        // console. Firmware decodes UTF-8 and dispatches by string.
        var (client, transport) = await NewBridge();

        await client.SendDebugCommandAsync("drives");

        var msg = Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.DebugCmd, msg.ChannelId);
        Assert.Equal("drives", Encoding.UTF8.GetString(msg.Payload));
    }

    [Fact]
    public async Task SendDebugCommandAsync_empty_command_throws_before_send()
    {
        // Real-world: empty-string command is a no-op the firmware can't
        // route. The helper rejects it instead of generating a useless
        // BLE write.
        var (client, transport) = await NewBridge();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendDebugCommandAsync(""));
        Assert.Empty(transport.SentMessages);
    }

    [Fact]
    public async Task SendAsync_with_no_transport_throws_invalid_operation()
    {
        // Real-world: a Razor page subscribes to BridgeClient before
        // UseTransportAsync has run (race during app startup). A send
        // call in that window must throw cleanly so the page can render
        // a "not connected" state - not NullReference, not silent drop.
        var client = new BridgeClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(new TransportMessage(ChannelIds.WifiScan, new byte[] { 0x01 })));
        Assert.Contains("UseTransportAsync", ex.Message);
    }

    [Fact]
    public async Task DisconnectWifiAsync_and_ForgetWifiAsync_emit_distinct_command_bytes()
    {
        // Real-world: pinning the contract that 0x02 != 0x03. If a future
        // refactor accidentally collapses both helpers to the same byte,
        // user sees "Disconnect" but firmware actually wipes creds (data
        // loss) - or vice versa.
        var (client, transport) = await NewBridge();

        await client.DisconnectWifiAsync();
        await client.ForgetWifiAsync();

        Assert.Equal(2, transport.SentMessages.Count);
        Assert.NotEqual(transport.SentMessages[0].Payload[0],
                        transport.SentMessages[1].Payload[0]);
        Assert.Equal(BleUuids.WifiCmdDisconnect, transport.SentMessages[0].Payload[0]);
        Assert.Equal(BleUuids.WifiCmdForget,     transport.SentMessages[1].Payload[0]);
    }
}
