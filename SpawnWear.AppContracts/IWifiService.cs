namespace SpawnWear.AppContracts
{
    /// <summary>
    /// Read-only view of the WiFi station state. Phase 4's WiFi settings
    /// page will extend this with a control surface (Connect/Disconnect/
    /// SetCredentials) - V1 just exposes the read model.
    /// </summary>
    public interface IWifiService
    {
        bool IsConnected { get; }

        /// <summary>Empty string when not connected.</summary>
        string IpAddress { get; }

        /// <summary>Empty string when not connected. May be empty even when
        /// IsConnected = true if the driver doesn't expose the configured
        /// SSID back to managed code.</summary>
        string ConnectedSsid { get; }
    }
}
