using System;
using System.Threading;

namespace SpawnWear.Services
{
    /// <summary>
    /// Single-threaded event-driven main loop, modeled on the Rust port's
    /// <c>select3(timer, touch_int, button_int)</c> pattern from
    /// waveshare-watch-rs. The loop blocks on an <see cref="AutoResetEvent"/>
    /// with a state-dependent timeout - the CPU spends most of its life in
    /// FreeRTOS tickless-idle (<c>CONFIG_FREERTOS_USE_TICKLESS_IDLE=y</c>)
    /// while the wait is pending.
    ///
    /// External wake sources (touch INT, button INT, RTC alarm, BLE event)
    /// call <see cref="Wake"/> from their interrupt or callback context to
    /// fire the wait early.
    ///
    /// Power model:
    ///   * Watchface (active, 1 Hz refresh) → 1000 ms tick budget
    ///   * Watchface with finger held       → 16 ms (60 Hz, smooth response)
    ///   * Display sleep (Phase 2)          → 30,000 ms (housekeeping only)
    ///
    /// Inspired by waveshare-watch-rs's main.rs:603 select3 main loop.
    /// </summary>
    public class EventLoop
    {
        /// <summary>
        /// Caller-supplied delegate invoked on every wake. Returns the desired
        /// next-tick timeout in milliseconds (the loop applies it as the next
        /// <see cref="AutoResetEvent.WaitOne"/> deadline). Returning 0 or a
        /// negative value yields immediately for a tight repaint.
        /// </summary>
        public delegate int TickHandler(WakeReason reason);

        public enum WakeReason
        {
            InitialPaint,
            Timeout,
            ExternalSignal,
        }

        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly TickHandler _onTick;
        private bool _running;

        public EventLoop(TickHandler onTick)
        {
            _onTick = onTick ?? throw new ArgumentNullException();
        }

        /// <summary>
        /// Signals the loop to wake on the next available context. Safe to call
        /// from interrupt / I2C-callback / timer-callback threads.
        /// </summary>
        public void Wake()
        {
            _signal.Set();
        }

        /// <summary>
        /// Stops the loop after the next iteration completes.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _signal.Set();
        }

        /// <summary>
        /// Runs the loop on the calling thread. Blocks until <see cref="Stop"/>.
        /// First iteration fires immediately with <see cref="WakeReason.InitialPaint"/>.
        /// </summary>
        public void Run()
        {
            _running = true;
            int nextTimeoutMs = _onTick(WakeReason.InitialPaint);

            while (_running)
            {
                if (nextTimeoutMs < 0) nextTimeoutMs = Timeout.Infinite;
                bool signaled = _signal.WaitOne(nextTimeoutMs, false);
                if (!_running) break;

                WakeReason reason = signaled ? WakeReason.ExternalSignal : WakeReason.Timeout;
                nextTimeoutMs = _onTick(reason);
            }
        }
    }
}
