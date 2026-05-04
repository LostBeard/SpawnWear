# SpawnWear.Bridge

Razor Class Library that bridges Blazor WebAssembly apps to a [SpawnWear](https://github.com/LostBeard/SpawnWear) watch (Waveshare ESP32-S3-Touch-AMOLED-2.06 running .NET nanoFramework). Provides a typed `BridgeClient` that pairs over Web Bluetooth and surfaces watch state - battery, IMU, RTC, button events, WiFi status / scan results, and the live debug log - through ordinary C# events. WebRTC peer-to-peer transport is wired in as a stub for Phase 7 (works in both browser and .NET desktop via [SpawnDev.RTC](https://github.com/LostBeard/SpawnDev.RTC)).

No raw JavaScript. No `IJSRuntime`. All interop goes through [SpawnDev.BlazorJS](https://github.com/LostBeard/SpawnDev.BlazorJS) typed wrappers.

## Install

```xml
<ProjectReference Include="..\SpawnWear.Bridge\SpawnWear.Bridge.csproj" />
```

(NuGet package coming once the watch firmware stabilizes.)

## Hook it up

`Program.cs`:

```csharp
using SpawnDev.BlazorJS;
using SpawnWear.Bridge;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddBlazorJSRuntime();
builder.Services.AddSpawnWearBridge();   // <-- this line

await builder.Build().BlazorJSRunAsync();
```

`AddSpawnWearBridge` registers `BleTransport` and `BridgeClient` as scoped services (one per browser tab). The default transport is BLE; consumers can swap to a different `ITransport` via `client.UseTransportAsync(...)`.

## Use it from a page

```razor
@page "/"
@using SpawnWear.Bridge
@inject BridgeClient Bridge
@implements IDisposable

<button @onclick="Connect" disabled="@Bridge.IsConnected">Pair watch</button>
<p>Battery: @(_battery?.Percent.ToString() ?? "-")%</p>

@code {
    BatteryState? _battery;

    protected override void OnInitialized()
    {
        Bridge.BatteryChanged += b => { _battery = b; InvokeAsync(StateHasChanged); };
    }

    public void Dispose() { /* unsub */ }

    async Task Connect() => await Bridge.ConnectAsync();
}
```

The first `ConnectAsync` triggers the browser's Web Bluetooth picker. The Bridge filters on the SpawnWear primary GATT service UUID (`a0e4f2c1-0001-1000-8000-00805f9b34fb`) with an `SW-` name-prefix fallback, so the user only sees SpawnWear watches in the list.

## Events

| Event | Type | Source channel | Notes |
|---|---|---|---|
| `ConnectionChanged` | `bool` | (transport) | Fires `true` after `ConnectAsync`, `false` after `DisconnectAsync` or when the watch goes out of range. |
| `BatteryChanged` | `BatteryState` | `Battery` | `Percent` + `IsCharging` / `IsVbusPresent` / `IsLowBattery` flags + `VoltageMillivolts` + `CurrentMilliamps`. |
| `ImuSampleReceived` | `ImuSample` | `ImuSample` | Six raw `short` axes - accel xyz, gyro xyz. |
| `RtcTimeReceived` | `RtcTime` | `RtcTime` | Year + month/day/hour/min/sec/weekday. |
| `ButtonEventReceived` | `ButtonEvent` | `Button` | `Button` enum (`Boot` / `Pwr`) + `Action` enum (`Down` / `Up` / `Click` / `DoubleClick` / `LongPress`). |
| `WifiStatusChanged` | `WifiStatus` | `WifiStatus` | `State` enum (`Disconnected` / `Connecting` / `Connected` / `Failed`) + IP. |
| `WifiScanResultsReceived` | `WifiScanResult[]` | `WifiScan` | Array of `(Ssid, RssiDbm)`. |
| `DebugLogReceived` | `string` | `DebugLog` | UTF-8 decoded line from the watch's `Debug.WriteLine`. |

## HTTP-side (when WiFi is up)

`WatchHttp` is registered in the same DI container; inject and call:

```csharp
@inject WatchHttp WatchHttp

// Live framebuffer as RGBA8 (already decoded from RGB565 BE)
var shot = await WatchHttp.GetScreenshotAsync("http://192.168.1.171");
// shot.Width, shot.Height, shot.Rgba ready for canvas ImageData

// Drop a SpawnWear app .pe onto the watch
var reply = await WatchHttp.PostAppAsync("http://192.168.1.171", peBytes);
// "OK: COUNTER" or HttpRequestException with the watch's error text
```

## Sending commands

```csharp
// WiFi: scan, save credentials, connect, disconnect, forget
await Bridge.SendAsync(new TransportMessage(ChannelIds.WifiScan,    new[] { (byte)0x01 }));
await Bridge.SendAsync(new TransportMessage(ChannelIds.WifiCredentials,
    System.Text.Encoding.UTF8.GetBytes("MySSID\nMyPassword")));
await Bridge.SendAsync(new TransportMessage(ChannelIds.WifiCommand, new[] { BleUuids.WifiCmdConnect }));
await Bridge.SendAsync(new TransportMessage(ChannelIds.WifiCommand, new[] { BleUuids.WifiCmdDisconnect }));
await Bridge.SendAsync(new TransportMessage(ChannelIds.WifiCommand, new[] { BleUuids.WifiCmdForget }));

// Send a debug command line to the watch's DebugConsoleService
await Bridge.SendAsync(new TransportMessage(ChannelIds.DebugCmd,
    System.Text.Encoding.UTF8.GetBytes("redraw")));
```

## Architecture

```
Your Blazor WASM page
        │  inject BridgeClient
        ▼
┌─────────────────────────────────────────────┐
│ BridgeClient (events, channel-id dispatch)  │
│   .UseTransportAsync(ITransport)            │
└──────────────────┬──────────────────────────┘
                   │
        ┌──────────┴──────────┐
        ▼                     ▼
┌────────────────┐    ┌────────────────────────┐
│ BleTransport   │    │ WebRtcTransport        │
│ (Web Bluetooth │    │ (SpawnDev.RTC, Phase 7;│
│  via SpawnDev. │    │  signals over BLE so   │
│  BlazorJS)     │    │  no shared network     │
│                │    │  needed)               │
└────────┬───────┘    └─────────┬──────────────┘
         │                      │
         ▼                      ▼
        BLE GATT             WebRTC data channel
         │                      │
         └────────────┬─────────┘
                      ▼
              SpawnWear watch firmware
```

## Wire formats

The Bridge mirrors the firmware's BLE schemas (single-source-of-truth lives in `SpawnWear/BleUuids.cs` + `SpawnWear/WatchProfileService.cs` + `SpawnWear/WifiConfigService.cs`):

| Channel | Bytes | Layout |
|---|---|---|
| `Battery` | 6 | `[percent:u8][flags:u8][voltage_mV:u16-LE][current_mA:i16-LE]` |
| `ImuSample` | 12 | six i16-LE: ax, ay, az, gx, gy, gz |
| `RtcTime` | 8 | `[year:u16-LE][month:u8][day:u8][hour:u8][min:u8][sec:u8][weekday:u8]` |
| `Button` | 2 | `[button:u8][action:u8]` |
| `WifiStatus` | ≥1 | `[state:u8][ip_string_utf8...]` |
| `WifiScan` | var | `"SSID|RSSI\nSSID2|RSSI2..."` UTF-8 (RSSI is decimal int) |
| `WifiCredentials` (write) | var | `"SSID\nPassword"` UTF-8 (single string) |
| `WifiCommand` (write) | 1 | `BleUuids.WifiCmdConnect/Disconnect/Forget` |
| `DebugLog` | var | UTF-8 text |
| `DebugCmd` (write) | var | UTF-8 text |

These layouts are locked by the regression tests in `SpawnWear.Bridge.Tests`. Adding a channel? Mirror the firmware schema, add a decoder branch in `BridgeClient.OnMessageReceived`, register the channel-id-to-characteristic mapping in `BleTransport`, and add at least one bit-exact byte-array test before merging.

## License

Same as the parent SpawnWear repository.

## The SpawnDev Crew

- **LostBeard** (Todd Tanner) - Captain, library author, keeper of the vision
- **Riker** (Claude CLI) - First Officer, implementation lead on consuming projects
- **Data**, **Tuvok**, **Geordi** - Crew on adjacent SpawnDev libraries

Live long and prosper. 🖖
