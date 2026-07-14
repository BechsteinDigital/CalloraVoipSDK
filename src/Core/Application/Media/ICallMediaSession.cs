using CalloraVoipSdk.Core.Domain.Calls;

namespace CalloraVoipSdk.Core.Application.Media;

/// <summary>
/// Represents one active RTP media session for a call.
/// Created by <see cref="ICallMediaSessionFactory"/> and managed by <see cref="CallMediaOrchestrator"/>.
/// </summary>
internal interface ICallMediaSession : IAsyncDisposable
{
    /// <summary>Starts the RTP receive loop and schedules the send timer.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Sends one encoded audio frame as an RTP packet.</summary>
    Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default);

    /// <summary>
    /// Sends one RFC 4733 DTMF tone (<c>telephone-event</c>) over RTP.
    /// Implementations should throw when telephone-event was not negotiated.
    /// </summary>
    Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken ct = default);

    /// <summary>
    /// Updates the receive-path RTT hint used by adaptive jitter buffering.
    /// Intended for application orchestration when fresh RTT samples are available.
    /// </summary>
    void UpdateRoundTripTimeHint(TimeSpan roundTripTime);

    /// <summary>
    /// Returns the latest media runtime metrics snapshot for this session.
    /// </summary>
    CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot();

    /// <summary>
    /// Returns RTP sender/receiver counters required for RTCP SR/RR generation.
    /// </summary>
    CallMediaRtpSnapshot GetRtpSnapshot();

    /// <summary>
    /// Sends one RTCP datagram over the RTP transport socket when RTCP-MUX is active.
    /// Implementations should throw when RTCP-MUX is not available.
    /// </summary>
    Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default);

    /// <summary>
    /// Raised when an inbound audio frame is ready for playout.
    /// Includes jitter-buffered RTP frames and optional PLC concealment frames.
    /// </summary>
    event Action<CallAudioFrame>? FrameReceived;

    /// <summary>
    /// Raised when one inbound RFC 4733 DTMF event is completed.
    /// Duration is provided in milliseconds.
    /// </summary>
    event Action<byte, int>? DtmfReceived;

    /// <summary>
    /// Raised when a new runtime-metrics snapshot is published.
    /// </summary>
    event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated;

    /// <summary>
    /// Raised when an RTCP datagram is received on the RTP socket in RTCP-MUX mode.
    /// </summary>
    event Action<byte[]>? RtcpMuxDatagramReceived;

    /// <summary>
    /// Negotiated video sub-stream (WebRTC phase 2); <see langword="null"/> for an
    /// audio-only call leg. Default implementation keeps audio-only sessions untouched.
    /// </summary>
    IVideoMediaStream? Video => null;
}
