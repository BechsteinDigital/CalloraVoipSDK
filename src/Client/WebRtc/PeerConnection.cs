using System.Net;
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
    private EventHandler<PeerConnectionState>? _connectionStateChanged;
    private EventHandler<RemoteTrack>? _trackReceived;

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

    public string CreateOffer() => _peer.CreateOffer();

    public async Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
    {
        var localDescription = await _peer.SetRemoteDescriptionAsync(remoteSdp, cancellationToken).ConfigureAwait(false);
        MaterializeRemoteTracks();
        return localDescription;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _peer.StartAsync(cancellationToken);

    public IDisposable AttachMediaTap(IMediaTap tap) => _taps.Attach(tap);

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        _taps.Audio(MediaDirection.Outbound, payload);
        return _peer.SendAudioAsync(payload, cancellationToken);
    }

    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
    {
        _taps.Video(MediaDirection.Outbound, encodedFrame, rtpTimestamp, isKeyFrame: false);
        return _peer.SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _peer.ConnectionStateChanged -= OnInternalStateChanged;
        _peer.AudioReceived -= OnAudioReceived;
        _peer.VideoFrameReceived -= OnVideoReceived;
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
        _taps.Video(MediaDirection.Inbound, frame, rtpTimestamp, isKeyFrame);
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
