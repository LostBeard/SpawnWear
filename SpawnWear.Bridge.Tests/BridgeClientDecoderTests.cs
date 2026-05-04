namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Wire-format regression tests for <see cref="BridgeClient"/>'s
/// channel-id decoders. The goal is to catch any drift between the
/// firmware's <c>Notify*</c> producers and the Bridge's consumers - if
/// either side changes its byte layout without updating the other, one
/// of these tests fails immediately.
///
/// Each test mirrors what the corresponding firmware service writes
/// via <c>nanoFramework.Device.Bluetooth.GenericAttributeProfile.DataWriter</c>.
/// nanoFramework's DataWriter defaults to <b>little-endian</b> for
/// 16-bit + 32-bit primitives - the tests reflect that.
/// </summary>
public class BridgeClientDecoderTests
{
    static async Task<(BridgeClient client, FakeTransport transport)> NewBridge()
    {
        var client = new BridgeClient();
        var transport = new FakeTransport();
        await client.UseTransportAsync(transport);
        return (client, transport);
    }

    [Fact]
    public async Task Battery_decodes_full_payload_with_all_flags()
    {
        // Firmware schema (WatchProfileService.NotifyBatteryState):
        //   [percent:u8][flags:u8][voltage_mV:u16-LE][current_mA:i16-LE]
        //   flags: bit0=charging, bit1=usbVbusPresent, bit2=lowBattery
        var payload = new byte[]
        {
            87,             // percent = 87
            0b0000_0011,    // charging + USB present, not low
            0xE8, 0x10,     // 4328 mV LE
            0xCE, 0xFF,     // -50 mA LE (i16)
        };

        var (client, transport) = await NewBridge();
        BatteryState? got = null;
        client.BatteryChanged += b => got = b;

        transport.Push(new TransportMessage(ChannelIds.Battery, payload));

        Assert.NotNull(got);
        Assert.Equal(87, got!.Value.Percent);
        Assert.True(got.Value.IsCharging);
        Assert.True(got.Value.IsVbusPresent);
        Assert.False(got.Value.IsLowBattery);
        Assert.Equal(4328, got.Value.VoltageMillivolts);
        Assert.Equal(-50, got.Value.CurrentMilliamps);
    }

    [Fact]
    public async Task Battery_low_battery_flag_decodes_independently()
    {
        var payload = new byte[]
        {
            12,             // 12% (real low-battery scenario)
            0b0000_0100,    // low battery, not charging, no USB
            0x80, 0x0E,     // 3712 mV LE
            0x9C, 0xFF,     // -100 mA LE
        };
        var (client, transport) = await NewBridge();
        BatteryState? got = null;
        client.BatteryChanged += b => got = b;

        transport.Push(new TransportMessage(ChannelIds.Battery, payload));

        Assert.NotNull(got);
        Assert.False(got!.Value.IsCharging);
        Assert.False(got.Value.IsVbusPresent);
        Assert.True(got.Value.IsLowBattery);
    }

    [Fact]
    public async Task Battery_too_short_payload_fires_no_event()
    {
        var (client, transport) = await NewBridge();
        bool fired = false;
        client.BatteryChanged += _ => fired = true;

        transport.Push(new TransportMessage(ChannelIds.Battery, new byte[]{ 50, 0x01 }));

        Assert.False(fired);
    }

    [Fact]
    public async Task Imu_decodes_six_signed_int16_le()
    {
        // Firmware schema (WatchProfileService.NotifyImuSample):
        //   [ax i16][ay i16][az i16][gx i16][gy i16][gz i16] all LE
        var payload = new byte[]
        {
            0x00, 0x01,   // ax = 256
            0xFF, 0xFF,   // ay = -1
            0x00, 0x40,   // az = 16384
            0xC0, 0x80,   // gx = -32576 -> 0x80C0 LE = 0x80C0 -> -32576 (i16)
            0x00, 0x00,   // gy = 0
            0x10, 0x27,   // gz = 10000
        };

        var (client, transport) = await NewBridge();
        ImuSample? got = null;
        client.ImuSampleReceived += s => got = s;

        transport.Push(new TransportMessage(ChannelIds.ImuSample, payload));

        Assert.NotNull(got);
        Assert.Equal(256, got!.Value.Ax);
        Assert.Equal(-1, got.Value.Ay);
        Assert.Equal(16384, got.Value.Az);
        Assert.Equal(-32576, got.Value.Gx);
        Assert.Equal(0, got.Value.Gy);
        Assert.Equal(10000, got.Value.Gz);
    }

    [Fact]
    public async Task Rtc_decodes_year_le_then_packed_bytes()
    {
        // Firmware schema (WatchProfileService.NotifyRtcTime):
        //   [year u16-LE][month u8][day u8][hour u8][min u8][sec u8][wd u8]
        var payload = new byte[]
        {
            0xEA, 0x07,   // 2026 LE
            5,            // May
            5,            // 5th
            14,           // 14:00 hour
            32,           // :32
            7,            // :07
            2,            // Tuesday
        };

        var (client, transport) = await NewBridge();
        RtcTime? got = null;
        client.RtcTimeReceived += t => got = t;

        transport.Push(new TransportMessage(ChannelIds.RtcTime, payload));

        Assert.NotNull(got);
        Assert.Equal(2026, got!.Value.Year);
        Assert.Equal(5, got.Value.Month);
        Assert.Equal(5, got.Value.Day);
        Assert.Equal(14, got.Value.Hour);
        Assert.Equal(32, got.Value.Minute);
        Assert.Equal(7, got.Value.Second);
        Assert.Equal(2, got.Value.Weekday);
    }

    [Theory]
    [InlineData((byte)0, WifiState.Disconnected)]
    [InlineData((byte)1, WifiState.Connecting)]
    [InlineData((byte)2, WifiState.Connected)]
    [InlineData((byte)3, WifiState.Failed)]
    public async Task WifiStatus_state_byte_maps_to_enum(byte b, WifiState expected)
    {
        // Firmware schema (WifiConfigService.BuildStatusBuffer):
        //   [state u8][ip_string]
        var ipBytes = System.Text.Encoding.UTF8.GetBytes("192.168.1.171");
        var payload = new byte[1 + ipBytes.Length];
        payload[0] = b;
        Buffer.BlockCopy(ipBytes, 0, payload, 1, ipBytes.Length);

        var (client, transport) = await NewBridge();
        WifiStatus? got = null;
        client.WifiStatusChanged += s => got = s;

        transport.Push(new TransportMessage(ChannelIds.WifiStatus, payload));

        Assert.NotNull(got);
        Assert.Equal(expected, got!.Value.State);
        Assert.Equal("192.168.1.171", got.Value.IpAddress);
    }

    [Fact]
    public async Task WifiStatus_state_only_payload_yields_empty_ip()
    {
        var (client, transport) = await NewBridge();
        WifiStatus? got = null;
        client.WifiStatusChanged += s => got = s;

        transport.Push(new TransportMessage(ChannelIds.WifiStatus, new byte[]{ 2 }));

        Assert.NotNull(got);
        Assert.Equal(WifiState.Connected, got!.Value.State);
        Assert.Equal("", got.Value.IpAddress);
    }

    [Theory]
    [InlineData(0x01, 0x01, WatchButton.Boot, ButtonAction.Down)]
    [InlineData(0x01, 0x03, WatchButton.Boot, ButtonAction.Click)]
    [InlineData(0x02, 0x05, WatchButton.Pwr,  ButtonAction.LongPress)]
    [InlineData(0x02, 0x04, WatchButton.Pwr,  ButtonAction.DoubleClick)]
    [InlineData(0x01, 0x02, WatchButton.Boot, ButtonAction.Up)]
    public async Task Button_decodes_button_and_action_bytes(byte b, byte a, WatchButton expectedBtn, ButtonAction expectedAct)
    {
        var (client, transport) = await NewBridge();
        ButtonEvent? got = null;
        client.ButtonEventReceived += e => got = e;
        transport.Push(new TransportMessage(ChannelIds.Button, new byte[]{ b, a }));
        Assert.NotNull(got);
        Assert.Equal(expectedBtn, got!.Value.Button);
        Assert.Equal(expectedAct, got.Value.Action);
    }

    [Fact]
    public async Task Button_too_short_payload_fires_no_event()
    {
        var (client, transport) = await NewBridge();
        bool fired = false;
        client.ButtonEventReceived += _ => fired = true;
        transport.Push(new TransportMessage(ChannelIds.Button, new byte[]{ 0x01 }));
        Assert.False(fired);
    }

    [Fact]
    public async Task WifiScan_decodes_pipe_separated_lines()
    {
        // Firmware schema (WifiConfigService.PerformWifiScan):
        //   "SSID|RSSI\nSSID2|RSSI2\n..." UTF-8.
        var payload = System.Text.Encoding.UTF8.GetBytes("HomeNetwork|-52\nNeighborWiFi|-77\nXfinity|-89");
        var (client, transport) = await NewBridge();
        WifiScanResult[]? got = null;
        client.WifiScanResultsReceived += r => got = r;

        transport.Push(new TransportMessage(ChannelIds.WifiScan, payload));

        Assert.NotNull(got);
        Assert.Equal(3, got!.Length);
        Assert.Equal("HomeNetwork", got[0].Ssid);
        Assert.Equal(-52, got[0].RssiDbm);
        Assert.Equal("NeighborWiFi", got[1].Ssid);
        Assert.Equal(-77, got[1].RssiDbm);
        Assert.Equal("Xfinity", got[2].Ssid);
        Assert.Equal(-89, got[2].RssiDbm);
    }

    [Fact]
    public async Task WifiScan_handles_ssid_with_pipe_in_name()
    {
        // SSID can technically contain '|' - the firmware's split is on the
        // LAST '|', so the RSSI is always the rightmost segment. Guard the
        // decoder against losing characters in pathological names.
        var payload = System.Text.Encoding.UTF8.GetBytes("Net|Special|-60");
        var (client, transport) = await NewBridge();
        WifiScanResult[]? got = null;
        client.WifiScanResultsReceived += r => got = r;
        transport.Push(new TransportMessage(ChannelIds.WifiScan, payload));
        Assert.Single(got!);
        Assert.Equal("Net|Special", got![0].Ssid);
        Assert.Equal(-60, got[0].RssiDbm);
    }

    [Fact]
    public async Task WifiScan_empty_payload_yields_empty_array()
    {
        var (client, transport) = await NewBridge();
        WifiScanResult[]? got = null;
        client.WifiScanResultsReceived += r => got = r;
        transport.Push(new TransportMessage(ChannelIds.WifiScan, Array.Empty<byte>()));
        Assert.NotNull(got);
        Assert.Empty(got!);
    }

    [Fact]
    public async Task DebugLog_decodes_utf8()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("[Boot] hello — 🌎");
        var (client, transport) = await NewBridge();
        string? got = null;
        client.DebugLogReceived += s => got = s;

        transport.Push(new TransportMessage(ChannelIds.DebugLog, payload));

        Assert.Equal("[Boot] hello — 🌎", got);
    }

    [Fact]
    public async Task Connection_lifecycle_event_round_trips_through_client()
    {
        var (client, _) = await NewBridge();
        var states = new List<bool>();
        client.ConnectionChanged += b => states.Add(b);

        // The fake's ConnectAsync sets IsConnected and fires (re-fired through client).
        await client.ConnectAsync();
        await client.DisconnectAsync();

        Assert.False(client.IsConnected);
        Assert.Contains(true, states);
        Assert.Contains(false, states);
    }

    [Fact]
    public async Task SendAsync_routes_through_transport_send()
    {
        var (client, transport) = await NewBridge();
        var msg = new TransportMessage(ChannelIds.WifiCommand, new byte[]{ BleUuids.WifiCmdConnect });
        await client.SendAsync(msg);
        Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiCommand, transport.SentMessages[0].ChannelId);
        Assert.Equal(BleUuids.WifiCmdConnect, transport.SentMessages[0].Payload[0]);
    }

    [Fact]
    public async Task SendAsync_without_transport_throws()
    {
        var bare = new BridgeClient();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await bare.SendAsync(new TransportMessage(ChannelIds.WifiCommand, new byte[]{ 1 })));
    }

    [Fact]
    public async Task RefreshAsync_without_transport_is_noop()
    {
        // Distinct from SendAsync - Refresh is an "update if you can"
        // hint, not a hard send. Should silently skip without throwing
        // when no transport is wired.
        var bare = new BridgeClient();
        await bare.RefreshAsync();
    }

    [Fact]
    public async Task RefreshAsync_routes_through_transport()
    {
        var (client, transport) = await NewBridge();
        Assert.Equal(0, transport.RefreshCallCount);
        await client.RefreshAsync();
        Assert.Equal(1, transport.RefreshCallCount);
        await client.RefreshAsync();
        Assert.Equal(2, transport.RefreshCallCount);
    }

    [Fact]
    public async Task SetWifiAsync_packs_creds_then_sends_connect_command()
    {
        var (client, transport) = await NewBridge();
        await client.SetWifiAsync("HomeNet", "secret");

        Assert.Equal(2, transport.SentMessages.Count);

        // First: credentials char gets "SSID\nPassword" UTF-8
        Assert.Equal(ChannelIds.WifiCredentials, transport.SentMessages[0].ChannelId);
        var creds = System.Text.Encoding.UTF8.GetString(transport.SentMessages[0].Payload);
        Assert.Equal("HomeNet\nsecret", creds);

        // Second: command char gets the WifiCmdConnect byte
        Assert.Equal(ChannelIds.WifiCommand, transport.SentMessages[1].ChannelId);
        Assert.Single(transport.SentMessages[1].Payload);
        Assert.Equal(BleUuids.WifiCmdConnect, transport.SentMessages[1].Payload[0]);
    }

    [Fact]
    public async Task SetWifiAsync_rejects_newline_in_ssid()
    {
        var (client, _) = await NewBridge();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.SetWifiAsync("Has\nNewline", "pw"));
    }

    [Fact]
    public async Task SetWifiAsync_rejects_empty_ssid()
    {
        var (client, _) = await NewBridge();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.SetWifiAsync("", "pw"));
    }

    [Fact]
    public async Task DisconnectWifiAsync_sends_correct_command_byte()
    {
        var (client, transport) = await NewBridge();
        await client.DisconnectWifiAsync();
        Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiCommand, transport.SentMessages[0].ChannelId);
        Assert.Equal(BleUuids.WifiCmdDisconnect, transport.SentMessages[0].Payload[0]);
    }

    [Fact]
    public async Task ForgetWifiAsync_sends_correct_command_byte()
    {
        var (client, transport) = await NewBridge();
        await client.ForgetWifiAsync();
        Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiCommand, transport.SentMessages[0].ChannelId);
        Assert.Equal(BleUuids.WifiCmdForget, transport.SentMessages[0].Payload[0]);
    }

    [Fact]
    public async Task ScanWifiAsync_writes_to_scan_channel()
    {
        var (client, transport) = await NewBridge();
        await client.ScanWifiAsync();
        Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.WifiScan, transport.SentMessages[0].ChannelId);
        // Body byte is a placeholder; firmware ignores content but
        // requires at least 1 byte to trigger a scan.
        Assert.True(transport.SentMessages[0].Payload.Length >= 1);
    }

    [Fact]
    public async Task SendDebugCommandAsync_packs_utf8_to_debug_channel()
    {
        var (client, transport) = await NewBridge();
        await client.SendDebugCommandAsync("redraw");
        Assert.Single(transport.SentMessages);
        Assert.Equal(ChannelIds.DebugCmd, transport.SentMessages[0].ChannelId);
        Assert.Equal("redraw", System.Text.Encoding.UTF8.GetString(transport.SentMessages[0].Payload));
    }
}
