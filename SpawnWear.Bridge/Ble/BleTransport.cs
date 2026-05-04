using SpawnDev.BlazorJS;

namespace SpawnWear.Bridge.Ble;

/// <summary>
/// <see cref="ITransport"/> implementation over Web Bluetooth GATT.
///
/// Browser-side workflow:
/// 1. <see cref="ConnectAsync"/> calls <c>navigator.bluetooth.requestDevice</c>
///    with a service-UUID filter so the browser shows the user the
///    SpawnWear watch in the picker.
/// 2. On selection, connect to the GATT server, get the SpawnWear primary
///    service, and resolve all the characteristics we care about.
/// 3. Subscribe to NOTIFY characteristics; route inbound bytes to
///    <see cref="MessageReceived"/> with the channel id matching the
///    characteristic's UUID.
/// 4. <see cref="SendAsync"/> writes to the matching write-characteristic.
///
/// V0.1: stub. Real Web Bluetooth wiring lands in follow-up commits as
/// SpawnDev.BlazorJS's typed Bluetooth interop API surface stabilizes.
/// Marking the methods so consumers can register the service today and
/// flip on real connections later without API changes.
/// </summary>
public class BleTransport : ITransport
{
    readonly BlazorJSRuntime _js;

    public BleTransport(BlazorJSRuntime js)
    {
        _js = js;
    }

    public bool IsConnected { get; private set; }

    public event Action<bool>? ConnectionChanged;
    public event Action<TransportMessage>? MessageReceived;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        // TODO Phase 4a: implement via SpawnDev.BlazorJS Web Bluetooth wrappers.
        // Pseudocode:
        //   var device = await navigator.Bluetooth.RequestDeviceAsync(
        //       new RequestDeviceOptions { Filters = [{ Services = [BleUuids.WifiServiceUuid] }] });
        //   var server = await device.Gatt.ConnectAsync();
        //   var svc = await server.GetPrimaryServiceAsync(BleUuids.WifiServiceUuid);
        //   _battery = await svc.GetCharacteristicAsync(BleUuids.BatteryNotifyUuid);
        //   _battery.OnCharacteristicValueChanged += b => MessageReceived?.Invoke(
        //       new TransportMessage(ChannelIds.Battery, b));
        //   await _battery.StartNotificationsAsync();
        //   ...
        //   IsConnected = true; ConnectionChanged?.Invoke(true);
        throw new NotImplementedException("BleTransport.ConnectAsync arrives in the next commit. Stub registered so Bridge consumers can DI today.");
    }

    public Task SendAsync(TransportMessage message, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected");
        // TODO route by ChannelId to the matching write characteristic.
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (!IsConnected) return Task.CompletedTask;
        IsConnected = false;
        ConnectionChanged?.Invoke(false);
        return Task.CompletedTask;
    }
}
