namespace SpawnWear.AppContracts
{
    /// <summary>
    /// Read-only view of the AXP2101 PMIC state. Apps consume this to render
    /// battery levels, charging indicators, etc. without holding a direct
    /// driver reference.
    /// </summary>
    public interface IPowerService
    {
        /// <summary>0..100 percent. -1 if uncalibrated or driver missing.</summary>
        int BatteryPercent { get; }

        /// <summary>VBAT in millivolts. -1 if read failed.</summary>
        int BatteryMillivolts { get; }

        /// <summary>True when USB-C VBUS is present.</summary>
        bool IsVbusPresent { get; }
    }
}
