using System;
using System.Collections;
using System.Diagnostics;
using System.Text;

namespace SpawnWear.Services
{
    /// <summary>Handler for an inbound message on a transport channel. Receives the FULL channel id
    /// (e.g. "battery" or "app.dice.score") and the raw payload bytes.</summary>
    public delegate void TransportChannelHandler(string channelId, byte[] payload);

    /// <summary>
    /// A namespaced, app-scoped view of the <see cref="TransportBus"/>. The app host hands one of
    /// these to each loadable app via <see cref="TransportBus.OpenAppChannel"/>. Every send/subscribe
    /// is FORCED into the app's own <c>app.&lt;appId&gt;.*</c> namespace, so an app physically cannot
    /// send to or read a system channel (e.g. "battery") or another app's channels. Closing it (on app
    /// unload) unsubscribes everything the app registered - no leaked handlers, no zombie channels.
    /// </summary>
    public interface IAppChannel
    {
        /// <summary>Send on this app's channel <c>app.&lt;appId&gt;.&lt;name&gt;</c>.</summary>
        void Send(string name, byte[] payload);

        /// <summary>Subscribe to inbound messages on <c>app.&lt;appId&gt;.&lt;name&gt;</c>.</summary>
        void OnMessage(string name, TransportChannelHandler handler);

        /// <summary>Unsubscribe everything this app registered. Called by the app host on unload.</summary>
        void Close();
    }

    /// <summary>
    /// The transport multiplexing bus. ONE authenticated WebRTC link carries many logical channels,
    /// keyed by the channel id in each <c>WebRtcDataFraming</c> frame
    /// (<c>[channelIdLen:u8][channelId][payloadLen:u16-LE][payload]</c>). Both the OS (system channels:
    /// "battery", "imu", "rtc", "log", ...) and loadable apps (<c>app.&lt;appId&gt;.*</c> via
    /// <see cref="IAppChannel"/>) send/receive through here without colliding, spoofing, or crashing
    /// each other:
    /// <list type="bullet">
    ///   <item>Sends are enqueued (thread-safe) and drained by the transport pump loop - callers never
    ///         touch the radio directly.</item>
    ///   <item>Inbound frames are parsed and routed to the registered channel handler; a throwing
    ///         handler is caught so one bad app/service can't take down the pump or the others.</item>
    ///   <item>Apps are confined to their <c>app.&lt;appId&gt;.*</c> prefix by <see cref="IAppChannel"/>.</item>
    /// </list>
    /// The bus is passive (no thread of its own); <see cref="WebRtcTransportService"/> owns the
    /// connection thread and drives <see cref="DequeueSend"/> / <see cref="RouteReceived"/>.
    /// </summary>
    public class TransportBus
    {
        // Channel-id prefix every app is confined to. The OS owns all non-"app." channel ids.
        public const string AppPrefix = "app.";

        readonly Queue _sendQueue = new Queue();   // byte[] frames ready for the wire
        readonly object _sendLock = new object();

        readonly Hashtable _handlers = new Hashtable(); // channelId(string) -> TransportChannelHandler
        readonly object _handlerLock = new object();

        /// <summary>True while a live, verified connection is draining the queue. Set by the service.</summary>
        public bool IsConnected { get; set; }

        // ---- send side (any thread: OS services, apps) ----

        /// <summary>Enqueue a message for sending on a SYSTEM channel (reserved names). Apps must use
        /// <see cref="IAppChannel"/> instead. Drops silently if not connected (telemetry is best-effort).</summary>
        public void Send(string channelId, byte[] payload)
        {
            if (!IsConnected) return;
            byte[] frame = Frame(channelId, payload);
            if (frame == null) return;
            lock (_sendLock)
            {
                // Bound the queue so a stalled link or a flooding sender can't exhaust RAM.
                if (_sendQueue.Count >= MaxQueuedFrames)
                    _sendQueue.Dequeue(); // drop oldest (telemetry is fine to drop; newest wins)
                _sendQueue.Enqueue(frame);
            }
        }

        const int MaxQueuedFrames = 64;

        /// <summary>Pump-loop hook: pull the next framed message to write to the data channel, or null.</summary>
        public byte[] DequeueSend()
        {
            lock (_sendLock)
            {
                return _sendQueue.Count > 0 ? (byte[])_sendQueue.Dequeue() : null;
            }
        }

        /// <summary>Drop any queued sends (called on disconnect so stale frames don't leak into the
        /// next connection).</summary>
        public void ClearSendQueue()
        {
            lock (_sendLock) { _sendQueue.Clear(); }
        }

        // ---- receive side (registration: any thread; routing: pump loop) ----

        /// <summary>Register a handler for a SYSTEM channel. Apps use <see cref="IAppChannel.OnMessage"/>.</summary>
        public void Subscribe(string channelId, TransportChannelHandler handler)
        {
            lock (_handlerLock) { _handlers[channelId] = handler; }
        }

        public void Unsubscribe(string channelId)
        {
            lock (_handlerLock) { _handlers.Remove(channelId); }
        }

        /// <summary>Pump-loop hook: parse one inbound WebRtcDataFraming frame and route it to its
        /// channel handler. A malformed frame is ignored (never tears down the link); a throwing
        /// handler is caught and logged (isolation).</summary>
        public void RouteReceived(byte[] buf, int len)
        {
            if (buf == null || len < 3) return;
            int cidLen = buf[0];
            if (cidLen <= 0 || len < 1 + cidLen + 2) return;
            string cid = new string(Encoding.UTF8.GetChars(buf, 1, cidLen));
            int p = 1 + cidLen;
            int plen = buf[p] | (buf[p + 1] << 8);
            if (len < p + 2 + plen) return;
            byte[] payload = new byte[plen];
            if (plen > 0) Array.Copy(buf, p + 2, payload, 0, plen);

            TransportChannelHandler handler;
            lock (_handlerLock) { handler = (TransportChannelHandler)_handlers[cid]; }
            if (handler == null) return; // no subscriber - silently dropped (not an error)
            try
            {
                handler(cid, payload);
            }
            catch (Exception ex)
            {
                // Isolation: a crashing service/app handler must NOT kill the pump or the others.
                Debug.WriteLine("[Bus] handler EX on '" + cid + "': " + ex.Message);
            }
        }

        // ---- app-scoped channel ----

        /// <summary>Hand a loadable app a namespaced, auto-cleaning channel. appId must be non-empty
        /// and contain no '.' (it becomes a path segment in <c>app.&lt;appId&gt;.*</c>).</summary>
        public IAppChannel OpenAppChannel(string appId)
        {
            if (string.IsNullOrEmpty(appId) || appId.IndexOf('.') >= 0)
                throw new ArgumentException("appId must be non-empty and contain no '.'", "appId");
            return new AppChannel(this, appId);
        }

        // ---- framing (WebRtcDataFraming, matched to SpawnWear.Bridge) ----

        // [channelIdLen:u8][channelId UTF-8][payloadLen:u16-LE][payload]. Returns null if the ids/lengths
        // exceed the wire caps (1-byte id length, 2-byte payload length).
        internal static byte[] Frame(string channelId, byte[] payload)
        {
            if (string.IsNullOrEmpty(channelId) || payload == null) return null;
            byte[] cid = Encoding.UTF8.GetBytes(channelId);
            if (cid.Length > 255 || payload.Length > 65535) return null;
            byte[] frame = new byte[3 + cid.Length + payload.Length];
            frame[0] = (byte)cid.Length;
            Array.Copy(cid, 0, frame, 1, cid.Length);
            int p = 1 + cid.Length;
            frame[p] = (byte)(payload.Length & 0xFF);
            frame[p + 1] = (byte)((payload.Length >> 8) & 0xFF);
            if (payload.Length > 0) Array.Copy(payload, 0, frame, p + 2, payload.Length);
            return frame;
        }

        // Private app-scoped view. Forces the app's prefix on every send/subscribe and tracks its
        // subscriptions for one-shot teardown on Close().
        sealed class AppChannel : IAppChannel
        {
            readonly TransportBus _bus;
            readonly string _prefix;          // "app.<appId>."
            readonly ArrayList _subscribed = new ArrayList(); // full channel ids this app registered

            public AppChannel(TransportBus bus, string appId)
            {
                _bus = bus;
                _prefix = AppPrefix + appId + ".";
            }

            public void Send(string name, byte[] payload)
            {
                _bus.Send(_prefix + name, payload);
            }

            public void OnMessage(string name, TransportChannelHandler handler)
            {
                string cid = _prefix + name;
                _bus.Subscribe(cid, handler);
                lock (_subscribed) { _subscribed.Add(cid); }
            }

            public void Close()
            {
                lock (_subscribed)
                {
                    foreach (object cid in _subscribed)
                        _bus.Unsubscribe((string)cid);
                    _subscribed.Clear();
                }
            }
        }
    }
}
