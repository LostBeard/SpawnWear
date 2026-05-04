using SpawnDev.BlazorJS;

namespace SpawnWear.Bridge.WebRtc;

/// <summary>
/// <see cref="ITransport"/> implementation over a WebRTC peer-to-peer
/// data channel. Phase 7 work; placeholder today so consumers can plan
/// against the API.
///
/// Signaling: the Blazor app and the watch exchange SDP offer/answer +
/// ICE candidates over BLE GATT (using the existing connected
/// <see cref="Ble.BleTransport"/> as a courier). Once the data channel
/// is open, this transport takes over for high-bandwidth payloads
/// (IMU streams, video frames, large file transfers, audio).
///
/// Why both: BLE is always available + works without WiFi but can't
/// stream audio/video. WebRTC needs network reachability between peers
/// (LAN OK, internet via STUN/TURN otherwise) but handles real-time
/// media. With BLE-as-signaling, the two devices never need to share a
/// network at all - they meet over BLE, exchange signaling, then talk
/// peer-to-peer over WebRTC if a path exists.
/// </summary>
public class WebRtcTransport : ITransport
{
    readonly BlazorJSRuntime _js;
    readonly Ble.BleTransport _signalingChannel;

    public WebRtcTransport(BlazorJSRuntime js, Ble.BleTransport signalingChannel)
    {
        _js = js;
        _signalingChannel = signalingChannel;
    }

    public bool IsConnected { get; private set; }

    // Events are the public contract; Phase 7 fills in the senders when
    // SpawnDev.RTC + BLE-as-signaling are wired. Suppressing CS0067 here
    // keeps the warning-clean build until then.
#pragma warning disable CS0067
    public event Action<bool>? ConnectionChanged;
    public event Action<TransportMessage>? MessageReceived;
#pragma warning restore CS0067

    public Task ConnectAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "WebRtcTransport: Phase 7 work. Will use SpawnDev.RTC for the peer + " +
            "the supplied BLE transport for signaling. Stub registered so consumers " +
            "can wire DI today against the eventual API.");
    }

    public Task SendAsync(TransportMessage message, CancellationToken ct = default) =>
        throw new NotImplementedException("Phase 7");

    /// <summary>WebRTC has no concept of "readable state" - state arrives
    /// via push from the watch over the data channel. No-op.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;
}
