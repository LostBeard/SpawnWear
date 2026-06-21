namespace SpawnWear.AppContracts
{
    /// <summary>
    /// Read-only view of the PCF85063 RTC. Apps consume this for current
    /// wall-clock time without holding a direct driver reference.
    /// </summary>
    public interface IRtcService
    {
        /// <summary>True when the chip's oscillator-stop flag is clear.
        /// When false, the values returned by Hour/Minute/etc are stale
        /// uptime-derived fallbacks.</summary>
        bool IsValid { get; }

        int Year { get; }
        int Month { get; }
        int Day { get; }
        int Hour { get; }
        int Minute { get; }
        int Second { get; }

        /// <summary>0=Sunday .. 6=Saturday (matches DateTime.DayOfWeek).</summary>
        int Weekday { get; }
    }
}
