# ESP32-S3 + nanoFramework WiFi: drop "ax" mode on the AP, lock to 20 MHz

When bringing up nanoFramework WiFi on an ESP32-S3 board, the first connect attempt against a modern home-router AP commonly fails with `WifiConnectionStatus.UnsupportedAuthenticationProtocol` or returns `false` from `WifiNetworkHelper.ConnectDhcp` with a vague helper status, **even when the SSID + password are correct**.

The fix is in the AP, not the watch.

## Symptoms

- Watch-side log shows `[WiFi] W2a - Connect returned status=5` (UnsupportedAuthenticationProtocol)
- With config saved via `Wireless80211Configuration.SaveConfiguration` first, the helper returns `status=4` (DateTimeAvailable in some enum versions, ConnectionFailed in others) and `ok=false`
- Both tightening (full credentials) and loosening (no password / wrong password) produce the same error - the chip never gets to the auth handshake, the failure is in capability negotiation

## Configurations that fail

- Modern home routers (MSI, ASUS, Netgear, etc.) running their default 2.4 GHz settings:
  - 802.11 mode: **b/g/n/ax mixed mode** (Wi-Fi 6)
  - Channel width: **auto 20/40 MHz**
  - WPA2-PSK + AES (the standard combo)

The AP is genuinely broadcasting a WPA2-PSK SSID, but it's also advertising WPA3-mixed-mode capability bits and HT40 channel width in its 802.11ax probe responses. ESP-IDF's station-mode capability negotiation appears to snag on those bits and refuse the auth.

## The fix

Switch the AP's 2.4 GHz radio to:
- **802.11 mode: b/g/n** (drop "ax")
- **Channel width: 20 MHz** (not auto 20/40)

Same SSID + password + WPA2-PSK + AES. Now the same `WifiNetworkHelper.ConnectDhcp(ssid, password, WifiReconnectionKind.Automatic, requiresDateTime: false, wifiAdapterId: 0, token)` call succeeds first try, the watch gets DHCP-assigned, and the in-app HTTP server binds + serves traffic.

## Reproduction history

- 2026-05-04 SDN2 (MSI router, 802.11 b/g/n/ax + auto 20/40 + WPA2-PSK + AES + Wi-Fi 6) refused to authenticate.
- After SSID was switched to b/g/n + 20 MHz, the same code connected first try.
- Burned ~2 hours on this before identifying it as a router issue.

## How to apply

When bringing up WiFi on a new ESP32-S3 board with nanoFramework, ALWAYS check the AP first. The MSI / ASUS / Netgear "AX" routers on default settings will fail; many other routers in legacy config will succeed. If a customer reports a non-working WiFi connection on a board that worked at the dev bench, **AP mode is the first thing to check**, NOT credentials.

This will surface again when SpawnWear ships - either we add an in-app diagnostic that explains the limitation, or we contribute a fix to `nanoFramework.System.Device.Wifi.dll` that advertises broader-compatibility station capabilities so it works on b/g/n/ax APs out of the box. ESP-IDF has a `wifi_ap_record_t` filter that may need adjustment in the binding.

## Reference connect call (known-good)

```csharp
var cts = new CancellationTokenSource(timeoutMs);
bool ok = WifiNetworkHelper.ConnectDhcp(
    ssid,
    password,
    WifiReconnectionKind.Automatic,
    requiresDateTime: false,
    wifiAdapterId: 0,
    token: cts.Token);
```

Note: `wifiAdapterId: 0` is required - on ESP32-S3 the WifiAdapter index is 0, not the conventional -1.
