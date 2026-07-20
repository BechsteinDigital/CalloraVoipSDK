using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
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
    private readonly BitrateMeter _outgoingBitrate = new();
    private readonly BitrateMeter _incomingBitrate = new();
    private readonly RateMeter _frameRate = new();
    private readonly object _statsSync = new();
    private EventHandler<PeerConnectionState>? _connectionStateChanged;
    private EventHandler<RemoteTrack>? _trackReceived;
    private EventHandler<string>? _localIceCandidateDiscovered;
    private EventHandler<DtmfTone>? _dtmfReceived;

    public PeerConnection(WebRtcPeerConnection peer, ILogger<PeerConnection> logger, Action<IPeerConnection>? onDisposed = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(logger);
        _peer = peer;
        _onDisposed = onDisposed;
        _tracks = new RemoteTrackSet(track => _trackReceived?.Invoke(this, track));
        _taps = new MediaTapSet(logger);
        _peer.ConnectionStateChanged += OnInternalStateChanged;
        _peer.AudioReceived += OnAudioReceived;
        _peer.VideoFrameReceived += OnVideoReceived;
        _peer.LocalIceCandidateDiscovered += OnLocalIceCandidate;
        _peer.DtmfReceived += OnDtmfReceived;
    }

    public PeerConnectionState State => Map(_peer.State);
    public string? LocalDescription => _peer.LocalDescription;
    public IPEndPoint? LocalMediaEndPoint => _peer.LocalMediaEndPoint;

    public event EventHandler<PeerConnectionState>? ConnectionStateChanged
    {
        add => _connectionStateChanged += value;
        remove => _connectionStateChanged -= value;
    }

    public event EventHandler<RemoteTrack>? TrackReceived
    {
        add => _trackReceived += value;
        remove => _trackReceived -= value;
    }

    public event EventHandler<string>? LocalIceCandidateDiscovered
    {
        add => _localIceCandidateDiscovered += value;
        remove => _localIceCandidateDiscovered -= value;
    }

    public event EventHandler<DtmfTone>? DtmfReceived
    {
        add => _dtmfReceived += value;
        remove => _dtmfReceived -= value;
    }

    public string CreateOffer() => _peer.CreateOffer();

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
            var nowTicks = DateTime.UtcNow.Ticks;
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
            // Dropped frames, NACK/PLI/FIR and available-bitrate stay null until their subsystems (bundle video
            // feedback, transport-cc) are wired.
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

    public async ValueTask DisposeAsync()
    {
        _peer.ConnectionStateChanged -= OnInternalStateChanged;
        _peer.AudioReceived -= OnAudioReceived;
        _peer.VideoFrameReceived -= OnVideoReceived;
        _peer.LocalIceCandidateDiscovered -= OnLocalIceCandidate;
        _peer.DtmfReceived -= OnDtmfReceived;
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
            _tracks.EnsureAudioTrack(StreamId(msid), msid?.TrackId);
        }
        if (_peer.HasRemoteVideo)
        {
            var msid = _peer.RemoteVideoMsid;
            _tracks.EnsureVideoTrack(StreamId(msid), msid?.TrackId);
        }
    }

    private void OnInternalStateChanged(WebRtcConnectionState state)
        => _connectionStateChanged?.Invoke(this, Map(state));

    private void OnLocalIceCandidate(string candidate)
        => _localIceCandidateDiscovered?.Invoke(this, candidate);

    private void OnDtmfReceived(byte toneCode, int durationMs)
        => _dtmfReceived?.Invoke(this, new DtmfTone(toneCode, durationMs));

    // Inbound media is projected onto the W3C track model via the RemoteTrackSet: the remote a=msid names
    // the track, and the set raises TrackReceived once per kind before the first frame flows.
    private void OnAudioReceived(byte[] payload)
    {
        _taps.Audio(MediaDirection.Inbound, payload);
        var msid = _peer.RemoteAudioMsid;
        _tracks.DeliverAudioFrame(StreamId(msid), msid?.TrackId, new EncodedFrame(payload, rtpTimestamp: null, isKeyFrame: false, presentationTimeUsec: null));
    }

    private void OnVideoReceived(byte[] frame, uint rtpTimestamp, bool isKeyFrame)
    {
        // Inbound RID demux is a later slice; the layer is not yet distinguished on the receive path.
        _taps.Video(MediaDirection.Inbound, frame, rtpTimestamp, isKeyFrame, rid: null);
        var msid = _peer.RemoteVideoMsid;
        _tracks.DeliverVideoFrame(StreamId(msid), msid?.TrackId, new EncodedFrame(frame, rtpTimestamp, isKeyFrame, presentationTimeUsec: null));
    }

    // RFC 8830: a stream id of "-" means the track belongs to no MediaStream.
    private static string? StreamId(SdpMsid? msid)
        => msid is null || msid.StreamId == "-" ? null : msid.StreamId;

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
}
