using System.Collections.Generic;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;

namespace SpawnWear.Bridge.Ble;

/// <summary>
/// <see cref="ITransport"/> implementation over Web Bluetooth GATT.
///
/// Connects to a SpawnWear watch via Web Bluetooth, resolves the
/// primary GATT service (<see cref="BleUuids.WifiServiceUuid"/>), and
/// subscribes to every notify-bearing characteristic so the bridge
/// receives <see cref="TransportMessage"/> events as the watch pushes
/// state.
///
/// All inbound notifies route through <see cref="MessageReceived"/>
/// with <see cref="ChannelIds"/> identifying which characteristic the
/// bytes came from. Outbound writes go through
/// <see cref="SendAsync"/>; the channel-id selects the matching write
/// characteristic.
/// </summary>
public class BleTransport : ITransport, IAsyncDisposable
{
    readonly BlazorJSRuntime _js;

    BluetoothDevice? _device;
    BluetoothRemoteGATTServer? _server;
    BluetoothRemoteGATTService? _service;

    // Notify-side characteristics
    BluetoothRemoteGATTCharacteristic? _battery;
    BluetoothRemoteGATTCharacteristic? _imu;
    BluetoothRemoteGATTCharacteristic? _rtc;
    BluetoothRemoteGATTCharacteristic? _button;
    BluetoothRemoteGATTCharacteristic? _wifiStatus;
    BluetoothRemoteGATTCharacteristic? _debugLog;

    // Write-side characteristics (set during Connect, used by SendAsync)
    BluetoothRemoteGATTCharacteristic? _wifiCommand;
    BluetoothRemoteGATTCharacteristic? _wifiCredentials;
    BluetoothRemoteGATTCharacteristic? _debugCommand;

    public BleTransport(BlazorJSRuntime js)
    {
        _js = js;
    }

    public bool IsConnected { get; private set; }

    public event Action<bool>? ConnectionChanged;
    public event Action<TransportMessage>? MessageReceived;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        var serviceUuid = BleUuids.WifiServiceUuid.ToString();

        using var navigator = _js.Get<Navigator>("navigator");
        using var bluetooth = navigator.Bluetooth ?? throw new InvalidOperationException(
            "Web Bluetooth not available in this browser. Use Chrome / Edge / Opera on a desktop or Android.");

        // Filter on our service UUID so the picker only shows SpawnWear watches.
        // Backup name-prefix filter in case the firmware doesn't include the
        // service UUID in its advertisement.
        var options = new BluetoothDeviceOptions
        {
            Filters = new[]
            {
                new BluetoothDeviceFilter { Services = new[] { serviceUuid } },
                new BluetoothDeviceFilter { NamePrefix = "SW-" },
            },
            OptionalServices = new[] { serviceUuid },
        };

        _device = await bluetooth.RequestDevice(options);
        _device.OnGATTServerDisconnected += Device_OnGATTServerDisconnected;

        _server = await _device.GATT!.Connect();
        _service = await _server.GetPrimaryService(serviceUuid);

        // Resolve every characteristic we touch. Some are notify-only,
        // some are write-only, some are both.
        _battery        = await TryGetCharacteristic(_service, BleUuids.BatteryStateUuid);
        _imu            = await TryGetCharacteristic(_service, BleUuids.ImuSampleUuid);
        _rtc            = await TryGetCharacteristic(_service, BleUuids.RtcTimeUuid);
        _button         = await TryGetCharacteristic(_service, BleUuids.ButtonEventUuid);
        _wifiStatus     = await TryGetCharacteristic(_service, BleUuids.WifiStatusUuid);
        _debugLog       = await TryGetCharacteristic(_service, BleUuids.DebugLogOutputUuid);
        _wifiCommand    = await TryGetCharacteristic(_service, BleUuids.WifiCommandUuid);
        _wifiCredentials = await TryGetCharacteristic(_service, BleUuids.WifiCredentialsUuid);
        _debugCommand   = await TryGetCharacteristic(_service, BleUuids.DebugCommandInputUuid);

        // Wire notifies. Each subscription routes inbound bytes to
        // MessageReceived with the channel-id matching the source.
        await SubscribeNotify(_battery,    ChannelIds.Battery);
        await SubscribeNotify(_imu,        ChannelIds.ImuSample);
        await SubscribeNotify(_rtc,        ChannelIds.RtcTime);
        await SubscribeNotify(_button,     ChannelIds.Button);
        await SubscribeNotify(_debugLog,   ChannelIds.DebugLog);

        IsConnected = true;
        ConnectionChanged?.Invoke(true);
    }

    public async Task SendAsync(TransportMessage message, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected");

        var characteristic = message.ChannelId switch
        {
            ChannelIds.WifiCommand     => _wifiCommand,
            ChannelIds.WifiCredentials => _wifiCredentials,
            ChannelIds.RtcTime         => _rtc,
            ChannelIds.DebugCmd        => _debugCommand,
            _ => null,
        };

        if (characteristic is null)
        {
            throw new InvalidOperationException(
                $"No write characteristic registered for channel '{message.ChannelId}'.");
        }

        // WriteWithoutResponse is faster but not always supported. The
        // SpawnDev wrapper's WriteValueWithResponse falls through to
        // whichever the characteristic actually supports.
        await characteristic.WriteValueWithResponse(message.Payload);
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected && _device is null) return;

        // Detach event handlers + dispose every JSObject we own. Order
        // matters - characteristics first, then service, then server,
        // then device.
        await DetachNotify(_battery);    _battery     = null;
        await DetachNotify(_imu);        _imu         = null;
        await DetachNotify(_rtc);        _rtc         = null;
        await DetachNotify(_button);     _button      = null;
        await DetachNotify(_debugLog);   _debugLog    = null;

        DisposeAndClear(ref _wifiStatus);
        DisposeAndClear(ref _wifiCommand);
        DisposeAndClear(ref _wifiCredentials);
        DisposeAndClear(ref _debugCommand);

        _service?.Dispose();    _service = null;

        if (_server is not null)
        {
            if (_server.Connected) _server.Disconnect();
            _server.Dispose();
            _server = null;
        }

        if (_device is not null)
        {
            _device.OnGATTServerDisconnected -= Device_OnGATTServerDisconnected;
            _device.Dispose();
            _device = null;
        }

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }

    static async Task<BluetoothRemoteGATTCharacteristic?> TryGetCharacteristic(
        BluetoothRemoteGATTService service, Guid uuid)
    {
        try
        {
            return await service.GetCharacteristic(uuid.ToString());
        }
        catch
        {
            // Characteristic not present on this firmware build — Bridge
            // gracefully omits its channel rather than aborting connect.
            return null;
        }
    }

    readonly Dictionary<BluetoothRemoteGATTCharacteristic, (string ChannelId, Action<Event> Handler)> _subs = new();

    async Task SubscribeNotify(BluetoothRemoteGATTCharacteristic? c, string channelId)
    {
        if (c is null) return;

        Action<Event> handler = e =>
        {
            using var characteristic = e.TargetAs<BluetoothRemoteGATTCharacteristic>();
            using var value = characteristic.Value;
            if (value is null) return;
            var bytes = value.ReadBytes();
            MessageReceived?.Invoke(new TransportMessage(channelId, bytes));
        };

        c.OnCharacteristicValueChanged += handler;
        _subs[c] = (channelId, handler);
        await c.StartNotifications();
    }

    async Task DetachNotify(BluetoothRemoteGATTCharacteristic? c)
    {
        if (c is null) return;
        if (_subs.TryGetValue(c, out var entry))
        {
            c.OnCharacteristicValueChanged -= entry.Handler;
            _subs.Remove(c);
            try { await c.StopNotifications(); } catch { /* server may already be gone */ }
        }
        c.Dispose();
    }

    static void DisposeAndClear(ref BluetoothRemoteGATTCharacteristic? c)
    {
        c?.Dispose();
        c = null;
    }

    async void Device_OnGATTServerDisconnected(Event e)
    {
        // Watch dropped the connection (out of range, powered off, etc.).
        // Clean up state but don't try to disconnect the server again.
        if (IsConnected)
        {
            try { await DisconnectAsync(); }
            catch { /* swallow - we're cleaning up */ }
        }
    }
}
