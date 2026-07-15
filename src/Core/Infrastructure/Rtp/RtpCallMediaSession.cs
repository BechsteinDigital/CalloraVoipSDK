using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CalloraVoipSdk.Core.Application.Media;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Domain.Calls;
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
    private readonly CancellationTokenSource _cts = new();
    private readonly object _rtcpStatsSync = new();
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
    private long _packetsReceived;
    private long _packetsQueued;
    private long _packetsDelivered;
    private long _packetsDroppedLate;
    private long _packetsDroppedOverflow;
    private long _packetsDroppedDuplicate;
    private long _packetsConcealed;
    private long _packetsUnrecoverableLoss;
    private bool _hasInboundRtcpStats;
    private bool _hasRemoteSsrc;
    private uint _remoteSsrc;
    private ushort _baseSequence;
    private ushort _maxSequence;
    private uint _sequenceCycles;
    private uint _packetsReceivedForRtcp;
    private uint _priorExpectedForFraction;
    private uint _priorReceivedForFraction;
    // RFC 4733 inbound DTMF reassembly state. Touched only by HandleInboundTelephoneEvent,
    // which runs solely on the single RTP receive loop (RtpSession fires PacketReceived
    // sequentially) — no other thread reads or writes it, so no synchronization is needed.
    // Keep it that way: any new reader from another thread must add explicit synchronization.
    private bool _hasPendingDtmfEvent;
    private uint _pendingDtmfSsrc;
    private uint _pendingDtmfTimestamp;
    private byte _pendingDtmfToneCode;
    private ushort _pendingDtmfDurationRtpUnits;
    private bool _pendingDtmfCompleted;
    private int _disposed;

    /// <inheritdoc />
    public event Action<CallAudioFrame>? FrameReceived;

    /// <inheritdoc />
    public event Action<byte, int>? DtmfReceived;

    /// <inheritdoc />
    public event Action<CallMediaRuntimeMetrics>? RuntimeMetricsUpdated;

    /// <inheritdoc />
    public event Action<byte[]>? RtcpMuxDatagramReceived;

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
        _negotiatedPayloadType = parameters.PayloadType;
        _payloadTypeCodecMap = parameters.PayloadTypeCodecMap ?? EmptyPayloadTypeCodecMap;
        _telephoneEventPayloadType = ResolveTelephoneEventPayloadType(parameters);
        _clockRate = Math.Max(parameters.ClockRate, 1);
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
        _rtp.ControlPacketReceived += OnControlPacketReceived;

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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
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
        var startDurationRtpUnits = (ushort)Math.Max(1, durationRtpUnits / 2);
        var eventTimestamp = _rtp.GetCurrentTimestamp();

        var startPacketPayload = RtpTelephoneEventCodec.BuildPayload(
            toneCode,
            endOfEvent: false,
            durationRtpUnits: startDurationRtpUnits);
        var endPacketPayload = RtpTelephoneEventCodec.BuildPayload(
            toneCode,
            endOfEvent: true,
            durationRtpUnits: durationRtpUnits);

        await _rtp.SendTimestampedAsync(
                startPacketPayload,
                marker: true,
                payloadType: (byte)payloadType,
                timestamp: eventTimestamp,
                cancellationToken: ct)
            .ConfigureAwait(false);
        await _rtp.SendTimestampedAsync(
                endPacketPayload,
                marker: false,
                payloadType: (byte)payloadType,
                timestamp: eventTimestamp,
                cancellationToken: ct)
            .ConfigureAwait(false);
        // RFC 4733 reliability recommendation: repeat final packet.
        await _rtp.SendTimestampedAsync(
                endPacketPayload,
                marker: false,
                payloadType: (byte)payloadType,
                timestamp: eventTimestamp,
                cancellationToken: ct)
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

        lock (_rtcpStatsSync)
        {
            var packetsExpected = CalculatePacketsExpected();
            var packetsReceivedForRtcp = _packetsReceivedForRtcp;
            var cumulativeLost = ClampSigned24((long)packetsExpected - packetsReceivedForRtcp);

            var expectedInterval = packetsExpected - _priorExpectedForFraction;
            var receivedInterval = packetsReceivedForRtcp - _priorReceivedForFraction;
            var lostInterval = (long)expectedInterval - receivedInterval;
            var fractionLost = ComputeFractionLost(expectedInterval, lostInterval);

            _priorExpectedForFraction = packetsExpected;
            _priorReceivedForFraction = packetsReceivedForRtcp;

            var localLossPercent = packetsExpected == 0
                ? 0
                : Math.Max(0, cumulativeLost) * 100.0 / packetsExpected;
            var remoteSsrc = _hasRemoteSsrc ? (uint?)_remoteSsrc : null;
            var extendedHighest = !_hasInboundRtcpStats
                ? 0
                : _sequenceCycles + _maxSequence;

            return new CallMediaRtpSnapshot(
                CapturedAtUtc: now,
                LocalSsrc: sender.LocalSsrc,
                RemoteSsrc: remoteSsrc,
                SenderPacketCount: sender.SenderPacketCount,
                SenderOctetCount: sender.SenderOctetCount,
                LastSentRtpTimestamp: sender.LastSentRtpTimestamp,
                HasSentRtpPackets: sender.HasSentPackets,
                PacketsExpected: packetsExpected,
                PacketsReceived: packetsReceivedForRtcp,
                FractionLost: fractionLost,
                CumulativePacketsLost: cumulativeLost,
                ExtendedHighestSequenceNumber: extendedHighest,
                InterarrivalJitterRtpUnits: jitterRtpUnits,
                LocalReceiveJitterMs: jitterMs,
                LocalReceivePacketLossPercent: localLossPercent,
                LocalRoundTripTimeHintMs: localRttHintMs);
        }
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
        var isTelephoneEventPacket = IsTelephoneEventPayloadType(packet.PayloadType);
        TrackInboundPayloadType(packet.PayloadType);
        TrackInboundStatistics(packet);
        Interlocked.Increment(ref _packetsReceived);

        if (isTelephoneEventPacket)
        {
            if (_hasLastDeliveredSequence)
                _lastDeliveredSequence = packet.SequenceNumber;

            HandleInboundTelephoneEvent(packet);
            return;
        }

        var addResult = _jitterBuffer.Add(packet, DateTimeOffset.UtcNow);
        HandleJitterBufferAddResult(addResult, packet);
    }

    private void OnControlPacketReceived(byte[] datagram)
    {
        if (datagram.Length == 0)
            return;

        try
        {
            RtcpMuxDatagramReceived?.Invoke(datagram);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unhandled exception while dispatching RTCP-MUX datagram.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _rtp.PacketReceived -= OnPacketReceived;
        _rtp.ControlPacketReceived -= OnControlPacketReceived;
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
        RtcpMuxDatagramReceived = null;
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
            var packet = _jitterBuffer.TryGetNext(DateTimeOffset.UtcNow);
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
        _lastDeliveredSequence = packet.SequenceNumber;
        _hasLastDeliveredSequence = true;
        Interlocked.Increment(ref _packetsDelivered);
    }

    private void EmitConcealmentFramesIfNeeded(ushort incomingSequenceNumber, byte incomingPayloadType, int incomingPayloadLength)
    {
        if (!_hasLastDeliveredSequence)
            return;

        var expectedSequence = unchecked((ushort)(_lastDeliveredSequence + 1));
        var gapSize = (ushort)(incomingSequenceNumber - expectedSequence);
        if (gapSize == 0)
            return;

        var concealmentCount = Math.Min((int)gapSize, MaxConcealmentBurstPackets);
        for (var i = 0; i < concealmentCount; i++)
        {
            var concealedPayload = CreateConcealmentPayload(incomingPayloadType, incomingPayloadLength);
            DispatchFrame(concealedPayload, incomingPayloadType);
            _lastDeliveredSequence = unchecked((ushort)(_lastDeliveredSequence + 1));
            Interlocked.Increment(ref _packetsConcealed);
        }

        if (gapSize > concealmentCount)
            Interlocked.Add(ref _packetsUnrecoverableLoss, gapSize - concealmentCount);
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
                Interlocked.Increment(ref _packetsQueued);
                break;

            case JitterBufferAddResult.Late:
                Interlocked.Increment(ref _packetsDroppedLate);
                _logger.LogDebug(
                    "RTP packet dropped as late in jitter buffer: seq={Seq}, ts={Timestamp}, pt={PayloadType}, ssrc={Ssrc:X8}.",
                    packet.SequenceNumber,
                    packet.Timestamp,
                    packet.PayloadType,
                    packet.Ssrc);
                break;

            case JitterBufferAddResult.Overflow:
                Interlocked.Increment(ref _packetsDroppedOverflow);
                _logger.LogDebug(
                    "RTP packet dropped due to jitter buffer overflow: seq={Seq}, ts={Timestamp}, ssrc={Ssrc:X8}.",
                    packet.SequenceNumber,
                    packet.Timestamp,
                    packet.Ssrc);
                break;

            case JitterBufferAddResult.Duplicate:
                Interlocked.Increment(ref _packetsDroppedDuplicate);
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
        => new(
            capturedAtUtc: timestamp,
            packetsReceived: Interlocked.Read(ref _packetsReceived),
            packetsQueued: Interlocked.Read(ref _packetsQueued),
            packetsDelivered: Interlocked.Read(ref _packetsDelivered),
            packetsDroppedLate: Interlocked.Read(ref _packetsDroppedLate),
            packetsDroppedOverflow: Interlocked.Read(ref _packetsDroppedOverflow),
            packetsDroppedDuplicate: Interlocked.Read(ref _packetsDroppedDuplicate),
            packetsConcealed: Interlocked.Read(ref _packetsConcealed),
            packetsUnrecoverableLoss: Interlocked.Read(ref _packetsUnrecoverableLoss),
            bufferedPackets: _jitterBuffer.BufferedCount,
            estimatedJitterMs: _jitterBuffer.EstimatedJitterMs,
            adaptiveDelayMs: _jitterBuffer.CurrentDelayMs,
            estimatedRoundTripTimeMs: _jitterBuffer.EstimatedRoundTripTimeMs);


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

    private void TrackInboundStatistics(RtpPacket packet)
    {
        lock (_rtcpStatsSync)
        {
            if (!_hasRemoteSsrc || _remoteSsrc != packet.Ssrc)
            {
                _remoteSsrc = packet.Ssrc;
                _hasRemoteSsrc = true;
                ResetInboundRtcpStatistics(packet.SequenceNumber);
                return;
            }

            _packetsReceivedForRtcp++;

            if (IsSequenceNewer(packet.SequenceNumber, _maxSequence))
            {
                if (packet.SequenceNumber < _maxSequence)
                    _sequenceCycles += 1u << 16;

                _maxSequence = packet.SequenceNumber;
            }
        }
    }

    private void ResetInboundRtcpStatistics(ushort firstSequenceNumber)
    {
        _hasInboundRtcpStats = true;
        _baseSequence = firstSequenceNumber;
        _maxSequence = firstSequenceNumber;
        _sequenceCycles = 0;
        _packetsReceivedForRtcp = 1;
        _priorExpectedForFraction = 0;
        _priorReceivedForFraction = 0;
    }

    private uint CalculatePacketsExpected()
    {
        if (!_hasInboundRtcpStats)
            return 0;

        return _sequenceCycles + _maxSequence - _baseSequence + 1;
    }

    private static byte ComputeFractionLost(uint expectedInterval, long lostInterval)
    {
        if (expectedInterval == 0 || lostInterval <= 0)
            return 0;

        var scaled = (lostInterval << 8) / expectedInterval;
        return (byte)Math.Clamp(scaled, 0, 255);
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

    private static int ClampSigned24(long value)
    {
        const int min = -8_388_608;
        const int max = 8_388_607;
        return (int)Math.Clamp(value, min, max);
    }

    private static bool IsSequenceNewer(ushort sequenceNumber, ushort reference)
        => unchecked((short)(sequenceNumber - reference)) > 0;

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

    private void HandleInboundTelephoneEvent(RtpPacket packet)
    {
        if (!RtpTelephoneEventCodec.TryParse(
                packet.Payload.Span,
                out var toneCode,
                out var endOfEvent,
                out var durationRtpUnits))
        {
            _logger.LogDebug(
                "Ignoring malformed telephone-event RTP payload from SSRC={Ssrc:X8} (payloadLength={PayloadLength}).",
                packet.Ssrc,
                packet.Payload.Length);
            return;
        }

        if (toneCode > 15)
        {
            _logger.LogDebug(
                "Ignoring unsupported telephone-event code {ToneCode}; supported range is 0-15.",
                toneCode);
            return;
        }

        var isSameEvent =
            _hasPendingDtmfEvent &&
            _pendingDtmfSsrc == packet.Ssrc &&
            _pendingDtmfTimestamp == packet.Timestamp &&
            _pendingDtmfToneCode == toneCode;

        if (!isSameEvent)
        {
            _hasPendingDtmfEvent = true;
            _pendingDtmfSsrc = packet.Ssrc;
            _pendingDtmfTimestamp = packet.Timestamp;
            _pendingDtmfToneCode = toneCode;
            _pendingDtmfDurationRtpUnits = durationRtpUnits;
            _pendingDtmfCompleted = false;
        }
        else if (durationRtpUnits > _pendingDtmfDurationRtpUnits)
        {
            _pendingDtmfDurationRtpUnits = durationRtpUnits;
        }

        if (!endOfEvent || _pendingDtmfCompleted)
            return;

        _pendingDtmfCompleted = true;
        var durationMs = RtpTelephoneEventCodec.DurationRtpUnitsToMs(_pendingDtmfDurationRtpUnits, _clockRate);
        DispatchInboundDtmf(toneCode, durationMs);
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
