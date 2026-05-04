using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnWear.Bridge;
using SpawnWear.Companion;
using SpawnWear.Companion.Services;

// Print build timestamp on startup so the running build can be verified at a
// glance via DevTools console - matches the pattern across every other SpawnDev
// Blazor WASM app. Same value is exposed via BuildInfo.Timestamp for in-page UI.
Console.WriteLine($"SpawnWear.Companion build {BuildInfo.Timestamp}");

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddBlazorJSRuntime();
builder.Services.AddSpawnWearBridge();
builder.Services.AddPlatformCrypto();   // SpawnDev.BlazorJS.Cryptography - browser-side IPortableCrypto
builder.Services.AddScoped<WatchPrefs>();
builder.Services.AddScoped<SpawnWear.Bridge.Pairing.IPairingStore, LocalStoragePairingStore>();

await builder.Build().BlazorJSRunAsync();
