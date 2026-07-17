using System.Net;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;

namespace CalloraVoipSdk.WebRtc;

/// <summary>
/// Surfaces the internal <see cref="WebRtcPeerConnection"/> as the public <see cref="IPeerConnection"/>,
/// mapping the internal state enum and its <see cref="Action{T}"/> event onto the public contract. Owns
/// the peer and disposes it.
/// </summary>
internal sealed class PeerConnection : IPeerConnection
{
    private readonly WebRtcPeerConnection _peer;
    private EventHandler<PeerConnectionState>? _connectionStateChanged;

    public PeerConnection(WebRtcPeerConnection peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _peer = peer;
        _peer.ConnectionStateChanged += OnInternalStateChanged;
    }

    public PeerConnectionState State => Map(_peer.State);
    public string? LocalDescription => _peer.LocalDescription;
    public IPEndPoint? LocalMediaEndPoint => _peer.LocalMediaEndPoint;

    public event EventHandler<PeerConnectionState>? ConnectionStateChanged
    {
        add => _connectionStateChanged += value;
        remove => _connectionStateChanged -= value;
    }

    public string CreateOffer() => _peer.CreateOffer();

    public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
        => _peer.SetRemoteDescriptionAsync(remoteSdp, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _peer.StartAsync(cancellationToken);

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _peer.SendAudioAsync(payload, cancellationToken);

    public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default)
        => _peer.SendVideoFrameAsync(encodedFrame, rtpTimestamp, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _peer.ConnectionStateChanged -= OnInternalStateChanged;
        await _peer.DisposeAsync().ConfigureAwait(false);
    }

    private void OnInternalStateChanged(WebRtcConnectionState state)
        => _connectionStateChanged?.Invoke(this, Map(state));

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
