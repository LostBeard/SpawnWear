using nanoFramework.Networking;
using SpawnWear.Config;
using System;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;

namespace SpawnWear.Drivers.Wifi
{
    /// <summary>
    /// Brings the WiFi station up using credentials from
    /// <see cref="WifiCredentials"/>. Phase 1 of network connectivity - no
    /// provisioning UI, no scanning, no fallback. Once <see cref="Connect"/>
    /// returns true, <see cref="IpAddress"/> is the watch's local-network
    /// IPv4 address that callers can route HTTP / WebSocket / WebRTC traffic
    /// against.
    ///
    /// Connect runs synchronously on the calling thread - typically Main()
    /// before the EventLoop starts. Future revisions can move it to a
    /// background thread + a "WiFi connecting..." status indicator on the
    /// watch face.
    /// </summary>
    public class WifiService
    {
        public bool IsConnected { get; private set; }
        public string IpAddress { get; private set; } = "";
        public string Ssid { get; private set; } = "";

        public bool Connect(int timeoutMs = 30000)
        {
            try
            {
                // Give the WiFi stack a moment to come up after boot. Without this,
                // early WifiAdapter.Connect calls can fail with vague status codes
                // because the underlying esp_wifi_start hasn't initialized the
                // station-mode context yet.
                Thread.Sleep(1500);

                // Persist the SSID + password + auth/encryption type in
                // Wireless80211Configuration FIRST. WifiNetworkHelper assumes
                // a stored configuration exists; without one it throws
                // InvalidOperationException from inside ScanAndConnectDhcp.
                Debug.WriteLine("[WiFi] W1 - Storing Wireless80211Configuration for '" + WifiCredentials.Ssid + "'");
                var configs = Wireless80211Configuration.GetAllWireless80211Configurations();
                if (configs == null || configs.Length == 0)
                {
                    Debug.WriteLine("[WiFi] W1-fail - no Wireless80211Configuration slots");
                    return false;
                }
                var cfg = configs[0];
                cfg.Ssid = WifiCredentials.Ssid;
                cfg.Password = WifiCredentials.Password;
                cfg.Authentication = AuthenticationType.WPA2;
                cfg.Encryption = EncryptionType.WPA2_PSK;
                cfg.Radio = RadioType.NotSpecified;
                cfg.SaveConfiguration();
                Debug.WriteLine("[WiFi] W1a - cfg saved auth=" + cfg.Authentication + " enc=" + cfg.Encryption);

                Debug.WriteLine("[WiFi] W2 - WifiAdapter.Connect direct (post-config-save)");
                var adapter = WifiAdapter.FindAllAdapters()[0];
                adapter.Disconnect();
                Thread.Sleep(500);
                // Try ScanAsync first to "warm up" the radio - some bindings need
                // an initial scan before Connect succeeds.
                try
                {
                    Debug.WriteLine("[WiFi] W2a - ScanAsync warm-up");
                    adapter.ScanAsync();
                    Thread.Sleep(2500);
                    var rep = adapter.NetworkReport;
                    int n = (rep != null && rep.AvailableNetworks != null) ? rep.AvailableNetworks.Length : 0;
                    Debug.WriteLine("[WiFi] W2b - scan saw " + n + " networks");
                    bool foundOurs = false;
                    if (rep != null && rep.AvailableNetworks != null)
                    {
                        for (int i = 0; i < rep.AvailableNetworks.Length; i++)
                        {
                            var nw = rep.AvailableNetworks[i];
                            if (nw.Ssid == WifiCredentials.Ssid)
                            {
                                foundOurs = true;
                                Debug.WriteLine("[WiFi]    target rssi=" + nw.NetworkRssiInDecibelMilliwatts);
                            }
                        }
                    }
                    Debug.WriteLine("[WiFi] W2c - target SSID found=" + foundOurs);
                }
                catch (Exception scanEx)
                {
                    Debug.WriteLine("[WiFi] W2-scan EX " + scanEx.GetType().Name + ": " + scanEx.Message);
                }
                var connectResult = adapter.Connect(WifiCredentials.Ssid, WifiReconnectionKind.Automatic, WifiCredentials.Password);
                Debug.WriteLine("[WiFi] W2d - Connect returned status=" + connectResult.ConnectionStatus);
                bool ok = connectResult.ConnectionStatus == WifiConnectionStatus.Success;

                if (!ok) return false;

                // Read assigned IP from the wireless NIC.
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    var nic = nics[i];
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                        !string.IsNullOrEmpty(nic.IPv4Address) &&
                        nic.IPv4Address != "0.0.0.0")
                    {
                        IpAddress = nic.IPv4Address;
                        Ssid = WifiCredentials.Ssid;
                        IsConnected = true;
                        Debug.WriteLine("[WiFi] W3 - IP=" + IpAddress);
                        return true;
                    }
                }

                Debug.WriteLine("[WiFi] W3-fail - no wireless NIC with IP");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WiFi] EX " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }
}
