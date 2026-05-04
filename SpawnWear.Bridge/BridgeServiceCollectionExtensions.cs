using Microsoft.Extensions.DependencyInjection;

namespace SpawnWear.Bridge;

/// <summary>
/// Convenience DI registration. Consumers add a single line in Program.cs:
///
/// <code>
/// builder.Services.AddSpawnWearBridge();
/// </code>
///
/// Registers <see cref="BridgeClient"/> as a scoped service (so each
/// browser tab gets its own connection state) and the BLE transport as
/// scoped under <see cref="ITransport"/>. Consumers that want WebRTC can
/// register an additional ITransport keyed off whatever discriminator
/// they prefer.
/// </summary>
public static class BridgeServiceCollectionExtensions
{
    public static IServiceCollection AddSpawnWearBridge(this IServiceCollection services)
    {
        services.AddScoped<Ble.BleTransport>();
        services.AddScoped<BridgeClient>(sp =>
        {
            var client = new BridgeClient();
            // Default to BLE transport; consumers can call UseTransportAsync to swap.
            var ble = sp.GetRequiredService<Ble.BleTransport>();
            _ = client.UseTransportAsync(ble);
            return client;
        });
        return services;
    }
}
