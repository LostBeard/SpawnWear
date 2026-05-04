using System;
using System.Device.Wifi;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;
using nanoFramework.Networking;

namespace SpawnWear
{
    /// <summary>
    /// BLE service for WiFi provisioning and the host of all SpawnWear sub-services
    /// (debug console, watch profile). nanoFramework on ESP32 advertises one
    /// <see cref="GattServiceProvider"/> reliably, so every characteristic the watch
    /// exposes is parented under this provider.
    ///
    /// Companion app workflow:
    /// 1. Read current WiFi status and IP address
    /// 2. Trigger a network scan and receive available SSIDs
    /// 3. Send WiFi credentials (SSID + password)
    /// 4. Send connect/disconnect/forget commands
    /// </summary>
    public class WifiConfigService
    {
        GattLocalCharacteristic _statusChar;
        GattLocalCharacteristic _scanChar;
        GattLocalCharacteristic _credentialsChar;
        GattLocalCharacteristic _commandChar;

        bool _statusHasSubscribers;
        bool _scanHasSubscribers;

        string _storedSsid;
        string _storedPassword;

        readonly DebugConsoleService _debug;
        readonly WatchProfileService _profile;
        readonly PairingService _pairing;

        public GattServiceProvider ServiceProvider { get; private set; }

        // WiFi status constants
        const byte StatusDisconnected = 0;
        const byte StatusConnecting = 1;
        const byte StatusConnected = 2;
        const byte StatusFailed = 3;

        byte _currentStatus = StatusDisconnected;
        string _currentIp = "";

        public WifiConfigService(DebugConsoleService debug, WatchProfileService profile, PairingService pairing)
        {
            _debug = debug;
            _profile = profile;
            _pairing = pairing;
        }

        /// <summary>
        /// Initialize WiFi characteristics only (no WatchProfile, no DebugConsole).
        /// Use when the BLE host can't fit the full ~10-characteristic layout - currently
        /// the case on the QSPI nanoCLR build, where adding all helper services hits
        /// OutOfMemoryException at GattLocalCharacteristic::.ctor.
        /// </summary>
        public bool InitializeWifiOnly()
        {
            return InitializeInternal(false);
        }

        public bool Initialize()
        {
            return InitializeInternal(true);
        }

        bool InitializeInternal(bool attachHelpers)
        {
            var result = GattServiceProvider.Create(BleUuids.WifiServiceUuid);
            if (result.Error != BluetoothError.Success)
            {
                _debug.Log("[WiFi] Failed to create service: " + result.Error);
                return false;
            }

            ServiceProvider = result.ServiceProvider;
            var service = ServiceProvider.Service;

            // WiFi Status — read + notify (status byte + IP string)
            var statusParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                UserDescription = "WiFi Status"
            };
            var statusResult = service.CreateCharacteristic(BleUuids.WifiStatusUuid, statusParams);
            if (statusResult.Error != BluetoothError.Success) return false;
            _statusChar = statusResult.Characteristic;
            _statusChar.ReadRequested += OnStatusReadRequested;
            _statusChar.SubscribedClientsChanged += (s, a) => _statusHasSubscribers = s.SubscribedClients.Length > 0;

            // WiFi Scan — write to trigger, notify with results
            var scanParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse | GattCharacteristicProperties.Notify,
                UserDescription = "WiFi Scan"
            };
            var scanResult = service.CreateCharacteristic(BleUuids.WifiScanUuid, scanParams);
            if (scanResult.Error != BluetoothError.Success) return false;
            _scanChar = scanResult.Characteristic;
            _scanChar.WriteRequested += OnScanWriteRequested;
            _scanChar.SubscribedClientsChanged += (s, a) => _scanHasSubscribers = s.SubscribedClients.Length > 0;

            // WiFi Credentials — write only (SSID\nPassword format)
            var credParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write,
                UserDescription = "WiFi Credentials"
            };
            var credResult = service.CreateCharacteristic(BleUuids.WifiCredentialsUuid, credParams);
            if (credResult.Error != BluetoothError.Success) return false;
            _credentialsChar = credResult.Characteristic;
            _credentialsChar.WriteRequested += OnCredentialsWriteRequested;

            // WiFi Command — write only (connect, disconnect, forget)
            var cmdParams = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                UserDescription = "WiFi Command"
            };
            var cmdResult = service.CreateCharacteristic(BleUuids.WifiCommandUuid, cmdParams);
            if (cmdResult.Error != BluetoothError.Success) return false;
            _commandChar = cmdResult.Characteristic;
            _commandChar.WriteRequested += OnCommandWriteRequested;

            // Attach watch profile + debug + pairing characteristics only when caller asked
            // for them AND the helper instances were supplied. All three are skipped under
            // InitializeWifiOnly().
            if (attachHelpers)
            {
                if (_profile != null && !_profile.Initialize(service))
                {
                    return false;
                }
                if (_debug != null && !_debug.Initialize(service))
                {
                    return false;
                }
                if (_pairing != null && !_pairing.Initialize(service))
                {
                    return false;
                }
                if (_debug != null) _debug.Log("[WiFi] Service initialized (full)");
            }
            else
            {
                Debug.WriteLine("[WiFi] Service initialized (wifi-only, helpers skipped)");
            }

            return true;
        }

        void OnStatusReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
        {
            var request = args.GetRequest();
            request.RespondWithValue(BuildStatusBuffer());
        }

        void OnScanWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var request = args.GetRequest();
            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            // Run scan on a background thread so we don't block BLE
            new Thread(() => PerformWifiScan()).Start();
        }

        void OnCredentialsWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var request = args.GetRequest();
            var reader = DataReader.FromBuffer(request.Value);
            var payload = reader.ReadString(request.Value.Length);

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            // Format: SSID\nPassword
            var separatorIndex = payload.IndexOf('\n');
            if (separatorIndex > 0)
            {
                _storedSsid = payload.Substring(0, separatorIndex);
                _storedPassword = payload.Substring(separatorIndex + 1);
                _debug.Log("[WiFi] Credentials stored for SSID: " + _storedSsid);
            }
            else
            {
                _debug.Log("[WiFi] Invalid credentials format. Expected: SSID\\nPassword");
            }
        }

        void OnCommandWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var request = args.GetRequest();
            var reader = DataReader.FromBuffer(request.Value);
            var command = reader.ReadByte();

            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }

            switch (command)
            {
                case BleUuids.WifiCmdConnect:
                    new Thread(() => ConnectToWifi()).Start();
                    break;

                case BleUuids.WifiCmdDisconnect:
                    DisconnectWifi();
                    break;

                case BleUuids.WifiCmdForget:
                    ForgetWifi();
                    break;

                default:
                    _debug.Log("[WiFi] Unknown command: 0x" + command.ToString("X2"));
                    break;
            }
        }

        void PerformWifiScan()
        {
            _debug.Log("[WiFi] Scanning for networks...");

            try
            {
                var adapter = WifiAdapter.FindAllAdapters()[0];
                adapter.ScanAsync();

                // Build result string: one SSID per line with signal strength.
                // Format: "SSID|RSSI\nSSID2|RSSI2\n..."
                var report = adapter.NetworkReport;
                var sb = new StringBuilder();

                foreach (var network in report.AvailableNetworks)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(network.Ssid);
                    sb.Append('|');
                    sb.Append(network.NetworkRssiInDecibelMilliwatts.ToString());
                }

                var resultStr = sb.ToString();
                _debug.Log("[WiFi] Scan complete. Found " + report.AvailableNetworks.Length + " networks");

                if (_scanHasSubscribers)
                {
                    var writer = new DataWriter();
                    writer.WriteString(resultStr);
                    _scanChar.NotifyValue(writer.DetachBuffer());
                }
            }
            catch (Exception ex)
            {
                _debug.Log("[WiFi] Scan failed: " + ex.Message);
            }
        }

        void ConnectToWifi()
        {
            if (_storedSsid == null || _storedPassword == null)
            {
                _debug.Log("[WiFi] No credentials stored. Send credentials first.");
                return;
            }

            UpdateStatus(StatusConnecting, "");
            _debug.Log("[WiFi] Connecting to: " + _storedSsid);

            try
            {
                var cts = new CancellationTokenSource(15000);
                var wifiResult = WifiNetworkHelper.ConnectDhcp(
                    _storedSsid,
                    _storedPassword,
                    WifiReconnectionKind.Automatic,
                    requiresDateTime: false,
                    wifiAdapterId: 0,
                    token: cts.Token);

                if (wifiResult)
                {
                    var ip = GetLocalIpAddress();
                    UpdateStatus(StatusConnected, ip);
                    _debug.Log("[WiFi] Connected! IP: " + ip);
                }
                else
                {
                    UpdateStatus(StatusFailed, "");
                    _debug.Log("[WiFi] Connection failed");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus(StatusFailed, "");
                _debug.Log("[WiFi] Connection error: " + ex.Message);
            }
        }

        void DisconnectWifi()
        {
            _debug.Log("[WiFi] Disconnecting...");
            WifiNetworkHelper.Disconnect();
            UpdateStatus(StatusDisconnected, "");
        }

        void ForgetWifi()
        {
            _debug.Log("[WiFi] Forgetting credentials");
            _storedSsid = null;
            _storedPassword = null;
            WifiNetworkHelper.Disconnect();
            UpdateStatus(StatusDisconnected, "");
        }

        void UpdateStatus(byte status, string ip)
        {
            _currentStatus = status;
            _currentIp = ip ?? "";

            if (_statusHasSubscribers)
            {
                _statusChar.NotifyValue(BuildStatusBuffer());
            }
        }

        Buffer BuildStatusBuffer()
        {
            // Format: [status_byte][ip_string]
            var writer = new DataWriter();
            writer.WriteByte(_currentStatus);
            writer.WriteString(_currentIp);
            return writer.DetachBuffer();
        }

        static string GetLocalIpAddress()
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.IPv4Address != null && ni.IPv4Address != "0.0.0.0")
                {
                    return ni.IPv4Address;
                }
            }
            return "0.0.0.0";
        }
    }
}
