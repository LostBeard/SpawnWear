using System;
using System.Diagnostics;
using System.Threading;
using SpawnWear.Drivers.Wifi;

namespace SpawnWear.Services
{
    /// <summary>
    /// System service that owns the watch's WebRTC transport to the paired Companion (the AI
    /// Assistant channel). It runs autonomously on its own thread: connect -> stay connected ->
    /// reconnect on drop, forever. No HTTP trigger - the watch maintains its own link.
    ///
    /// <para>This replaces the dev-time HTTP "/webrtc-connect" trigger. The actual connect/challenge/
    /// persist logic still lives in <see cref="SpawnWear.Program.WebRtcConnectRun"/> for now (it grew
    /// up there during the Phase 7 milestone work); extracting that whole body into this service is the
    /// next refactor. This service owns the LIFECYCLE (thread, gating, reconnect) which is the part
    /// the OS architecture cares about.</para>
    ///
    /// <para>Threading: libpeer's native interop needs a real call-stack; it runs fine on a dedicated
    /// thread (the old HTTP AcceptLoop was one). With the HTTP server retired there is no second thread
    /// touching libpeer, so there is no concurrency to crash on.</para>
    /// </summary>
    public class WebRtcTransportService
    {
        readonly PairingService _pairing;
        readonly WifiService _wifi;
        Thread _thread;
        bool _running;  // nanoFramework has no 'volatile'; plain bool read/write is atomic enough here

        /// <summary>The multiplexing channel bus shared by the OS and loadable apps. Survives across
        /// connect/reconnect cycles - subscribers register once and the service routes whenever a link
        /// is up. OS services use <c>Bus.Send/Subscribe</c>; apps get a scoped <c>Bus.OpenAppChannel</c>.</summary>
        public TransportBus Bus { get; } = new TransportBus();

        /// <summary>Reconnect cool-down after a connection ends or a failed attempt (ms).</summary>
        public int ReconnectDelayMs { get; set; } = 3000;

        /// <summary>Dev posture: when true the watch ALSO connects while UNPAIRED, announcing with its
        /// test/dev identity (room "SWclean0623pmRoom01x") so the console / a fresh peer can reach it.
        /// A paired watch always uses its real paired identity regardless. Production should set this
        /// false (require pairing) once the console's own BLE pairing exists.</summary>
        public bool AllowUnpaired { get; set; } = true;

        public WebRtcTransportService(PairingService pairing, WifiService wifi)
        {
            _pairing = pairing;
            _wifi = wifi;
        }

        /// <summary>Start the autonomous transport loop on its own thread. Idempotent.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop);
            _thread.Start();
            Debug.WriteLine("[WebRtcTransport] service started");
        }

        /// <summary>Request the loop to stop after the current attempt unwinds.</summary>
        public void Stop()
        {
            _running = false;
        }

        void Loop()
        {
            while (_running)
            {
                try
                {
                    // Only attempt when we can actually reach the hub AND have an identity to
                    // authenticate with. Otherwise idle until those become true.
                    bool wifiUp = _wifi != null && _wifi.IsConnected;
                    bool paired = _pairing != null && _pairing.IsPaired;
                    // Connect when paired (production), OR when unpaired + AllowUnpaired (dev: announce
                    // with the test identity so the console / a fresh peer can reach the watch).
                    if (wifiUp && (paired || AllowUnpaired))
                    {
                        // Blocks for the life of the connection: connect (re-announcing until the
                        // Companion is reachable) -> mutual challenge -> stay connected, pumping the
                        // channel Bus (drain sends, route inbound) until the peer disconnects, then
                        // returns here and we reconnect.
                        Program.WebRtcConnectRun(Bus);
                    }
                    else
                    {
                        Debug.WriteLine("[WebRtcTransport] idle (wifi=" + wifiUp + " paired=" + paired + ")");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WebRtcTransport] loop EX " + ex.Message);
                }

                // Cool-down before the next connect attempt so a missing peer / hub doesn't spin.
                for (int slept = 0; slept < ReconnectDelayMs && _running; slept += 250)
                    Thread.Sleep(250);
            }
            Debug.WriteLine("[WebRtcTransport] service stopped");
        }
    }
}
