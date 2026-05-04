namespace SpawnWear.Bridge;

/// <summary>
/// High-level client for talking to a SpawnWear watch. Holds an
/// <see cref="ITransport"/> (BLE today, WebRTC in Phase 7) and exposes
/// strongly-typed events + commands on top of the channel-id messages
/// the transport delivers.
///
/// Typical Blazor consumer pattern:
///
/// <code>
/// // Program.cs
/// builder.Services.AddSpawnWearBridge();
///
/// // Consumer page
/// [Inject] BridgeClient Bridge { get; set; } = default!;
/// await Bridge.ConnectAsync(); // shows the Web Bluetooth picker
/// Bridge.BatteryChanged += (s, e) => { ... };
/// </code>
///
/// V0.1: skeleton with connection lifecycle + a couple of event hooks.
/// Wire-up of the BLE transport + characteristic subscriptions lands in
/// follow-up commits as the firmware-side WatchProfileService stabilizes.
/// </summary>
public class BridgeClient : IAsyncDisposable
{
    ITransport? _transport;

    /// <summary>True when an underlying transport is connected.</summary>
    public bool IsConnected => _transport?.IsConnected ?? false;

    /// <summary>The active transport, or null if none is wired. Most
    /// consumers shouldn't reach for this directly - use the typed
    /// helper methods instead. Phase 7's <c>PairingFlow</c> needs the
    /// raw <see cref="ITransport"/> to call its pairing-specific
    /// methods; that's the canonical use case.</summary>
    public ITransport? GetUnderlyingTransport() => _transport;

    /// <summary>Best-effort human-readable peer identifier from the
    /// active transport (e.g. "SW-OK-Tok" from BLE), or null if not
    /// connected.</summary>
    public string? PeerName => _transport?.PeerName;

    public event Action<bool>? ConnectionChanged;
    public event Action<BatteryState>? BatteryChanged;
    public event Action<ImuSample>? ImuSampleReceived;
    public event Action<RtcTime>? RtcTimeReceived;
    public event Action<WifiStatus>? WifiStatusChanged;
    public event Action<WifiScanResult[]>? WifiScanResultsReceived;
    public event Action<ButtonEvent>? ButtonEventReceived;
    public event Action<string>? DebugLogReceived;

    /// <summary>Use the supplied transport for the next Connect call.
    /// Replaces any prior transport. Closes the previous one.</summary>
    public async Task UseTransportAsync(ITransport transport)
    {
        if (_transport != null)
        {
            _transport.ConnectionChanged -= OnConnectionChanged;
            _transport.MessageReceived -= OnMessageReceived;
            await _transport.DisconnectAsync();
        }
        _transport = transport;
        if (_transport != null)
        {
            _transport.ConnectionChanged += OnConnectionChanged;
            _transport.MessageReceived += OnMessageReceived;
        }
    }

    public Task ConnectAsync(CancellationToken ct = default) =>
        _transport?.ConnectAsync(ct) ?? Task.CompletedTask;

    public Task DisconnectAsync() =>
        _transport?.DisconnectAsync() ?? Task.CompletedTask;

    /// <summary>Send a framed message to the watch through whichever
    /// transport is currently active. Throws if not connected.</summary>
    public Task SendAsync(TransportMessage message, CancellationToken ct = default)
    {
        if (_transport is null)
            throw new InvalidOperationException("No transport configured. Call UseTransportAsync first.");
        return _transport.SendAsync(message, ct);
    }

    /// <summary>Force a one-shot read of every readable characteristic
    /// and feed the bytes through the decoder pipeline. Equivalent to
    /// "show me what the watch's state is RIGHT NOW" without waiting
    /// for the next push. Call after Connect to populate UI before any
    /// notify lands.</summary>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        if (_transport is null) return Task.CompletedTask;
        return _transport.RefreshAsync(ct);
    }

    // ===== Typed helpers =====
    // These wrap the channel-id + payload-packing for the most common
    // operations so consumers don't have to know wire formats. Each is
    // a pure passthrough to SendAsync with the correct channel + bytes.

    /// <summary>Save WiFi credentials on the watch and tell it to
    /// connect. Two BLE writes: <c>"SSID\nPassword"</c> UTF-8 to the
    /// credentials characteristic, then <c>WifiCmdConnect</c> byte to
    /// the command characteristic.</summary>
    public async Task SetWifiAsync(string ssid, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ssid)) throw new ArgumentException("ssid is empty", nameof(ssid));
        if (ssid.Contains('\n'))        throw new ArgumentException("ssid may not contain a newline", nameof(ssid));
        var combined = ssid + "\n" + (password ?? "");
        var payload = System.Text.Encoding.UTF8.GetBytes(combined);
        await SendAsync(new TransportMessage(ChannelIds.WifiCredentials, payload), ct);
        await SendAsync(new TransportMessage(ChannelIds.WifiCommand, new[]{ BleUuids.WifiCmdConnect }), ct);
    }

    /// <summary>Tell the watch to disconnect from its current WiFi
    /// network. <c>WifiCmdDisconnect</c> byte to the command
    /// characteristic.</summary>
    public Task DisconnectWifiAsync(CancellationToken ct = default) =>
        SendAsync(new TransportMessage(ChannelIds.WifiCommand, new[]{ BleUuids.WifiCmdDisconnect }), ct);

    /// <summary>Tell the watch to forget stored WiFi credentials.
    /// <c>WifiCmdForget</c> byte.</summary>
    public Task ForgetWifiAsync(CancellationToken ct = default) =>
        SendAsync(new TransportMessage(ChannelIds.WifiCommand, new[]{ BleUuids.WifiCmdForget }), ct);

    /// <summary>Trigger a WiFi scan on the watch. Results arrive on
    /// <see cref="WifiScanResultsReceived"/> when the watch finishes
    /// scanning. Body byte is ignored by firmware; we send 0x01 as a
    /// non-empty placeholder.</summary>
    public Task ScanWifiAsync(CancellationToken ct = default) =>
        SendAsync(new TransportMessage(ChannelIds.WifiScan, new byte[]{ 0x01 }), ct);

    /// <summary>Send a UTF-8 command to the watch's debug console.
    /// Fires through the firmware's <c>DebugConsoleService</c> command
    /// handler.</summary>
    public Task SendDebugCommandAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(command)) throw new ArgumentException("command is empty", nameof(command));
        var bytes = System.Text.Encoding.UTF8.GetBytes(command);
        return SendAsync(new TransportMessage(ChannelIds.DebugCmd, bytes), ct);
    }

    void OnConnectionChanged(bool connected) => ConnectionChanged?.Invoke(connected);

    void OnMessageReceived(TransportMessage msg)
    {
        // Channel-id-based dispatch. Each branch decodes the payload
        // according to its schema (defined in firmware's
        // SpawnWear/WatchProfileService.cs).
        switch (msg.ChannelId)
        {
            case ChannelIds.Battery:
                if (msg.Payload.Length >= 6)
                {
                    // Firmware schema (WatchProfileService.NotifyBatteryState):
                    //   [percent:u8][flags:u8][voltage_mV:u16-LE][current_mA:i16-LE]
                    //   flags bit0 = charging, bit1 = USB VBUS present, bit2 = low battery
                    byte flags = msg.Payload[1];
                    var b = new BatteryState(
                        Percent:           msg.Payload[0],
                        IsCharging:        (flags & 0x01) != 0,
                        IsVbusPresent:     (flags & 0x02) != 0,
                        IsLowBattery:      (flags & 0x04) != 0,
                        VoltageMillivolts: (ushort)(msg.Payload[2] | (msg.Payload[3] << 8)),
                        CurrentMilliamps:  (short) (msg.Payload[4] | (msg.Payload[5] << 8)));
                    BatteryChanged?.Invoke(b);
                }
                break;

            case ChannelIds.ImuSample:
                if (msg.Payload.Length >= 12)
                {
                    short ax = (short)(msg.Payload[0] | (msg.Payload[1] << 8));
                    short ay = (short)(msg.Payload[2] | (msg.Payload[3] << 8));
                    short az = (short)(msg.Payload[4] | (msg.Payload[5] << 8));
                    short gx = (short)(msg.Payload[6] | (msg.Payload[7] << 8));
                    short gy = (short)(msg.Payload[8] | (msg.Payload[9] << 8));
                    short gz = (short)(msg.Payload[10] | (msg.Payload[11] << 8));
                    ImuSampleReceived?.Invoke(new ImuSample(ax, ay, az, gx, gy, gz));
                }
                break;

            case ChannelIds.WifiScan:
                {
                    // Firmware schema (WifiConfigService.PerformWifiScan):
                    //   "SSID|RSSI\nSSID2|RSSI2\n..."
                    //   RSSI is decimal int (dBm, typically negative).
                    var text = System.Text.Encoding.UTF8.GetString(msg.Payload);
                    var lines = text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
                    var results = new List<WifiScanResult>(lines.Length);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        var pipe = line.LastIndexOf('|');
                        if (pipe < 0) { results.Add(new WifiScanResult(line, 0)); continue; }
                        var ssid = line.Substring(0, pipe);
                        var rssiStr = line.Substring(pipe + 1);
                        int.TryParse(rssiStr, out int rssi);
                        results.Add(new WifiScanResult(ssid, rssi));
                    }
                    WifiScanResultsReceived?.Invoke(results.ToArray());
                }
                break;

            case ChannelIds.WifiStatus:
                if (msg.Payload.Length >= 1)
                {
                    var state = (WifiState)msg.Payload[0];
                    var ip = msg.Payload.Length > 1
                        ? System.Text.Encoding.UTF8.GetString(msg.Payload, 1, msg.Payload.Length - 1)
                        : "";
                    WifiStatusChanged?.Invoke(new WifiStatus(state, ip));
                }
                break;

            case ChannelIds.Button:
                if (msg.Payload.Length >= 2)
                {
                    // Firmware schema (WatchProfileService.NotifyButtonEvent):
                    //   [button:u8][action:u8]
                    //   button: 0x01=BOOT, 0x02=PWR (BleUuids.Button*)
                    //   action: 0x01=Down, 0x02=Up, 0x03=Click, 0x04=DoubleClick, 0x05=LongPress
                    ButtonEventReceived?.Invoke(new ButtonEvent(
                        Button: (WatchButton)msg.Payload[0],
                        Action: (ButtonAction)msg.Payload[1]));
                }
                break;

            case ChannelIds.RtcTime:
                if (msg.Payload.Length >= 8)
                {
                    var t = new RtcTime(
                        Year:    (ushort)(msg.Payload[0] | (msg.Payload[1] << 8)),
                        Month:   msg.Payload[2],
                        Day:     msg.Payload[3],
                        Hour:    msg.Payload[4],
                        Minute:  msg.Payload[5],
                        Second:  msg.Payload[6],
                        Weekday: msg.Payload[7]);
                    RtcTimeReceived?.Invoke(t);
                }
                break;

            case ChannelIds.DebugLog:
                DebugLogReceived?.Invoke(System.Text.Encoding.UTF8.GetString(msg.Payload));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport != null) await _transport.DisconnectAsync();
        _transport = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Channel identifiers used in <see cref="TransportMessage.ChannelId"/>.
/// On BLE these map 1:1 to GATT characteristic UUIDs; on WebRTC they're
/// the channel-id field of the framed data-channel message.</summary>
public static class ChannelIds
{
    public const string Battery         = "battery";
    public const string ImuSample       = "imu";
    public const string RtcTime         = "rtc";
    public const string Button          = "button";
    public const string DebugLog        = "log";
    public const string DebugCmd        = "log.cmd";
    public const string WifiStatus      = "wifi.status";
    public const string WifiScan        = "wifi.scan";
    public const string WifiCommand     = "wifi.cmd";
    public const string WifiCredentials = "wifi.creds";
    public const string AppPayload      = "app.payload";
}

public readonly record struct BatteryState(byte Percent, bool IsCharging, bool IsVbusPresent, bool IsLowBattery, ushort VoltageMillivolts, short CurrentMilliamps);
public readonly record struct ImuSample(short Ax, short Ay, short Az, short Gx, short Gy, short Gz);
public readonly record struct RtcTime(ushort Year, byte Month, byte Day, byte Hour, byte Minute, byte Second, byte Weekday);

/// <summary>WiFi connection state reported by the watch on
/// <see cref="ChannelIds.WifiStatus"/>. Mirrors the firmware-side
/// constants in <c>WifiConfigService.cs</c>.</summary>
public enum WifiState : byte
{
    Disconnected = 0,
    Connecting   = 1,
    Connected    = 2,
    Failed       = 3,
}

public readonly record struct WifiStatus(WifiState State, string IpAddress);

/// <summary>One row in a watch-side WiFi scan. RSSI is dBm (typically
/// negative; -50 = strong, -90 = weak). Mirrors the
/// <c>"SSID|RSSI"</c> line format produced by
/// <c>WifiConfigService.PerformWifiScan</c>.</summary>
public readonly record struct WifiScanResult(string Ssid, int RssiDbm);

/// <summary>Watch-side hardware buttons. Mirrors firmware constants
/// in <c>BleUuids.Button*</c>.</summary>
public enum WatchButton : byte
{
    Boot = 0x01,
    Pwr  = 0x02,
}

/// <summary>Press / release / click cadence for a watch button.
/// Mirrors firmware constants in <c>BleUuids.Action*</c>.</summary>
public enum ButtonAction : byte
{
    Down        = 0x01,
    Up          = 0x02,
    Click       = 0x03,
    DoubleClick = 0x04,
    LongPress   = 0x05,
}

public readonly record struct ButtonEvent(WatchButton Button, ButtonAction Action);
