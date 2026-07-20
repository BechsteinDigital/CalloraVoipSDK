using System.Collections.Concurrent;
using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Trickle-ICE over the signalling channel (RFC 8838/8840): when the app supplies an
/// <see cref="IWebRtcTrickleSignaling"/>, <see cref="WebRtcPeerConnectionExtensions.ConnectAsync"/> gathers
/// local candidates and sends them out of band, applies remote candidates as they arrive, and signals
/// end-of-candidates — beyond the candidates the offer/answer carried. A plain channel stays SDP-only.
/// </summary>
public sealed class WebRtcTrickleSignalingTests
{
    [Fact]
    public async Task ConnectAsync_trickles_local_candidates_applies_remote_ones_and_signals_end()
    {
        // The peer only reaches Connected once the remote candidate is applied, so the exchange is complete
        // and observable by the time ConnectAsync returns (no timing race).
        var peer = new TricklePeer();
        var signalling = new FakeTrickleSignalling("REMOTE-ANSWER", "candidate:REMOTE-1 1 udp 100 127.0.0.1 40000 typ host");

        await peer.ConnectAsync(signalling, WebRtcRole.Offerer);

        Assert.True(peer.Started);
        Assert.Equal("OFFER", Assert.Single(signalling.SentDescriptions));
        Assert.Contains("candidate:LOCAL-SRFLX", signalling.SentCandidates);          // gathered locally, trickled out
        Assert.Contains("candidate:REMOTE-1 1 udp 100 127.0.0.1 40000 typ host", peer.AppliedCandidates); // remote applied
        Assert.True(signalling.EndOfCandidatesSent);
    }

    [Fact]
    public async Task ConnectAsync_over_a_plain_channel_stays_sdp_only()
    {
        // A non-trickle channel: ConnectAsync must not gather or exchange candidates.
        var peer = new TricklePeer { ConnectOnStart = true };
        var signalling = new FakeSignalling("REMOTE-ANSWER");

        await peer.ConnectAsync(signalling, WebRtcRole.Offerer);

        Assert.True(peer.Started);
        Assert.False(peer.Gathered); // GatherCandidatesAsync was not called on a plain channel
        Assert.Empty(peer.AppliedCandidates);
    }

    // ── fakes ──────────────────────────────────────────────────────────────────

    private sealed class TricklePeer : IPeerConnection
    {
        public bool Started { get; private set; }
        public bool Gathered { get; private set; }
        public bool ConnectOnStart { get; init; }
        public ConcurrentQueue<string> AppliedCandidates { get; } = new();

        private PeerConnectionState _state = PeerConnectionState.New;
        public PeerConnectionState State => _state;
        public string? LocalDescription => "OFFER";
        public IPEndPoint? LocalMediaEndPoint => null;

        public event EventHandler<PeerConnectionState>? ConnectionStateChanged;
        public event EventHandler<RemoteTrack>? TrackReceived { add { } remove { } }
        public event EventHandler<string>? LocalIceCandidateDiscovered;
        public event EventHandler<DtmfTone>? DtmfReceived { add { } remove { } }

        public string CreateOffer() => "OFFER";

        public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
        {
            Raise(PeerConnectionState.Connecting);
            return Task.FromResult("ANSWER");
        }

        public Task GatherCandidatesAsync(CancellationToken cancellationToken = default)
        {
            Gathered = true;
            LocalIceCandidateDiscovered?.Invoke(this, "candidate:LOCAL-SRFLX"); // a "gathered" server-reflexive candidate
            return Task.CompletedTask;
        }

        public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default)
        {
            AppliedCandidates.Enqueue(candidate);
            Raise(PeerConnectionState.Connected); // applying the remote candidate completes the connection
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            if (ConnectOnStart)
                Raise(PeerConnectionState.Connected);
            return Task.CompletedTask;
        }

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendDtmfAsync(byte toneCode, int durationMs = 160, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IDisposable AttachMediaTap(IMediaTap tap) => NoopDisposable.Instance;
        public WebRtcStats GetStats() => new() { ConnectionState = _state };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Raise(PeerConnectionState state)
        {
            _state = state;
            ConnectionStateChanged?.Invoke(this, state);
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeTrickleSignalling(string remoteAnswer, string remoteCandidate) : IWebRtcTrickleSignaling
    {
        public List<string> SentDescriptions { get; } = [];
        public ConcurrentQueue<string> SentCandidates { get; } = new();
        public bool EndOfCandidatesSent { get; private set; }
        private int _remoteDelivered;

        public Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default)
        {
            SentDescriptions.Add(sdp);
            return Task.CompletedTask;
        }

        public Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(remoteAnswer);

        public Task SendCandidateAsync(string candidate, CancellationToken cancellationToken = default)
        {
            SentCandidates.Enqueue(candidate);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveCandidateAsync(CancellationToken cancellationToken = default)
        {
            // Deliver the one remote candidate, then block on the token until the connection resolves so the
            // pump does not spin (a real channel awaits the next signalled candidate).
            if (Interlocked.Exchange(ref _remoteDelivered, 1) == 0)
                return remoteCandidate;
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }

        public Task SendEndOfCandidatesAsync(CancellationToken cancellationToken = default)
        {
            EndOfCandidatesSent = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSignalling(string remoteAnswer) : IWebRtcSignaling
    {
        public Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default) => Task.FromResult(remoteAnswer);
    }
}
