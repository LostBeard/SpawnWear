namespace SpawnWear.Bridge.Tests;

/// <summary>
/// Test-only <see cref="ITransport"/> that lets a test push synthetic
/// <see cref="TransportMessage"/> values into a <see cref="BridgeClient"/>
/// to exercise the payload-decoder paths without standing up real BLE.
///
/// This is NOT a mock - the BridgeClient under test runs its actual
/// production OnMessageReceived code, decoding actual bytes per the
/// schema documented in firmware. We're only stubbing the wire that
/// would normally carry those bytes.
/// </summary>
public class FakeTransport : ITransport
{
    public bool IsConnected { get; private set; }

    public string? PeerName { get; set; } = "FakeWatch";

    public event Action<bool>? ConnectionChanged;
    public event Action<TransportMessage>? MessageReceived;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
        }
        return Task.CompletedTask;
    }

    public Task SendAsync(TransportMessage message, CancellationToken ct = default)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        RefreshCallCount++;
        return Task.CompletedTask;
    }

    public int RefreshCallCount { get; private set; }

    /// <summary>Test seam: set this to the watch's "advertised" pubkey
    /// before exercising pairing flows.</summary>
    public byte[]? FakeWatchPubKey { get; set; }

    /// <summary>Test seam: when set, ExchangePairingHandshakeAsync
    /// returns these bytes (simulates the watch's signed-response notify).
    /// When null, throws NotSupportedException.</summary>
    public byte[]? FakeHandshakeResponse { get; set; }

    /// <summary>Captured payload from the most recent
    /// ExchangePairingHandshakeAsync call.</summary>
    public byte[]? LastHandshakePayloadSent { get; private set; }

    public Task<byte[]> ReadWatchPublicKeyAsync(CancellationToken ct = default)
    {
        if (FakeWatchPubKey is null)
            return Task.FromException<byte[]>(new NotSupportedException("FakeWatchPubKey not set in test."));
        return Task.FromResult(FakeWatchPubKey);
    }

    public Task<byte[]> ExchangePairingHandshakeAsync(byte[] companionWritePayload, CancellationToken ct = default)
    {
        LastHandshakePayloadSent = companionWritePayload;
        if (FakeHandshakeResponse is null)
            return Task.FromException<byte[]>(new NotSupportedException("FakeHandshakeResponse not set in test."));
        return Task.FromResult(FakeHandshakeResponse);
    }

    /// <summary>Drive a TransportMessage into the BridgeClient as if it
    /// arrived over the wire.</summary>
    public void Push(TransportMessage message) => MessageReceived?.Invoke(message);

    public List<TransportMessage> SentMessages { get; } = new();
}
