using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Wire;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Infrastructure implementation of <see cref="ICallMediaSession"/>.
/// Wraps one <see cref="RtpSession"/> for a single call leg.
/// Created by <see cref="RtpCallMediaSessionFactory"/> from negotiated SDP parameters.
/// </summary>
internal sealed class RtpCallMediaSession : ICallMediaSession
{
    private static readonly IReadOnlyDictionary<int, string> EmptyPayloadTypeCodecMap =
        new ReadOnlyDictionary<int, string>(new Dictionary<int, string>());
    private static readonly TimeSpan DefaultMetricsPublishInterval = TimeSpan.FromSeconds(1);
    private const double DefaultRoundTripTimeHintMs = 60;
    private const int MaxConcealmentBurstPackets = 3;
    private const int DtmfDefaultDurationMs = 160;

    private readonly RtpSession _rtp;

    // ICE on the media socket: answers inbound connectivity checks (RFC 8445 §7.3) and runs
    // RFC 7675 consent freshness. Inactive (routes nothing) when ICE was not negotiated.
    private readonly IceMediaAttachment _iceMedia;
    private readonly DtlsMediaAttachment? _dtlsMedia;
    private readonly VideoRtpStream? _videoStream;
    private readonly IJitterBuffer _jitterBuffer;

    // SRTP/SRTCP contexts created (and thus owned) by this session; internal for test evidence.
    internal ISrtpContext? OutboundSrtpContext => _outboundSrtp;
    internal ISrtpContext? InboundSrtpContext => _inboundSrtp;
    private readonly ISrtpContext? _outboundSrtp;
    private readonly ISrtpContext? _inboundSrtp;
    internal ISrtcpContext? OutboundSrtcpContext => _outboundSrtcp;
    internal ISrtcpContext? InboundSrtcpContext => _inboundSrtcp;
    private readonly ISrtcpContext? _outboundSrtcp;
    private readonly ISrtcpContext? _inboundSrtcp;

    // Wire<->tap audio transcoder; null means the consumer receives the raw wire payload
    // (passthrough — the default and the case when wire already equals the tap codec).
    private readonly BridgeAudioTranscoder? _bridgeTranscoder;
    internal bool BridgeTranscodingActive => _bridgeTranscoder is not null;

    private readonly ILogger<RtpCallMediaSession> _logger;

    // Inbound RTCP compounds seen on this session (#261). Read through Interlocked with the counter it is
    // reported next to, so the supervision sees a monotonic count from any thread.
    private long _rtcpPacketsReceived;
    // Binds this leg to one remote synchronisation source; everything downstream is single-stream (#161 P2-6).
    private readonly RtpRemoteSourceLatch _sourceLatch;
    // RFC 4733 inbound DTMF reassembly, shared with the bundled path. Driven only by the single RTP receive
    // loop (RtpSession fires PacketReceived sequentially), which is the confinement it relies on.
    private readonly RtpInboundDtmfReassembler _dtmfReassembler;
    private readonly CancellationTokenSource _cts = new();
    private readonly InboundRtpStatistics _inboundStats = new();
    private readonly TimeSpan _playoutInterval;
    private readonly TimeSpan _metricsPublishInterval;
    private readonly uint _defaultFrameDurationRtpUnits;
    private readonly int _clockRate;
    private readonly int _negotiatedPayloadType;
    private readonly IReadOnlyDictionary<int, string> _payloadTypeCodecMap;
    private readonly int? _telephoneEventPayloadType;
    private Task? _playoutLoop;
    private DateTimeOffset _nextMetricsPublishAtUtc;
    private byte[] _lastDeliveredPayload = Array.Empty<byte>();
    private int _lastDeliveredPayloadType = -1;
    private bool _hasLastDeliveredSequence;
    private ushort _lastDeliveredSequence;
    private int _observedInboundPayloadType = -1;
    private int _loggedUnadvertisedInboundPayloadType;
    private int _started;
    private int _disposed;

    /// <inheritdoc />
    public event Action<CallAudioFrame>? FrameReceived;

    /// <inheritdoc />
    public event Action<byte, int>? DtmfReceived;

    /// <inheritdoc />
    public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated;

    /// <inheritdoc />
    public event Action<IReadOnlyList<RtcpPacket>>? RtcpCompoundReceived;

    /// <inheritdoc />
    public event Action? MediaConsentLost;

    /// <inheritdoc />
    public event Action? MediaConnectivityDegraded;

    /// <inheritdoc />
    public event Action? MediaConnectivityRecovered;

    internal RtpCallMediaSession(
        CallMediaParameters parameters,
        ILoggerFactory loggerFactory,
        PayloadCodecKind? bridgeTapCodec = null,
        IDtlsSrtpHandshaker? dtlsHandshaker = null,
        DtlsCertificate? dtlsCertificate = null)
        : this(parameters, loggerFactory, jitterBufferOptions: null, playoutInterval: null,
            metricsPublishInterval: null, bridgeTapCodec: bridgeTapCodec,
            dtlsHandshaker: dtlsHandshaker, dtlsCertificate: dtlsCertificate)
    {
    }

    internal RtpCallMediaSession(
        CallMediaParameters parameters,
        ILoggerFactory loggerFactory,
        JitterBufferOptions? jitterBufferOptions,
        TimeSpan? playoutInterval,
        TimeSpan? metricsPublishInterval,
        PayloadCodecKind? bridgeTapCodec = null,
        IDtlsSrtpHandshaker? dtlsHandshaker = null,
        DtlsCertificate? dtlsCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // Fail closed before any resource is allocated: a DTLS-negotiated leg with missing
        // dependencies must not bind a socket or create contexts it would then leak.
        DtlsMediaAttachment.EnsureDependencies(parameters, dtlsHandshaker, dtlsCertificate);

        _logger = loggerFactory.CreateLogger<RtpCallMediaSession>();
        _sourceLatch = new RtpRemoteSourceLatch(_logger);
        _negotiatedPayloadType = parameters.PayloadType;
        _payloadTypeCodecMap = parameters.PayloadTypeCodecMap ?? EmptyPayloadTypeCodecMap;
        _telephoneEventPayloadType = ResolveTelephoneEventPayloadType(parameters);
        _clockRate = Math.Max(parameters.ClockRate, 1);
        _dtmfReassembler = new RtpInboundDtmfReassembler(_clockRate, DispatchInboundDtmf, _logger);
        _playoutInterval = ResolvePlayoutInterval(parameters, playoutInterval);
        _metricsPublishInterval = ResolveMetricsPublishInterval(metricsPublishInterval);
        _defaultFrameDurationRtpUnits = (uint)Math.Max(parameters.SamplesPerPacket, 0);

        var effectiveJitterBufferOptions = jitterBufferOptions ?? new JitterBufferOptions
        {
            ClockRate = _clockRate
        };
        _jitterBuffer = new global::CalloraVoipSdk.Core.Infrastructure.Rtp.JitterBuffer.JitterBuffer(effectiveJitterBufferOptions);
        _jitterBuffer.UpdateRoundTripTime(DefaultRoundTripTimeHintMs);

        // Bridge transcoding: when a fixed tap codec is requested and the negotiated wire
        // codec differs, transcode audio frames between wire and tap so a single-codec
        // consumer (e.g. the µ-law-only OpenAI bridge) works over any negotiated codec.
        _bridgeTranscoder = bridgeTapCodec == PayloadCodecKind.Pcmu
            ? BridgeAudioTranscoder.CreateForPcmuTap(
                AudioPayloadTranscoder.ResolveCodecKind(ResolveWireCodecName(parameters), parameters.PayloadType),
                (byte)parameters.PayloadType,
                _logger)
            : null;

        // This session creates the SDES SRTP/SRTCP contexts and therefore owns their disposal
        // (key zeroing) — RtpSession only borrows them via options. DTLS-keyed legs start with
        // no contexts and RequireEncryptedMedia keeps the session fail-closed until the DTLS
        // attachment installs the post-handshake contexts (which the attachment owns).
        (_outboundSrtp, _inboundSrtp, _outboundSrtcp, _inboundSrtcp) =
            SdesMediaCryptoContextFactory.TryCreate(parameters, _logger);
        var options = new RtpSessionOptions
        {
            LocalEndPoint    = parameters.LocalEndPoint,
            RemoteEndPoint   = parameters.RemoteEndPoint,
            PayloadType      = (byte)parameters.PayloadType,
            ClockRate        = _clockRate,
            SamplesPerPacket = parameters.SamplesPerPacket,
            OutboundSrtp     = _outboundSrtp,
            InboundSrtp      = _inboundSrtp,
            OutboundSrtcp    = _outboundSrtcp,
            InboundSrtcp     = _inboundSrtcp,
            // Fail-closed backstop: any secure-signaled negotiation (SDES, DTLS, or a
            // keyless degenerate exchange) must never fall through to plain RTP. SDES
            // legs have their contexts installed above; DTLS legs get them post-handshake.
            RequireEncryptedMedia = parameters.IsSrtpNegotiated || parameters.IsDtlsNegotiated
        };

        var logger = loggerFactory.CreateLogger<RtpSession>();
        _rtp = new RtpSession(options, new RtpPacketCodec(), logger);
        _rtp.PacketReceived += OnPacketReceived;
        _rtp.RtcpCompoundReceived += OnRtcpCompoundReceived;

        // ICE on the media 5-tuple (RFC 8445 §7.3 inbound checks + RFC 7675 consent): the attachment
        // answers inbound checks and runs consent freshness on this same socket.
        _iceMedia = new IceMediaAttachment(
            IceMediaParameters.FromCall(parameters), _rtp.SendRawAsync, loggerFactory,
            OnMediaConsentLost, OnMediaConnectivityDegraded, OnMediaConnectivityRecovered);
        if (_iceMedia.IsActive)
            _rtp.StunPacketReceived += _iceMedia.OnStunPacketReceived;

        // DTLS-SRTP keying (RFC 5763): the attachment runs the handshake over this same
        // socket and installs the derived contexts; on failure media stays fail-closed and
        // transmission ceases. Throws when DTLS was negotiated but dependencies are missing.
        _dtlsMedia = DtlsMediaAttachment.TryCreate(
            parameters, dtlsHandshaker, dtlsCertificate, _rtp.SendRawAsync,
            _rtp.InstallSecurityContexts, _rtp.StopTransmission, loggerFactory);
        if (_dtlsMedia is not null)
            _rtp.DtlsPacketReceived += _dtlsMedia.OnDtlsPacketReceived;

        // Video sub-stream (WebRTC phase 2): own socket, own payload format, and on
        // DTLS-keyed legs its own handshake — null for audio-only legs.
        _videoStream = VideoRtpStream.TryCreate(parameters, loggerFactory, dtlsHandshaker, dtlsCertificate);
    }

    /// <inheritdoc />
    public IVideoMediaStream? Video => _videoStream;

    // RFC 7675 §5.1: on ICE consent loss the pair is dead — cease media transmission on it. The
    // socket stays open (ICE restart could revive the path); surfacing the loss to the application
    // for terminate / ICE-restart is left to a later step.
    private void OnMediaConsentLost()
    {
        _logger.LogWarning("Ceasing media transmission for the call after ICE consent loss (RFC 7675 §5.1).");
        _rtp.StopTransmission();

        // Surface the loss so the orchestrator can move the call's ICE state to Failed. Defensive:
        // a throwing handler must not disturb the consent-monitor loop that raised this.
        try
        {
            MediaConsentLost?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICE consent-loss handler threw while surfacing the transport state.");
        }
    }

    // Transient connectivity changes from the consent monitor (still inside the consent window). Unlike
    // consent loss they do NOT cease transmission — the path may recover — they only surface the running
    // ICE state. The monitor already isolates a throwing handler, so no extra guarding is needed here.
    private void OnMediaConnectivityDegraded() => MediaConnectivityDegraded?.Invoke();
    private void OnMediaConnectivityRecovered() => MediaConnectivityRecovered?.Invoke();

    /// <summary>Test seam: the running playout loop, so a repeated start can be proven not to replace it.</summary>
    internal Task? PlayoutLoopForTest => _playoutLoop;

    /// <summary>
    /// Inbound RTP packets dropped because they came from a synchronisation source this leg is not latched to
    /// (#161 P2-6). Surfaced for tests and diagnostics — the drop itself is logged once, not per packet.
    /// </summary>
    internal long ForeignSourcePacketsDropped => _sourceLatch.DroppedPackets;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Idempotent, mirroring RtpSession.StartAsync and the bundle guard (HARD-C5). A second call used to
        // start a second playout loop and overwrite _playoutLoop: the first one kept running against the same
        // jitter buffer and the same delivery state — two threads on fields written without synchronisation —
        // and DisposeAsync could then only ever await the last one, so the orphan ran on until cancellation.
        // Restarting the ICE/DTLS/video attachments under a live session is disruptive in its own right, so
        // the guard covers the whole method, not just the loop. A start after disposal is a no-op as well:
        // _cts is disposed by then, and reading its token would throw. (A start racing a concurrent dispose
        // is still the caller's contract — this closes the sequential cases, which is what the API promises.)
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;

        _nextMetricsPublishAtUtc = DateTimeOffset.UtcNow.Add(_metricsPublishInterval);

        _ = _rtp.StartAsync(_cts.Token);
        _iceMedia.Start();
        _dtlsMedia?.Start(_cts.Token);
        _videoStream?.Start(_cts.Token);
        _playoutLoop = RunPlayoutLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendFrameAsync(CallAudioFrame frame, CancellationToken ct = default)
    {
        // Bridge transcoding: the consumer hands us tap-codec (µ-law) audio; encode it to
        // the negotiated wire codec and always send under the wire payload type.
        if (_bridgeTranscoder is { } transcoder)
        {
            await _rtp.SendAsync(
                transcoder.TapToWire(frame.Payload),
                payloadTypeOverride: transcoder.WirePayloadType,
                cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        var outboundPayloadType = ResolveOutboundPayloadType(frame.PayloadType);
        await _rtp.SendAsync(
            frame.Payload,
            payloadTypeOverride: (byte)outboundPayloadType,
            cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendDtmfAsync(byte toneCode, int durationMs = DtmfDefaultDurationMs, CancellationToken ct = default)
    {
        if (toneCode > 15)
            throw new ArgumentOutOfRangeException(nameof(toneCode), toneCode, "DTMF tone code must be between 0 and 15.");
        if (durationMs < RtpTelephoneEventCodec.MinDurationMs)
            throw new ArgumentOutOfRangeException(
                nameof(durationMs),
                durationMs,
                $"DTMF duration must be at least {RtpTelephoneEventCodec.MinDurationMs} ms.");

        var payloadType = _telephoneEventPayloadType
            ?? throw new InvalidOperationException("RTP telephone-event was not negotiated for this call media session.");
        var durationRtpUnits = RtpTelephoneEventCodec.DurationMsToRtpUnits(durationMs, _clockRate);
        // Stamp the whole burst with the audio stream's current cursor and reserve the event's full duration so
        // the cursor advances past it — otherwise a following DTMF event reuses this timestamp and a receiver
        // folds it into this event, dropping the repeated tone (RFC 4733 §2.5.1.4).
        var eventTimestamp = _rtp.ReserveTimestamp(durationRtpUnits);

        await RtpTelephoneEventBurst.SendAsync(
                async (payload, marker, token) => await _rtp.SendTimestampedAsync(
                        payload,
                        marker: marker,
                        payloadType: (byte)payloadType,
                        timestamp: eventTimestamp,
                        cancellationToken: token).ConfigureAwait(false),
                static (ms, token) => Task.Delay(ms, token),
                toneCode,
                durationMs,
                _clockRate,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void UpdateRoundTripTimeHint(TimeSpan roundTripTime)
    {
        if (roundTripTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(roundTripTime), "RTT hint must be >= 0.");

        _jitterBuffer.UpdateRoundTripTime(roundTripTime.TotalMilliseconds);
    }

    /// <inheritdoc />
    public CallMediaRuntimeMetrics GetRuntimeMetricsSnapshot()
        => CreateRuntimeMetricsSnapshot(DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public CallMediaRtpSnapshot GetRtpSnapshot()
    {
        var sender = _rtp.GetSenderStatisticsSnapshot();
        var now = DateTimeOffset.UtcNow;
        var jitterMs = _jitterBuffer.EstimatedJitterMs;
        var jitterRtpUnits = ConvertJitterMsToRtpUnits(jitterMs, _clockRate);
        var localRttHintMs = _jitterBuffer.EstimatedRoundTripTimeMs;

        var report = _inboundStats.CaptureRtcpReport();
        var localLossPercent = report.PacketsExpected == 0
            ? 0
            : Math.Max(0, report.CumulativePacketsLost) * 100.0 / report.PacketsExpected;

        return new CallMediaRtpSnapshot(
            CapturedAtUtc: now,
            LocalSsrc: sender.LocalSsrc,
            RemoteSsrc: report.RemoteSsrc,
            SenderPacketCount: sender.SenderPacketCount,
            SenderOctetCount: sender.SenderOctetCount,
            LastSentRtpTimestamp: sender.LastSentRtpTimestamp,
            SinceLastRtpSend: sender.SinceLastSend,
            HasSentRtpPackets: sender.HasSentPackets,
            PacketsExpected: report.PacketsExpected,
            PacketsReceived: report.PacketsReceived,
            FractionLost: report.FractionLost,
            CumulativePacketsLost: report.CumulativePacketsLost,
            ExtendedHighestSequenceNumber: report.ExtendedHighestSequenceNumber,
            InterarrivalJitterRtpUnits: jitterRtpUnits,
            LocalReceiveJitterMs: jitterMs,
            LocalReceivePacketLossPercent: localLossPercent,
            LocalRoundTripTimeHintMs: localRttHintMs);
    }

    /// <inheritdoc />
    public async Task SendRtcpMuxDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default)
    {
        if (datagram.IsEmpty)
            throw new ArgumentException("RTCP datagram must not be empty.", nameof(datagram));

        await _rtp.SendControlAsync(datagram, ct).ConfigureAwait(false);
    }

    private void OnPacketReceived(object? sender, RtpPacket packet)
    {
        // One leg, one remote source: a second concurrent SSRC is dropped rather than mixed into the single
        // jitter buffer and playout cursor, while a genuine source change takes over and resets them (P2-6).
        if (!_sourceLatch.Admit(packet.Ssrc, out var sourceChanged))
            return;
        if (sourceChanged)
            ResetStreamStateForNewSource();

        var isTelephoneEventPacket = IsTelephoneEventPayloadType(packet.PayloadType);
        TrackInboundPayloadType(packet.PayloadType);
        _inboundStats.TrackSequence(packet.Ssrc, packet.SequenceNumber);
        _inboundStats.RecordReceived();

        if (isTelephoneEventPacket)
        {
            // A telephone-event consumes a sequence number but never enters the jitter buffer, so the
            // cursor has to account for it or the next audio packet reads as a gap. Advance it the same
            // forward-only way a delivery does: a reordered event arriving behind the cursor must not pull
            // it back, which would fabricate a gap the size of the reordering and burn concealment frames
            // on audio that was already played out (probe: event 105 pulled the cursor from 110 to 105).
            // Still only a bump, never an establish — a leading event must not seed the cursor.
            if (_hasLastDeliveredSequence)
                AdvanceDeliveredSequence(packet.SequenceNumber);

            _dtmfReassembler.Handle(packet);
            return;
        }

        // Audio keeps arriving while a lost end-of-event packet does not, so this is where a pending tone the
        // wire will never close gets closed instead (#161 P3-16). No-op unless one is overdue.
        _dtmfReassembler.PollTimeout();

        // Jitter arrival/playout are driven off a monotonic clock (not wall-clock UtcNow) so an NTP step or
        // manual system-clock change mid-call cannot corrupt the interarrival jitter estimate or the playout
        // schedule. Add here and TryGetNext in DrainReadyPackets must read the same jump-free source.
        var addResult = _jitterBuffer.Add(packet, MonotonicClock.Now);
        HandleJitterBufferAddResult(addResult, packet);
    }

    // Forgets everything that describes the previous source's stream so the new one starts clean: the jitter
    // buffer's sequence/playout reference, the delivery cursor, and the concealment payload. The RTCP
    // receiver-report bookkeeping restarts on its own (InboundRtpStatistics.TrackSequence resets on an SSRC
    // change), and the delivery counters are cumulative for the leg by design.
    private void ResetStreamStateForNewSource()
    {
        _jitterBuffer.Reset();
        _hasLastDeliveredSequence = false;
        _lastDeliveredPayload = Array.Empty<byte>();
        _lastDeliveredPayloadType = -1;
    }

    private void OnRtcpCompoundReceived(IReadOnlyList<RtcpPacket> packets)
    {
        // Liveness evidence for the media supervision (#261): counted before the fan-out so a throwing
        // subscriber cannot make a live peer look dead.
        Interlocked.Increment(ref _rtcpPacketsReceived);

        try
        {
            RtcpCompoundReceived?.Invoke(packets);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unhandled exception while dispatching decoded RTCP compound.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _rtp.PacketReceived -= OnPacketReceived;
        _rtp.RtcpCompoundReceived -= OnRtcpCompoundReceived;
        if (_iceMedia.IsActive)
            _rtp.StunPacketReceived -= _iceMedia.OnStunPacketReceived;
        await _iceMedia.DisposeAsync().ConfigureAwait(false);
        if (_dtlsMedia is not null)
        {
            // Ordering: stop media/RTCP transmission first so no send can hit a security
            // context the attachment is about to dispose; the attachment then sends
            // close_notify while the socket send path is still up (before _rtp disposal).
            // A receive racing the context disposal is a clean drop (the unprotect paths
            // treat ObjectDisposedException as such).
            _rtp.StopTransmission();
            _rtp.DtlsPacketReceived -= _dtlsMedia.OnDtlsPacketReceived;
            await _dtlsMedia.DisposeAsync().ConfigureAwait(false);
        }

        // Video runs on its own socket and DTLS association — tear it down independently.
        if (_videoStream is not null)
            await _videoStream.DisposeAsync().ConfigureAwait(false);

        _cts.Cancel();

        var playoutLoop = _playoutLoop;

        await _rtp.DisposeAsync().ConfigureAwait(false);
        if (playoutLoop is not null)
        {
            try
            {
                await playoutLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        FrameReceived = null;
        DtmfReceived = null;
        RuntimeMetricsUpdated = null;
        RtcpCompoundReceived = null;
        MediaConsentLost = null;
        MediaConnectivityDegraded = null;
        MediaConnectivityRecovered = null;
        _cts.Dispose();

        // Zero the SRTP session keys once the RTP session (their only borrower) is down.
        _outboundSrtp?.Dispose();
        _inboundSrtp?.Dispose();
        _outboundSrtcp?.Dispose();
        _inboundSrtcp?.Dispose();
    }

    private int ResolveOutboundPayloadType(int framePayloadType)
    {
        var observedInbound = Volatile.Read(ref _observedInboundPayloadType);
        if (IsAdvertisedPayloadType(observedInbound))
            return observedInbound;

        if (IsAdvertisedPayloadType(framePayloadType))
            return framePayloadType;

        return _negotiatedPayloadType;
    }

    private async Task RunPlayoutLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_playoutInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                DrainReadyPackets();
                PublishRuntimeMetricsIfDue(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RTP playout loop failed unexpectedly.");
        }
        finally
        {
            DrainReadyPackets();
            PublishRuntimeMetricsIfDue(DateTimeOffset.UtcNow, force: true);
        }
    }

    private void DrainReadyPackets()
    {
        while (true)
        {
            var packet = _jitterBuffer.TryGetNext(MonotonicClock.Now); // monotonic — see OnInboundRtpPacket
            if (packet is null)
                return;

            DispatchPacketWithConcealment(packet);
        }
    }

    private void DispatchPacketWithConcealment(RtpPacket packet)
    {
        EmitConcealmentFramesIfNeeded(packet.SequenceNumber, packet.PayloadType, packet.Payload.Length);

        var payload = GetPacketPayloadArray(packet.Payload);
        DispatchFrame(payload, packet.PayloadType);

        _lastDeliveredPayload = payload;
        _lastDeliveredPayloadType = packet.PayloadType;
        AdvanceDeliveredSequence(packet.SequenceNumber);
        _inboundStats.RecordDelivered();
    }

    // Marks a sequence as accounted for and advances the delivered-sequence cursor forward only (RFC 3550 §A.1
    // signed-delta wraparound). Forward-only so that a reordered delivery arriving behind a cursor already
    // advanced by a late drop does not move the cursor backwards (which would fabricate a huge gap). The first
    // call establishes the cursor. Called from the playout loop (delivery) and the RTP receive loop (late drop
    // and the telephone-event bump) — _lastDeliveredSequence is a ushort, so the writes are atomic.
    private void AdvanceDeliveredSequence(ushort sequenceNumber)
    {
        if (!_hasLastDeliveredSequence || unchecked((short)(sequenceNumber - _lastDeliveredSequence)) > 0)
            _lastDeliveredSequence = sequenceNumber;
        _hasLastDeliveredSequence = true;
    }

    private void EmitConcealmentFramesIfNeeded(ushort incomingSequenceNumber, byte incomingPayloadType, int incomingPayloadLength)
    {
        if (!_hasLastDeliveredSequence)
            return;

        var expectedSequence = unchecked((ushort)(_lastDeliveredSequence + 1));
        // RFC 3550 §A.1 signed-delta: a non-positive delta is not a forward gap — the packet is in order (0) or
        // already behind the cursor, which happens once a late-dropped packet has advanced the cursor past this
        // slot (F002). Neither case is unrecoverable loss, so there is nothing to conceal or count.
        var forwardGap = unchecked((short)(incomingSequenceNumber - expectedSequence));
        if (forwardGap <= 0)
            return;

        var gapSize = (ushort)forwardGap;
        var concealmentCount = Math.Min((int)gapSize, MaxConcealmentBurstPackets);
        for (var i = 0; i < concealmentCount; i++)
        {
            var concealedPayload = CreateConcealmentPayload(incomingPayloadType, incomingPayloadLength);
            DispatchFrame(concealedPayload, incomingPayloadType);
            _lastDeliveredSequence = unchecked((ushort)(_lastDeliveredSequence + 1));
            _inboundStats.RecordConcealed();
        }

        if (gapSize > concealmentCount)
            _inboundStats.AddUnrecoverableLoss(gapSize - concealmentCount);
    }

    private byte[] CreateConcealmentPayload(byte payloadType, int fallbackLength)
    {
        if (_lastDeliveredPayload.Length > 0 && _lastDeliveredPayloadType == payloadType)
        {
            var copy = new byte[_lastDeliveredPayload.Length];
            Buffer.BlockCopy(_lastDeliveredPayload, 0, copy, 0, copy.Length);
            return copy;
        }

        if (fallbackLength <= 0)
            return Array.Empty<byte>();

        return new byte[fallbackLength];
    }

    /// <summary>
    /// Returns the underlying payload array when the memory already spans the full array.
    /// Falls back to a copy for sliced/non-array-backed payload memory.
    /// </summary>
    private static byte[] GetPacketPayloadArray(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment)
            && segment.Array is not null)
        {
            if (segment.Offset == 0 && segment.Count == segment.Array.Length)
                return segment.Array;

            var copy = GC.AllocateUninitializedArray<byte>(segment.Count);
            Buffer.BlockCopy(segment.Array, segment.Offset, copy, 0, segment.Count);
            return copy;
        }

        return payload.ToArray();
    }

    private static string ResolveWireCodecName(CallMediaParameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters.CodecName))
            return parameters.CodecName.Trim();

        return parameters.PayloadTypeCodecMap is not null
               && parameters.PayloadTypeCodecMap.TryGetValue(parameters.PayloadType, out var mapped)
               && !string.IsNullOrWhiteSpace(mapped)
            ? mapped.Trim()
            : string.Empty;
    }

    // µ-law digital silence is 0xFF; used as a safe inbound fallback on transcode failure.
    private static byte[] MuLawSilence(int wirePayloadLength)
    {
        var samples = wirePayloadLength > 0 ? 160 : 0; // one 20 ms G.711 frame
        var silence = new byte[samples];
        Array.Fill(silence, (byte)0xFF);
        return silence;
    }

    private void DispatchFrame(byte[] payload, byte payloadType)
    {
        // Bridge transcoding: convert the wire payload to the tap codec (µ-law) so a
        // single-codec consumer receives a codec it understands. A decode failure yields
        // µ-law silence rather than tearing down the playout loop.
        if (_bridgeTranscoder is { } transcoder)
        {
            try
            {
                payload = transcoder.WireToTap(payload);
                payloadType = transcoder.TapPayloadType;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Bridge wire->tap transcode failed; delivering silence.");
                payload = MuLawSilence(payload.Length);
                payloadType = transcoder.TapPayloadType;
            }
        }

        var frame = new CallAudioFrame(
            payload,
            payloadType,
            DurationRtpUnits: _defaultFrameDurationRtpUnits);

        try
        {
            FrameReceived?.Invoke(frame);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unhandled exception while dispatching inbound RTP frame.");
        }
    }

    private void HandleJitterBufferAddResult(JitterBufferAddResult addResult, RtpPacket packet)
    {
        switch (addResult)
        {
            case JitterBufferAddResult.Queued:
                _inboundStats.RecordQueued();
                break;

            case JitterBufferAddResult.Late:
                _inboundStats.RecordDroppedLate();
                // The packet arrived (RFC 3550 counts it as received) but too late to play out — it is a late
                // drop, NOT unrecoverable loss. Advance the delivered-sequence cursor past it (forward-only, so a
                // genuinely out-of-order/lost sequence is never masked) so the next delivered packet does not see
                // a false gap that EmitConcealmentFramesIfNeeded would otherwise miscount as unrecoverable loss
                // (F002). Runs on the RTP receive loop, like the telephone-event cursor bump above.
                if (_hasLastDeliveredSequence)
                    AdvanceDeliveredSequence(packet.SequenceNumber);
                _logger.LogDebug(
                    "RTP packet dropped as late in jitter buffer: seq={Seq}, ts={Timestamp}, pt={PayloadType}, ssrc={Ssrc:X8}.",
                    packet.SequenceNumber,
                    packet.Timestamp,
                    packet.PayloadType,
                    packet.Ssrc);
                break;

            case JitterBufferAddResult.Overflow:
                _inboundStats.RecordDroppedOverflow();
                _logger.LogDebug(
                    "RTP packet dropped due to jitter buffer overflow: seq={Seq}, ts={Timestamp}, ssrc={Ssrc:X8}.",
                    packet.SequenceNumber,
                    packet.Timestamp,
                    packet.Ssrc);
                break;

            case JitterBufferAddResult.Duplicate:
                _inboundStats.RecordDroppedDuplicate();
                break;
        }
    }

    private void PublishRuntimeMetricsIfDue(DateTimeOffset now, bool force = false)
    {
        if (!force && now < _nextMetricsPublishAtUtc)
            return;

        _nextMetricsPublishAtUtc = now.Add(_metricsPublishInterval);
        var snapshot = CreateRuntimeMetricsSnapshot(now);

        try
        {
            RuntimeMetricsUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unhandled exception while dispatching media runtime metrics.");
        }
    }

    private CallMediaRuntimeMetrics CreateRuntimeMetricsSnapshot(DateTimeOffset timestamp)
    {
        var counters = _inboundStats.SnapshotCounters();
        return new(
            capturedAtUtc: timestamp,
            packetsReceived: counters.PacketsReceived,
            packetsQueued: counters.PacketsQueued,
            packetsDelivered: counters.PacketsDelivered,
            packetsDroppedLate: counters.PacketsDroppedLate,
            packetsDroppedOverflow: counters.PacketsDroppedOverflow,
            packetsDroppedDuplicate: counters.PacketsDroppedDuplicate,
            packetsConcealed: counters.PacketsConcealed,
            packetsUnrecoverableLoss: counters.PacketsUnrecoverableLoss,
            bufferedPackets: _jitterBuffer.BufferedCount,
            estimatedJitterMs: _jitterBuffer.EstimatedJitterMs,
            adaptiveDelayMs: _jitterBuffer.CurrentDelayMs,
            estimatedRoundTripTimeMs: _jitterBuffer.EstimatedRoundTripTimeMs,
            rtcpPacketsReceived: Interlocked.Read(ref _rtcpPacketsReceived));
    }


    private static TimeSpan ResolvePlayoutInterval(CallMediaParameters parameters, TimeSpan? configuredInterval)
    {
        if (configuredInterval is { } explicitInterval && explicitInterval > TimeSpan.Zero)
            return explicitInterval;

        var packetDurationMs = parameters.ClockRate <= 0
            ? 20.0
            : (parameters.SamplesPerPacket * 1000.0 / parameters.ClockRate);
        var intervalMs = Math.Clamp(packetDurationMs / 4.0, 2.0, 10.0);
        return TimeSpan.FromMilliseconds(intervalMs);
    }

    private static TimeSpan ResolveMetricsPublishInterval(TimeSpan? configuredInterval)
    {
        if (configuredInterval is { } explicitInterval && explicitInterval > TimeSpan.Zero)
            return explicitInterval;

        return DefaultMetricsPublishInterval;
    }

    private void TrackInboundPayloadType(byte payloadType)
    {
        if (IsTelephoneEventPayloadType(payloadType))
            return;

        if (!IsAdvertisedPayloadType(payloadType))
        {
            if (Interlocked.Exchange(ref _loggedUnadvertisedInboundPayloadType, 1) == 0)
            {
                _logger.LogWarning(
                    "Inbound RTP PT {InboundPt} is not advertised in negotiated SDP; ignoring for outbound PT adaptation.",
                    payloadType);
            }
            return;
        }

        var previous = Interlocked.Exchange(ref _observedInboundPayloadType, payloadType);
        if (previous != payloadType)
        {
            _logger.LogDebug(
                "Detected inbound RTP payload type {InboundPt}; adapting outbound PT (negotiated={NegotiatedPt}, previousObserved={PreviousObservedPt}).",
                payloadType,
                _negotiatedPayloadType,
                previous);
        }
    }

    private static uint ConvertJitterMsToRtpUnits(double jitterMs, int clockRate)
    {
        if (jitterMs <= 0)
            return 0;

        var units = jitterMs * clockRate / 1000.0;
        if (units >= uint.MaxValue)
            return uint.MaxValue;

        return (uint)Math.Round(units, MidpointRounding.AwayFromZero);
    }

    private static bool IsValidPayloadType(int payloadType)
        => payloadType is >= 0 and <= 127;

    private bool IsAdvertisedPayloadType(int payloadType)
    {
        if (!IsValidPayloadType(payloadType))
            return false;

        if (IsTelephoneEventPayloadType(payloadType))
            return false;

        if (_payloadTypeCodecMap.ContainsKey(payloadType))
            return true;

        // Fallback for static payload types when rtpmap is absent from SDP.
        return payloadType is 0 or 8 or 9;
    }

    private bool IsTelephoneEventPayloadType(int payloadType)
        => _telephoneEventPayloadType is int telephoneEventPayloadType
           && payloadType == telephoneEventPayloadType;

    private static int? ResolveTelephoneEventPayloadType(CallMediaParameters parameters)
    {
        if (parameters.TelephoneEventPayloadType is >= 0 and <= 127)
            return parameters.TelephoneEventPayloadType.Value;

        foreach (var mapping in parameters.PayloadTypeCodecMap)
        {
            if (mapping.Key is < 0 or > 127)
                continue;

            if (mapping.Value.Equals("TELEPHONE-EVENT", StringComparison.OrdinalIgnoreCase))
                return mapping.Key;
        }

        return null;
    }

    private void DispatchInboundDtmf(byte toneCode, int durationMs)
    {
        try
        {
            DtmfReceived?.Invoke(toneCode, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unhandled exception while dispatching inbound RTP DTMF event.");
        }
    }

}
