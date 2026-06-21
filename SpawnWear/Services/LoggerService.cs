using System.Diagnostics;
using SpawnWear.AppContracts;

namespace SpawnWear.Services
{
    /// <summary>
    /// Phase 3 Logger system service. Replaces the throwaway DebugLogger shim.
    ///
    /// Every log line is:
    ///  1. kept in a small in-memory ring buffer (so a late-attaching debugger or the
    ///     Companion Console can backfill the recent history via <see cref="GetRecent"/>),
    ///  2. mirrored to Debug.WriteLine (the wire-protocol console on COM3/COM9), and
    ///  3. pushed to an optional <see cref="Sink"/> - wired at boot to the BLE debug-log
    ///     channel so log lines reach the companion app without the debugger attached.
    ///
    /// Implements <see cref="ILogger"/> so apps log through IServiceHost.GetLogger().
    /// Thread-safe: multiple FreeRTOS tasks (UI loop, BLE callbacks, WiFi) can log.
    /// nanoFramework-safe: fixed-size arrays, lock, no optional parameters, no LINQ.
    /// </summary>
    public class LoggerService : ILogger
    {
        /// <summary>A downstream sink for a formatted log line (e.g. BLE notify).</summary>
        public delegate void LogSink(string line);

        const int Capacity = 64;

        readonly string[] _ring = new string[Capacity];
        readonly object _lock = new object();
        int _next;   // index of the next slot to write
        int _count;  // number of valid entries (<= Capacity)

        /// <summary>Optional downstream sink. Null until wired (see Program.cs).
        /// Exceptions from the sink are swallowed so logging never throws.</summary>
        public LogSink Sink;

        public void Info(string message) { Write("[INFO] ", message); }
        public void Warn(string message) { Write("[WARN] ", message); }
        public void Error(string message) { Write("[ERROR] ", message); }

        void Write(string prefix, string message)
        {
            string line = prefix + (message == null ? "" : message);

            lock (_lock)
            {
                _ring[_next] = line;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
            }

            Debug.WriteLine(line);

            LogSink sink = Sink;
            if (sink != null)
            {
                try { sink(line); } catch { }
            }
        }

        /// <summary>
        /// Snapshot of the recent log lines, oldest first. Returns a fresh array each
        /// call so the caller can iterate without holding the lock.
        /// </summary>
        public string[] GetRecent()
        {
            lock (_lock)
            {
                string[] outp = new string[_count];
                int start = (_next - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                {
                    outp[i] = _ring[(start + i) % Capacity];
                }
                return outp;
            }
        }
    }
}
