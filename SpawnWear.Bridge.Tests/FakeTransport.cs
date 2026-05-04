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

    /// <summary>Drive a TransportMessage into the BridgeClient as if it
    /// arrived over the wire.</summary>
    public void Push(TransportMessage message) => MessageReceived?.Invoke(message);

    public List<TransportMessage> SentMessages { get; } = new();
}
