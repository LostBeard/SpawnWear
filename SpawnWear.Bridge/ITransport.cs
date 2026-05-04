namespace SpawnWear.Bridge;

/// <summary>
/// Abstract transport between a Blazor app and the SpawnWear watch.
///
/// V1 has one impl: <see cref="Ble.BleTransport"/> (Web Bluetooth GATT).
/// Phase 7 adds <see cref="WebRtc.WebRtcTransport"/> (peer-to-peer data
/// channel; signaling happens over BLE so peers don't need to share a
/// network).
///
/// Bridge consumers don't pick the transport directly - they hand the
/// <see cref="BridgeClient"/> a transport and call typed methods on the
/// client. The client routes messages through whichever transport is
/// connected.
/// </summary>
public interface ITransport
{
    /// <summary>True when the underlying connection is established.</summary>
    bool IsConnected { get; }

    /// <summary>Best-effort human-readable identifier for the paired
    /// peer (e.g. <c>SW-OK-Tok</c> from a BLE advertising name).
    /// <c>null</c> when not connected or when the transport doesn't
    /// have a name to report.</summary>
    string? PeerName { get; }

    /// <summary>Fires when IsConnected flips. Bool argument is the new value.</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>Fires for every framed message received from the watch.
    /// Frame format is transport-defined; the BridgeClient interprets the
    /// payload bytes.</summary>
    event Action<TransportMessage>? MessageReceived;

    /// <summary>Connect to the watch. Browser-side this triggers the
    /// pairing UI on first call.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Send a framed message to the watch.</summary>
    Task SendAsync(TransportMessage message, CancellationToken ct = default);

    /// <summary>Force a one-shot read of every characteristic that
    /// supports Read, feeding the bytes back through
    /// <see cref="MessageReceived"/> as if a notify had just arrived.
    /// Useful right after Connect so the consumer sees current state
    /// without having to wait for the next firmware-pushed notify.
    /// Optional - transports that don't model "readable state"
    /// (WebRTC) implement this as a no-op.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Disconnect cleanly.</summary>
    Task DisconnectAsync();
}

/// <summary>
/// A framed message moving between the Blazor app and the watch. The
/// <see cref="ChannelId"/> identifies which logical surface this is for
/// (battery notify, IMU sample, debug log, app payload, etc.); the
/// payload is opaque bytes interpreted by both sides per the channel's
/// schema.
///
/// On BLE the channel maps to a specific GATT characteristic UUID. On
/// WebRTC it's a numeric channel-id in a single shared data channel.
/// </summary>
public readonly record struct TransportMessage(string ChannelId, byte[] Payload);
