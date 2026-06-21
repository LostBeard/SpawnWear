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

        /// <summary>
        /// Disconnects the WiFi station (Settings "WIFI OFF"). Uses the helper's own
        /// Disconnect so its internal state resets - a raw WifiAdapter.Disconnect leaves
        /// WifiNetworkHelper thinking it is still connected and the next reconnect fails.
        /// The caller should stop the HTTP server first. Reconnect via <see cref="Reconnect"/>.
        /// </summary>
        /// <summary>
        /// Disconnects via the low-level WifiAdapter (not WifiNetworkHelper), so the
        /// toggle stays entirely at the adapter level - consistent with <see cref="Reconnect"/>,
        /// which re-connects the same adapter directly.
        /// </summary>
        public void Disconnect()
        {
            try
            {
                WifiAdapter[] adapters = WifiAdapter.FindAllAdapters();
                if (adapters != null && adapters.Length > 0) adapters[0].Disconnect();
                Debug.WriteLine("[WiFi] disconnected (adapter)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WiFi] disconnect EX " + ex.GetType().Name + ": " + ex.Message);
            }
            IsConnected = false;
            IpAddress = "";
        }

        /// <summary>
        /// Reconnects via the low-level WifiAdapter.Connect(ssid,...) overload, bypassing
        /// WifiNetworkHelper entirely (ConnectDhcp is one-shot and the helper's Reconnect
        /// returns false after a disconnect on ESP32). Polls for the DHCP-assigned IP.
        /// </summary>
        public bool Reconnect(int timeoutMs = 30000)
        {
            try
            {
                WifiAdapter[] adapters = WifiAdapter.FindAllAdapters();
                if (adapters == null || adapters.Length == 0)
                {
                    Debug.WriteLine("[WiFi] R1-fail - no adapter");
                    return false;
                }
                WifiAdapter adapter = adapters[0];

                Debug.WriteLine("[WiFi] R1 - WifiAdapter.Connect '" + WifiCredentials.Ssid + "'");
                WifiConnectionResult result = adapter.Connect(
                    WifiCredentials.Ssid, WifiReconnectionKind.Automatic, WifiCredentials.Password);
                Debug.WriteLine("[WiFi] R1a - status=" + result.ConnectionStatus);
                if (result.ConnectionStatus != WifiConnectionStatus.Success)
                {
                    return false;
                }

                // Connect only brings up the link; poll for the DHCP-assigned IPv4.
                long deadline = DateTime.UtcNow.Ticks + (long)timeoutMs * TimeSpan.TicksPerMillisecond;
                while (DateTime.UtcNow.Ticks < deadline)
                {
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
                            Debug.WriteLine("[WiFi] R3 - IP=" + IpAddress);
                            return true;
                        }
                    }
                    Thread.Sleep(500);
                }
                Debug.WriteLine("[WiFi] R3-fail - link up but no DHCP IP within timeout");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WiFi] reconnect EX " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        public bool Connect(int timeoutMs = 30000)
        {
            try
            {
                // Direct call - no Wireless80211Configuration prelude. Mirrors
                // NanoFrameTest1's working pattern from the same chip family.
                Debug.WriteLine("[WiFi] W1 - WifiNetworkHelper.ConnectDhcp '" + WifiCredentials.Ssid + "' (timeout=" + timeoutMs + "ms)");
                var cts = new CancellationTokenSource(timeoutMs);
                bool ok = WifiNetworkHelper.ConnectDhcp(
                    WifiCredentials.Ssid,
                    WifiCredentials.Password,
                    WifiReconnectionKind.Automatic,
                    requiresDateTime: false,
                    wifiAdapterId: 0,
                    token: cts.Token);
                Debug.WriteLine("[WiFi] W1a - ok=" + ok + " status=" + WifiNetworkHelper.Status +
                    (WifiNetworkHelper.HelperException != null ? (" ex=" + WifiNetworkHelper.HelperException.Message) : ""));

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
