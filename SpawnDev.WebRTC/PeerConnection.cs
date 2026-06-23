using System.Runtime.CompilerServices;

namespace SpawnDev.WebRTC
{
    /// <summary>
    /// Minimal managed surface over libpeer's <c>PeerConnection</c> (data-channel-only
    /// WebRTC) for the SpawnWear watch firmware (Phase 7b). Native code (the SpawnDev.WebRTC
    /// interop assembly in nf-interpreter) owns a table of libpeer <c>PeerConnection*</c> and
    /// runs the <c>peer_connection_loop</c> pump on a dedicated FreeRTOS task; these calls are
    /// thin, mutex-guarded entry points into that table.
    ///
    /// <para>Handle-based because nanoFramework interop can't carry rich objects across the
    /// boundary: <see cref="Create"/> returns an <c>int</c> handle and every other call takes
    /// it. The interop boundary also can't RETURN or by-ref arrays/strings, so the local SDP
    /// is read via <see cref="GetLocalSdpLength"/> + a caller-allocated buffer
    /// (<see cref="GetLocalSdp"/>), and inbound data is polled into a caller buffer
    /// (<see cref="TryReceive"/>). Strings cross only as INPUT args (set-remote-SDP, ICE).</para>
    /// </summary>
    public static class PeerConnection
    {
        /// <summary>libpeer PeerConnectionState values (peer_connection.h order).</summary>
        public const int StateClosed = 0;
        public const int StateNew = 1;
        public const int StateChecking = 2;
        public const int StateConnected = 3;
        public const int StateCompleted = 4;
        public const int StateFailed = 5;
        public const int StateDisconnected = 6;

        /// <summary>SDP types for <see cref="SetRemoteDescription"/>.</summary>
        public const int SdpTypeOffer = 0;
        public const int SdpTypeAnswer = 1;

        /// <summary>Create a data-channel-only peer connection (CODEC_NONE, DATA_CHANNEL_BINARY).
        /// Returns a handle &gt;= 0, or -1 on failure (e.g. out of native slots / OOM).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Create();

        /// <summary>Create the outbound data channel (offerer side). Call BEFORE
        /// <see cref="CreateOffer"/> so the channel is described in the offer SDP.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void CreateDataChannel(int handle, string label);

        /// <summary>Generate the SDP offer (gathers ICE; candidates are embedded). The result
        /// is stored natively - read it with <see cref="GetLocalSdpLength"/> / <see cref="GetLocalSdp"/>.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void CreateOffer(int handle);

        /// <summary>Generate the SDP answer (answerer side, after <see cref="SetRemoteDescription"/>
        /// with an offer). Stored natively; read via GetLocalSdp*.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void CreateAnswer(int handle);

        /// <summary>Apply the peer's SDP. <paramref name="sdpType"/> is
        /// <see cref="SdpTypeOffer"/> or <see cref="SdpTypeAnswer"/>.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void SetRemoteDescription(int handle, string sdp, int sdpType);

        /// <summary>Add a remote ICE candidate (trickle). Optional when full-SDP exchange
        /// already carries candidates.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void AddIceCandidate(int handle, string candidate);

        /// <summary>Byte length (UTF-8) of the stored local SDP, or 0 if none generated yet.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetLocalSdpLength(int handle);

        /// <summary>Copy the stored local SDP (UTF-8) into <paramref name="buffer"/>
        /// (which must be at least <see cref="GetLocalSdpLength"/> bytes).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void GetLocalSdp(int handle, byte[] buffer);

        /// <summary>Send <paramref name="length"/> bytes of <paramref name="data"/> on the
        /// data channel. Returns the number of bytes queued, or &lt; 0 on error / not open.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Send(int handle, byte[] data, int length);

        /// <summary>Poll for one inbound data-channel message. Copies up to
        /// <paramref name="buffer"/>.Length bytes into it and returns the message byte count,
        /// or 0 if none is queued. Native drains its receive queue one message per call.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int TryReceive(int handle, byte[] buffer);

        /// <summary>Current connection state (one of the State* constants).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetState(int handle);

        /// <summary>Close + free the peer connection and release its handle.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void Close(int handle);
    }
}
