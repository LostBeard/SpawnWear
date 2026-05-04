using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnWear.Bridge;
using SpawnWear.Companion;
using SpawnWear.Companion.Services;

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
