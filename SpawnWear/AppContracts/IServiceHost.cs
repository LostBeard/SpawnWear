namespace SpawnWear.AppContracts
{
    /// <summary>
    /// Single point through which screens / apps reach system services. The
    /// firmware constructs one ServiceHost at boot and hands it to every
    /// screen that wants to consume Power / RTC / WiFi / Log capabilities.
    ///
    /// Phase 8 (SD-card-loadable apps) will move this interface into a
    /// separate SpawnWear.AppContracts.nfproj NuGet package that both the
    /// firmware and external apps reference. Until then it lives in-tree
    /// so the firmware can use it without the inter-project reference dance.
    ///
    /// Generic dispatch (TryGet&lt;T&gt;) isn't supported on nanoFramework's
    /// CoreLib; the host exposes type-specific accessors instead.
    /// </summary>
    public interface IServiceHost
    {
        IPowerService GetPower();
        IRtcService GetRtc();
        IWifiService GetWifi();
        ILogger GetLogger();
    }
}
