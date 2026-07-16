using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Adapts a <see cref="BundledMediaSession"/> (the RFC 8843 shared transport) to the call's
/// <see cref="ICallMediaSession"/> contract (ADR-011 B5-wire b-2), so a BUNDLE call runs through the
/// same orchestration seam as the single-stream <see cref="RtpCallMediaSession"/>. It bridges the audio
/// media path — sending and receiving RTP audio payloads — and the ICE consent lifecycle.
///
/// Transport-only like the rest of the bundle stack: the app brings the audio codec, so a
/// <see cref="CallAudioFrame"/> payload is an already-encoded RTP payload. Several ICallMediaSession
/// features the single-stream path provides are not yet wired on the bundle and are marked below — DTMF
/// (RFC 4733), RTCP-mux datagram send/receive, runtime metrics, and exposing the bundle's video as an
/// <see cref="IVideoMediaStream"/>. They fail closed (throw) or return empty rather than fake a result.
/// </summary>
internal sealed class BundledCallMediaSession : ICallMediaSession
{
    private readonly BundledMediaSession _session;

    public BundledCallMediaSession(BundledMediaSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.AudioReceived += OnAudioReceived;
        _session.MediaConsentLost += RaiseConsentLost;
        _session.MediaConnectivityDegraded += RaiseConnectivityDegraded;
        _session.MediaConnectivityRecovered += RaiseConnectivityRecovered;
    }

    /// <inheritdoc />
    public event Action<CallAudioFrame>? FrameReceived;

    // The following three are inert on the bundle path (not wired yet): empty accessors satisfy the
    // interface without retaining handlers that would never be invoked.

    /// <inheritdoc />
    public event Action<byte, int>? DtmfReceived { add { } remove { } }

    /// <inheritdoc />
    public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated { add { } remove { } }

    /// <inheritdoc />
    public event Action<byte[]>? RtcpMuxDatagramReceived { add { } remove { } }

    /// <inheritdoc />
    public event Action? MediaConsentLost;

    /// <inheritdoc />
    public event Action? MediaConnectivityDegraded;

    /// <inheritdoc />
    public event Action? MediaConnectivityRecovered;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default) => _session.StartAsync(ct);

    /// <inheritdoc />
    public Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default)
        => _session.SendAudioAsync(frame.Payload, cancellationToken: ct).AsTask();

    /// <inheritdoc />
    /// <remarks>FOLLOW-UP: telephone-event (RFC 4733) over the bundle needs a DTMF track/codec on the
    /// outbound pipeline; until then a BUNDLE leg has no in-band DTMF.</remarks>
    public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken ct = default)
        => throw new NotSupportedException("DTMF (RFC 4733) is not yet supported on a BUNDLE media session.");

    /// <inheritdoc />
    public void UpdateRoundTripTimeHint(TimeSpan roundTripTime)
    {
        // No adaptive jitter buffer on the bundle path yet — nothing to hint.
    }

    /// <inheritdoc />
    public CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot() => default; // no runtime metrics wired yet

    /// <inheritdoc />
    public CallMediaRtpSnapshot GetRtpSnapshot() => new(
        CapturedAtUtc: DateTimeOffset.UtcNow,
        LocalSsrc: _session.AudioSsrc,
        RemoteSsrc: null,
        SenderPacketCount: 0,
        SenderOctetCount: 0,
        LastSentRtpTimestamp: 0,
        HasSentRtpPackets: false,
        PacketsExpected: 0,
        PacketsReceived: 0,
        FractionLost: 0,
        CumulativePacketsLost: 0,
        ExtendedHighestSequenceNumber: 0,
        InterarrivalJitterRtpUnits: 0,
        LocalReceiveJitterMs: 0,
        LocalReceivePacketLossPercent: 0,
        LocalRoundTripTimeHintMs: 0);

    /// <inheritdoc />
    /// <remarks>FOLLOW-UP: SRTCP send over the bundle's shared socket (rtcp-mux) needs the outbound SRTCP
    /// context and a send seam; until then RTCP is not generated for a BUNDLE leg.</remarks>
    public Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default)
        => throw new NotSupportedException("RTCP-mux datagram send is not yet supported on a BUNDLE media session.");

    /// <inheritdoc />
    /// <remarks>FOLLOW-UP: expose the bundle's <c>BundledVideoTrack</c> as an <see cref="IVideoMediaStream"/>.
    /// Null keeps the call's video wiring off the bundle path for now (the bundle still carries video internally).</remarks>
    public IVideoMediaStream? Video => null;

    private void OnAudioReceived(RtpPacket packet)
        // Guarded upstream by BundledMediaSession.RaiseAudioReceived, so a throwing FrameReceived
        // subscriber cannot tear down the shared receive loop.
        => FrameReceived?.Invoke(new CallAudioFrame(packet.Payload.ToArray(), packet.PayloadType, DurationRtpUnits: 0));

    private void RaiseConsentLost() => MediaConsentLost?.Invoke();
    private void RaiseConnectivityDegraded() => MediaConnectivityDegraded?.Invoke();
    private void RaiseConnectivityRecovered() => MediaConnectivityRecovered?.Invoke();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _session.AudioReceived -= OnAudioReceived;
        _session.MediaConsentLost -= RaiseConsentLost;
        _session.MediaConnectivityDegraded -= RaiseConnectivityDegraded;
        _session.MediaConnectivityRecovered -= RaiseConnectivityRecovered;
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
