using System.Diagnostics;
using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Surfaces the internal <see cref="WebRtcPeerConnection"/> as the public <see cref="IPeerConnection"/>,
/// mapping the internal state enum and its <see cref="Action{T}"/> events onto the public contract,
/// projecting inbound media onto the W3C track model (<see cref="TrackReceived"/>), and fanning both
/// directions out to attached L3 media taps. Owns the peer and disposes it.
/// </summary>
internal sealed class PeerConnection : IPeerConnection
{
    private readonly WebRtcPeerConnection _peer;
    private readonly RemoteTrackSet _tracks;
    private readonly MediaTapSet _taps;
    private readonly Action<IPeerConnection>? _onDisposed;
    // The client's default video codecs (config VideoCodecs) for a track added without explicit codecs.
    private readonly IReadOnlyList<string> _defaultVideoCodecs;
    // The client's default audio codecs (config AudioCodecs) for an added audio track without explicit codecs.
    private readonly IReadOnlyList<string> _defaultAudioCodecs;
    private readonly BitrateMeter _outgoingBitrate = new();
    private readonly BitrateMeter _incomingBitrate = new();
    private readonly RateMeter _frameRate = new();
    private readonly object _statsSync = new();
    // Guards the event-handler multicast fields against lost handlers under concurrent subscribe/unsubscribe.
    // The default field-like event accessors this replaces used a plain += / -=, which is not atomic, so
    // two threads racing on subscribe could drop a handler. Every add/remove and every fire snapshots under
    // this lock (invoking outside it, so a handler cannot deadlock by re-subscribing).
    private readonly object _eventSync = new();
    private EventHandler<PeerConnectionState>? _connectionStateChanged;
    private EventHandler<SignalingState>? _signalingStateChanged;
    private EventHandler<RemoteTrack>? _trackReceived;
    private EventHandler<string>? _localIceCandidateDiscovered;
    private EventHandler<DtmfTone>? _dtmfReceived;
    private EventHandler? _videoKeyFrameRequested;
    private EventHandler<BitrateRecommendation>? _recommendedBitrateChanged;

    public PeerConnection(
        WebRtcPeerConnection peer,
        ILogger<PeerConnection> logger,
        Action<IPeerConnection>? onDisposed = null,
        IReadOnlyList<string>? defaultVideoCodecs = null,
        IReadOnlyList<string>? defaultAudioCodecs = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(logger);
        _peer = peer;
        _onDisposed = onDisposed;
        _defaultVideoCodecs = defaultVideoCodecs ?? [];
        _defaultAudioCodecs = defaultAudioCodecs ?? [];
        _tracks = new RemoteTrackSet(RaiseTrackReceived);
        _taps = new MediaTapSet(logger);
        _peer.ConnectionStateChanged += OnInternalStateChanged;
        _peer.SignalingStateChanged += OnInternalSignalingStateChanged;
        _peer.AudioReceived += OnAudioReceived;
        // Inbound audio for the ADDITIONAL tracks (4.7.0: N remote audio m-lines) arrives MID-tagged; the primary
        // stays on the mid-less OnAudioReceived. The two paths are disjoint (the peer never fires the mid-tagged
        // event for the primary), so subscribing to both delivers each track exactly once.
        _peer.AudioTrackFrameReceived += OnAudioTrackReceived;
        // Inbound video is projected via the MID-tagged event only (P2c): the peer fires it for EVERY video
        // track — including the primary, for which the legacy untagged VideoFrameReceived also fires — so
        // subscribing to both would double-deliver the primary track's frames. The MID-tagged path covers the
        // 1+1 case (one MID) and the N case (one per m-line) with a single subscription.
        _peer.VideoTrackFrameReceived += OnVideoTrackReceived;
        // Inbound simulcast layers (4.7.0, RFC 8853) arrive per (mid, rid) on VideoLayerFrameReceived; the peer
        // fires it ONLY for RID-tagged layers (the primary RID-less stream stays on VideoTrackFrameReceived), so
        // subscribing to both delivers each frame exactly once — no double-delivery.
        _peer.VideoLayerFrameReceived += OnVideoLayerReceived;
        _peer.LocalIceCandidateDiscovered += OnLocalIceCandidate;
        _peer.DtmfReceived += OnDtmfReceived;
        _peer.VideoKeyFrameRequested += OnVideoKeyFrameRequested;
        _peer.RecommendedBitrateChanged += OnRecommendedBitrateChanged;
    }

    public PeerConnectionState State => Map(_peer.State);
    public SignalingState SignalingState => MapSignaling(_peer.SignalingState);
    public string? LocalDescription => _peer.LocalDescription;
    public IPEndPoint? LocalMediaEndPoint => _peer.LocalMediaEndPoint;
    public long? RecommendedOutgoingBitrateBps => _peer.RecommendedOutgoingBitrateBps;

    public event EventHandler<PeerConnectionState>? ConnectionStateChanged
    {
        add { lock (_eventSync) _connectionStateChanged += value; }
        remove { lock (_eventSync) _connectionStateChanged -= value; }
    }

    public event EventHandler<SignalingState>? SignalingStateChanged
    {
        add { lock (_eventSync) _signalingStateChanged += value; }
        remove { lock (_eventSync) _signalingStateChanged -= value; }
    }

    public event EventHandler<RemoteTrack>? TrackReceived
    {
        add { lock (_eventSync) _trackReceived += value; }
        remove { lock (_eventSync) _trackReceived -= value; }
    }

    public event EventHandler<string>? LocalIceCandidateDiscovered
    {
        add { lock (_eventSync) _localIceCandidateDiscovered += value; }
        remove { lock (_eventSync) _localIceCandidateDiscovered -= value; }
    }

    public event EventHandler<DtmfTone>? DtmfReceived
    {
        add { lock (_eventSync) _dtmfReceived += value; }
        remove { lock (_eventSync) _dtmfReceived -= value; }
    }

    public event EventHandler? VideoKeyFrameRequested
    {
        add { lock (_eventSync) _videoKeyFrameRequested += value; }
        remove { lock (_eventSync) _videoKeyFrameRequested -= value; }
    }

    public event EventHandler<BitrateRecommendation>? RecommendedBitrateChanged
    {
        add { lock (_eventSync) _recommendedBitrateChanged += value; }
        remove { lock (_eventSync) _recommendedBitrateChanged -= value; }
    }

    public string CreateOffer() => _peer.CreateOffer();

    public IAudioTrack AddAudioTrack() => AddAudioTrack(new AudioTrackOptions());

    public IAudioTrack AddAudioTrack(AudioTrackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Resolve the track's codecs: explicit names, else the client's configured default audio codecs.
        // WebRtcCodecCatalog.Audio throws on unknown names (consistent with the primary-audio path) before the
        // track is added, so an unusable offer never reaches the wire.
        var codecNames = options.Codecs ?? _defaultAudioCodecs;
        var codecs = codecNames.Select(WebRtcCodecCatalog.Audio).ToArray();

        var mid = _peer.AddAudioTrack(new WebRtcAddedAudioTrack
        {
            Codecs = codecs,
            Direction = MapDirection(options.Direction),
            StreamId = options.StreamId,
        });

        // The handle routes each send through this facade's tap fan-out (so a recorder/analytics sees the
        // outbound payload) and the peer's mid-targeted send-lease path (drained against dispose).
        return new AudioTrack(
            mid,
            options.Direction,
            (frame, ts, ct) =>
            {
                _taps.Audio(MediaDirection.Outbound, frame);
                return _peer.SendAudioTrackFrameAsync(mid, frame, ts, ct);
            });
    }

    public IVideoTrack AddVideoTrack() => AddVideoTrack(new VideoTrackOptions());

    public IVideoTrack AddVideoTrack(VideoTrackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Resolve the track's codecs: explicit names, else the client's configured default video codecs.
        // VideoCodecCatalog rejects unknown names (consistent with the EnableVideo path) before the track is added.
        var codecNames = options.Codecs ?? _defaultVideoCodecs;
        foreach (var name in codecNames)
        {
            if (!VideoCodecCatalog.IsSupported(name))
                throw new ArgumentException($"Unknown WebRTC video codec '{name}'.", nameof(options));
        }

        var mid = _peer.AddVideoTrack(new WebRtcAddedVideoTrack
        {
            Codecs = VideoCodecCatalog.Resolve(codecNames),
            Direction = MapDirection(options.Direction),
            SimulcastSendRids = options.SimulcastSendRids,
            StreamId = options.StreamId,
        });

        // The handle routes each send through this facade's tap fan-out (so a recorder/analytics sees the
        // outbound frame) and the peer's mid-targeted send-lease path (drained against dispose).
        return new VideoTrack(
            mid,
            options.Direction,
            (frame, ts, ct) =>
            {
                _taps.Video(MediaDirection.Outbound, frame, ts, isKeyFrame: false, rid: null);
                return _peer.SendVideoTrackFrameAsync(mid, frame, ts, ct);
            },
            (rid, frame, ts, ct) =>
            {
                _taps.Video(MediaDirection.Outbound, frame, ts, isKeyFrame: false, rid: rid);
                return _peer.SendVideoTrackFrameAsync(mid, rid, frame, ts, ct);
            });
    }

    private static SdpMediaDirection MapDirection(TrackDirection direction) => direction switch
    {
        TrackDirection.SendRecv => SdpMediaDirection.SendRecv,
        TrackDirection.SendOnly => SdpMediaDirection.SendOnly,
        TrackDirection.RecvOnly => SdpMediaDirection.RecvOnly,
        TrackDirection.Inactive => SdpMediaDirection.Inactive,
        _ => SdpMediaDirection.SendRecv,
    };

    public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default)
        => _peer.AddIceCandidateAsync(candidate, cancellationToken);

    public async Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
    {
        var localDescription = await _peer.SetRemoteDescriptionAsync(remoteSdp, cancellationToken).ConfigureAwait(false);
        MaterializeRemoteTracks();
        return localDescription;
    }

    public Task GatherCandidatesAsync(CancellationToken cancellationToken = default)
        => _peer.GatherCandidatesAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _peer.StartAsync(cancellationToken);

    public IDisposable AttachMediaTap(IMediaTap tap) => _taps.Attach(tap);

    public WebRtcStats GetStats()
    {
        var state = Map(_peer.State);
        if (_peer.GetStats() is not { } s)
        {
            // No media session yet: report the state with zero counters, not fabricated values.
            return new WebRtcStats { ConnectionState = state, IceState = IceConnectionState(state) };
        }

        double? outgoing, incoming, framesPerSecond;
        lock (_statsSync)
        {
            // Monotone clock for rate deltas (RFC-agnostic, but consistent with the Core media path which
            // was moved off wall-clock): a wall-clock NTP step would otherwise inflate or negate a bitrate.
            // The meters only consume the delta, so any monotone unit works as long as it is 100 ns ticks.
            var nowTicks = MonotonicTicks();
            outgoing = _outgoingBitrate.Sample(s.BytesSent, nowTicks);
            incoming = _incomingBitrate.Sample(s.BytesReceived, nowTicks);
            framesPerSecond = s.FramesReceived is { } frames ? _frameRate.Sample(frames, nowTicks) : null;
        }

        // RTCP-derived outbound quality (RFC 3550 §6.4.1): round-trip time and the loss the peer reports on our
        // media. Null until the peer has echoed a matching report, so early snapshots report null, not a zero.
        var quality = _peer.GetQuality();

        // Per-stream breakdown (CF-004f): outbound RTT/loss per our sending SSRC folded by MID, inbound jitter
        // per remote source. Projected onto the public per-stream type; the scalars above stay the worst-of.
        var mediaStreams = MapStreamQuality(_peer.GetStreamQuality());

        return new WebRtcStats
        {
            ConnectionState = state,
            PacketsSent = s.PacketsSent,
            BytesSent = s.BytesSent,
            PacketsReceived = s.PacketsReceived,
            BytesReceived = s.BytesReceived,
            SuppressedSends = s.SuppressedSends,
            DroppedDatagrams = s.DroppedDatagrams,
            OutgoingBitrateBps = outgoing,
            IncomingBitrateBps = incoming,
            PacketLoss = quality?.RemotePacketLossFraction,
            RoundTripTimeMs = quality?.RoundTripTimeMs,
            // Local receive-side interarrival jitter in ms (RFC 3550 §A.8), converted with the negotiated audio
            // clock rate; null until an inbound clock is established (CF-004e).
            JitterMs = quality?.JitterMs,
            MediaStreams = mediaStreams,
            // ICE: the bundle uses single-candidate selection (no full pairing), so the "selected pair" is
            // the bound local endpoint and the resolved remote endpoint; the state is derived from
            // connectivity (ICE consent + DTLS drive the peer state).
            IceState = IceConnectionState(state),
            SelectedLocalCandidate = _peer.LocalMediaEndPoint?.ToString(),
            SelectedRemoteCandidate = _peer.RemoteMediaEndPoint?.ToString(),
            FramesPerSecond = framesPerSecond,
            KeyFrames = s.KeyFrames,
            FramesDropped = s.FramesDropped,
            // Video RTCP feedback we sent the peer on detected inbound loss (RFC 4585) and the sender-side
            // congestion estimate (transport-cc / RFC 8888) — both null until a video track / the extension is
            // negotiated. FirCount stays null: the bundle honours an inbound FIR as a keyframe request but never
            // generates FIR (PLI is the keyframe fallback), so there is no sent-FIR count to report.
            NackCount = s.NacksSent,
            PliCount = s.PlisSent,
            AvailableOutgoingBitrateBps = s.AvailableOutgoingBitrateBps,
        };
    }

    // Projects the internal per-stream quality (CF-004f) onto the public per-stream stats type: the media kind
    // enum maps to the W3C-style "audio"/"video" label ("unknown" for an unattributed inbound source).
    private static IReadOnlyList<WebRtcMediaStreamStats> MapStreamQuality(IReadOnlyList<BundledStreamQuality> streams)
    {
        if (streams.Count == 0)
            return [];

        var result = new List<WebRtcMediaStreamStats>(streams.Count);
        foreach (var s in streams)
        {
            result.Add(new WebRtcMediaStreamStats
            {
                Mid = s.Mid,
                Ssrc = s.Ssrc,
                Kind = KindLabel(s.Kind),
                PacketLoss = s.PacketLoss,
                JitterMs = s.JitterMs,
                RoundTripTimeMs = s.RoundTripTimeMs,
            });
        }

        return result;
    }

    private static string KindLabel(BundledStreamKind kind) => kind switch
    {
        BundledStreamKind.Audio => "audio",
        BundledStreamKind.Video => "video",
        _ => "unknown",
    };

    // A W3C RTCIceConnectionState-style label derived from the peer's connectivity (the bundle's media path
    // is gated on ICE consent + DTLS; it does not run a separate multi-pair ICE checklist).
    private static string IceConnectionState(PeerConnectionState state) => state switch
    {
        PeerConnectionState.New          => "new",
        PeerConnectionState.Connecting   => "checking",
        PeerConnectionState.Connected    => "connected",
        PeerConnectionState.Disconnected => "disconnected",
        PeerConnectionState.Failed       => "failed",
        PeerConnectionState.Closed       => "closed",
        _ => "closed",
    };

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        _taps.Audio(MediaDirection.Outbound, payload);
        return _peer.SendAudioAsync(payload, cancellationToken);
    }

    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
    {
        _taps.Video(MediaDirection.Outbound, encodedFrame, rtpTimestamp, isKeyFrame: false, rid: null);
        return _peer.SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken);
    }

    public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
    {
        // A blank rid on the simulcast overload would reach the tap as a layer id indistinguishable from the
        // single-stream null contract — reject it up front so the tap's rid is always a real layer or null.
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        // Tag the outbound tap with the simulcast layer id so a recorder/analytics can separate the layers.
        _taps.Video(MediaDirection.Outbound, encodedFrame, rtpTimestamp, isKeyFrame: false, rid: rid);
        return _peer.SendVideoFrameAsync(rid, encodedFrame, rtpTimestamp, cancellationToken);
    }

    public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken cancellationToken = default)
        => _peer.SendDtmfAsync(toneCode, durationMs, cancellationToken);

    public ValueTask<bool> RequestVideoKeyFrameAsync(CancellationToken cancellationToken = default)
        => _peer.RequestVideoKeyFrameAsync(cancellationToken);

    public ValueTask<bool> RequestVideoKeyFrameAsync(string mid, CancellationToken cancellationToken = default)
        => _peer.RequestVideoKeyFrameAsync(mid, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _peer.ConnectionStateChanged -= OnInternalStateChanged;
        _peer.SignalingStateChanged -= OnInternalSignalingStateChanged;
        _peer.AudioReceived -= OnAudioReceived;
        _peer.AudioTrackFrameReceived -= OnAudioTrackReceived;
        _peer.VideoTrackFrameReceived -= OnVideoTrackReceived;
        _peer.VideoLayerFrameReceived -= OnVideoLayerReceived;
        _peer.LocalIceCandidateDiscovered -= OnLocalIceCandidate;
        _peer.DtmfReceived -= OnDtmfReceived;
        _peer.VideoKeyFrameRequested -= OnVideoKeyFrameRequested;
        _peer.RecommendedBitrateChanged -= OnRecommendedBitrateChanged;
        try
        {
            await _peer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Untrack from the peer manager even if the inner dispose throws, so a failed teardown
            // never leaves a dead peer registered.
            _onDisposed?.Invoke(this);
        }
    }

    // W3C ontrack semantics: materialise the remote tracks the moment the remote description is applied
    // (before any media flows), so a handler can subscribe to FrameReceived up front. Later frames route to
    // the already-created track. Falls back to first-frame materialisation if a description was not applied.
    private void MaterializeRemoteTracks()
    {
        if (_peer.HasRemoteAudio)
        {
            var msid = _peer.RemoteAudioMsid;
            // The primary audio anchor is the mid-less track (keyed under the empty string), materialised from the
            // has-remote-audio flag exactly as the pre-4.7.0 single-audio path.
            _tracks.EnsureAudioTrack(mid: null, StreamId(msid), msid?.TrackId);
        }
        // One RemoteTrack per ADDITIONAL remote audio m-line (4.7.0: N tracks), keyed by MID, so several remote
        // participants' audio streams each surface a distinct track with the right MID/msid.
        foreach (var audio in _peer.RemoteAudioTracks)
            _tracks.EnsureAudioTrack(audio.Mid, StreamId(audio.Msid), audio.Msid?.TrackId);
        // One RemoteTrack per remote video m-line (P2c: N tracks), keyed by MID, so several remote cameras /
        // screen-shares each surface a distinct track with the right MID/msid.
        foreach (var video in _peer.RemoteVideoTracks)
            _tracks.EnsureVideoTrack(video.Mid, StreamId(video.Msid), video.Msid?.TrackId);
    }

    private void OnInternalStateChanged(WebRtcConnectionState state)
    {
        EventHandler<PeerConnectionState>? handler;
        lock (_eventSync) handler = _connectionStateChanged;
        handler?.Invoke(this, Map(state));
    }

    private void OnInternalSignalingStateChanged(WebRtcSignalingState state)
    {
        EventHandler<SignalingState>? handler;
        lock (_eventSync) handler = _signalingStateChanged;
        handler?.Invoke(this, MapSignaling(state));
    }

    private void OnLocalIceCandidate(string candidate)
    {
        EventHandler<string>? handler;
        lock (_eventSync) handler = _localIceCandidateDiscovered;
        handler?.Invoke(this, candidate);
    }

    private void OnDtmfReceived(byte toneCode, int durationMs)
    {
        EventHandler<DtmfTone>? handler;
        lock (_eventSync) handler = _dtmfReceived;
        handler?.Invoke(this, new DtmfTone(toneCode, durationMs));
    }

    // Send-side feedback (RFC 4585/5104): the peer asked for a key frame. Surfaced as a top-level event
    // (like DtmfReceived) rather than on a remote track — it targets our encoder, not an inbound stream.
    private void OnVideoKeyFrameRequested()
    {
        EventHandler? handler;
        lock (_eventSync) handler = _videoKeyFrameRequested;
        handler?.Invoke(this, EventArgs.Empty);
    }

    // The SDK revised its recommended send bitrate for this peer (transport-cc / RFC 8888). Surfaced as a
    // top-level event carrying the finished recommendation (bitrate + coarse quality) — an SFU reacts per
    // receiver. Snapshot the handler under the event lock and invoke outside it (K3), like the other fan-outs.
    private void OnRecommendedBitrateChanged(long bitrateBps, NetworkQuality quality)
    {
        EventHandler<BitrateRecommendation>? handler;
        lock (_eventSync) handler = _recommendedBitrateChanged;
        handler?.Invoke(this, new BitrateRecommendation(bitrateBps, quality));
    }

    // Snapshotted TrackReceived fire path used by the RemoteTrackSet when a remote track materialises.
    private void RaiseTrackReceived(RemoteTrack track)
    {
        EventHandler<RemoteTrack>? handler;
        lock (_eventSync) handler = _trackReceived;
        handler?.Invoke(this, track);
    }

    // Inbound media is projected onto the W3C track model via the RemoteTrackSet: the remote a=msid names
    // the track, and the set raises TrackReceived once per kind before the first frame flows.
    private void OnAudioReceived(byte[] payload)
    {
        _taps.Audio(MediaDirection.Inbound, payload);
        var msid = _peer.RemoteAudioMsid;
        // The primary audio anchor is the mid-less track (keyed under the empty string).
        _tracks.DeliverAudioFrame(mid: null, StreamId(msid), msid?.TrackId, new EncodedFrame(payload, rtpTimestamp: null, isKeyFrame: false, presentationTimeUsec: null));
    }

    // Mid-tagged inbound audio (4.7.0: N remote audio tracks — the SFU pattern): route each frame to its own
    // RemoteTrack by MID. The peer fires this only for the additional tracks (never the primary), so a single
    // subscription alongside OnAudioReceived covers 1-audio and N-audio without double-delivering the primary.
    private void OnAudioTrackReceived(string mid, byte[] payload)
    {
        _taps.Audio(MediaDirection.Inbound, payload);
        // The remote m-line's msid for this MID (for stream grouping); null when the remote advertised none.
        var msid = _peer.RemoteAudioTracks.FirstOrDefault(t => string.Equals(t.Mid, mid, StringComparison.Ordinal))?.Msid;
        _tracks.DeliverAudioFrame(mid, StreamId(msid), msid?.TrackId, new EncodedFrame(payload, rtpTimestamp: null, isKeyFrame: false, presentationTimeUsec: null));
    }

    // Mid-tagged inbound video (P2c): route each frame to its own RemoteTrack (by MID). The peer fires this
    // for every video track (primary and added), so a single subscription covers 1+1 and N without the
    // double-delivery the untagged event would cause on the primary.
    private void OnVideoTrackReceived(string mid, byte[] frame, uint rtpTimestamp, bool isKeyFrame)
    {
        // The primary / RID-less stream: no simulcast layer to distinguish (RID-tagged layers arrive on the
        // separate VideoLayerFrameReceived path). After the recv-side simulcast wiring this fires only for
        // the non-simulcast frames, so frame.Rid is always null here.
        _taps.Video(MediaDirection.Inbound, frame, rtpTimestamp, isKeyFrame, rid: null);
        // The remote m-line's msid for this MID (for stream grouping); null when the remote advertised none.
        var msid = _peer.RemoteVideoTracks.FirstOrDefault(t => string.Equals(t.Mid, mid, StringComparison.Ordinal))?.Msid
            ?? _peer.RemoteVideoMsid;
        _tracks.DeliverVideoFrame(mid, StreamId(msid), msid?.TrackId, new EncodedFrame(frame, rtpTimestamp, isKeyFrame, presentationTimeUsec: null, rid: null));
    }

    // Mid-tagged inbound simulcast layer (4.7.0, RFC 8853): the demultiplexed encoding lands on the SAME mid
    // RemoteTrack as the primary stream — each frame is distinguished per EncodedFrame.Rid (an SFU reads
    // frame.Rid to forward the right layer). There is no per-rid RemoteTrack identity: one RemoteTrack per mid.
    private void OnVideoLayerReceived(string mid, string rid, byte[] frame, uint rtpTimestamp, bool isKeyFrame)
    {
        _taps.Video(MediaDirection.Inbound, frame, rtpTimestamp, isKeyFrame, rid: rid);
        // The remote m-line's msid for this MID (for stream grouping); null when the remote advertised none.
        var msid = _peer.RemoteVideoTracks.FirstOrDefault(t => string.Equals(t.Mid, mid, StringComparison.Ordinal))?.Msid
            ?? _peer.RemoteVideoMsid;
        _tracks.DeliverVideoFrame(mid, StreamId(msid), msid?.TrackId, new EncodedFrame(frame, rtpTimestamp, isKeyFrame, presentationTimeUsec: null, rid: rid));
    }

    // RFC 8830: a stream id of "-" means the track belongs to no MediaStream.
    private static string? StreamId(SdpMsid? msid)
        => msid is null || msid.StreamId == "-" ? null : msid.StreamId;

    // Scales the monotone high-resolution counter to 100 ns ticks so the rate meters keep their documented
    // tick contract while being immune to wall-clock jumps. Only successive deltas are consumed, so the
    // absolute origin is irrelevant.
    private static readonly double TicksPerStopwatchTick = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    // Internal (not private) so a test can pin that the rate clock is the uptime-based monotone counter and
    // not the wall clock: its magnitude is the process uptime in 100 ns ticks, far below DateTime.UtcNow.Ticks.
    internal static long MonotonicTicks() => (long)(Stopwatch.GetTimestamp() * TicksPerStopwatchTick);

    private static PeerConnectionState Map(WebRtcConnectionState state) => state switch
    {
        WebRtcConnectionState.New          => PeerConnectionState.New,
        WebRtcConnectionState.Connecting   => PeerConnectionState.Connecting,
        WebRtcConnectionState.Connected    => PeerConnectionState.Connected,
        WebRtcConnectionState.Disconnected => PeerConnectionState.Disconnected,
        WebRtcConnectionState.Failed       => PeerConnectionState.Failed,
        WebRtcConnectionState.Closed       => PeerConnectionState.Closed,
        _ => PeerConnectionState.Closed,
    };

    // Projects the internal RFC 8829 signalling state onto the public enum (1:1; no pranswer path exists).
    private static SignalingState MapSignaling(WebRtcSignalingState state) => state switch
    {
        WebRtcSignalingState.Stable          => SignalingState.Stable,
        WebRtcSignalingState.HaveLocalOffer  => SignalingState.HaveLocalOffer,
        WebRtcSignalingState.HaveRemoteOffer => SignalingState.HaveRemoteOffer,
        WebRtcSignalingState.Closed          => SignalingState.Closed,
        _ => SignalingState.Closed,
    };
}
