using System.Net;
using System.Threading.Channels;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The signalling happy path (ADR-012 step 5): <see cref="WebRtcPeerConnectionExtensions.ConnectAsync"/>
/// drives the RFC 8829 offer/answer over an app-owned <see cref="IWebRtcSignaling"/> channel and completes
/// when the peer is connected. Orchestration is verified deterministically against a fake peer; the wiring
/// is proven end-to-end through the public <see cref="WebRtcClient"/> over an in-memory channel.
/// </summary>
public sealed class WebRtcSignalingTests
{
    [Fact]
    public async Task Offerer_sends_its_offer_applies_the_answer_and_starts()
    {
        var peer = new FakePeer { Answer = "unused", Terminal = PeerConnectionState.Connected };
        var signalling = new FakeSignalling("REMOTE-ANSWER");

        await peer.ConnectAsync(signalling, WebRtcRole.Offerer);

        Assert.True(peer.OfferCreated);
        Assert.Equal("OFFER", Assert.Single(signalling.Sent));   // the peer's own offer went out
        Assert.Equal("REMOTE-ANSWER", peer.RemoteApplied);       // the received answer was applied
        Assert.True(peer.Started);
    }

    [Fact]
    public async Task Answerer_applies_the_offer_and_sends_back_its_answer()
    {
        var peer = new FakePeer { Answer = "LOCAL-ANSWER", Terminal = PeerConnectionState.Connected };
        var signalling = new FakeSignalling("REMOTE-OFFER");

        await peer.ConnectAsync(signalling, WebRtcRole.Answerer);

        Assert.False(peer.OfferCreated);                         // answerer does not create an offer
        Assert.Equal("REMOTE-OFFER", peer.RemoteApplied);
        Assert.Equal("LOCAL-ANSWER", Assert.Single(signalling.Sent));
        Assert.True(peer.Started);
    }

    [Fact]
    public async Task A_failed_handshake_throws_WebRtcConnectException()
    {
        var peer = new FakePeer { Answer = "a", Terminal = PeerConnectionState.Failed };
        var signalling = new FakeSignalling("x");

        await Assert.ThrowsAsync<WebRtcConnectException>(() => peer.ConnectAsync(signalling, WebRtcRole.Offerer));
    }

    [Fact]
    public async Task Cancellation_while_awaiting_the_remote_description_is_observed()
    {
        var peer = new FakePeer();
        var signalling = new BlockingSignalling();
        using var cts = new CancellationTokenSource();

        var connect = peer.ConnectAsync(signalling, WebRtcRole.Answerer, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.False(peer.Started);
    }

    [Fact]
    public async Task Two_peers_connect_through_an_in_memory_signalling_channel()
    {
        // Early-bind (Trickle-ICE slice 1): both peers bind their media socket before creating the
        // offer/answer, so a default (port-0) configuration advertises the real ephemeral port and connects
        // — no fixed port needed anymore.
        var offererClient = new WebRtcClient();
        var answererClient = new WebRtcClient();
        await using var offerer = offererClient.CreatePeer();
        await using var answerer = answererClient.CreatePeer();
        var (offererChannel, answererChannel) = InMemorySignalling.Pair();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(
            offerer.ConnectAsync(offererChannel, WebRtcRole.Offerer, cts.Token),
            answerer.ConnectAsync(answererChannel, WebRtcRole.Answerer, cts.Token));

        Assert.Equal(PeerConnectionState.Connected, offerer.State);
        Assert.Equal(PeerConnectionState.Connected, answerer.State);

        // Stats S2: a connected peer reports the ICE state and the selected (local ↔ remote) endpoints.
        var stats = offerer.GetStats();
        Assert.Equal("connected", stats.IceState);
        Assert.NotNull(stats.SelectedLocalCandidate);
        Assert.NotNull(stats.SelectedRemoteCandidate);
    }

    // ── fakes ──────────────────────────────────────────────────────────────────

    private sealed class FakePeer : IPeerConnection
    {
        public string Answer { get; init; } = "ANSWER";
        public PeerConnectionState Terminal { get; init; } = PeerConnectionState.Connected;
        public bool OfferCreated { get; private set; }
        public string? RemoteApplied { get; private set; }
        public bool Started { get; private set; }

        public PeerConnectionState State { get; private set; } = PeerConnectionState.New;
        public string? LocalDescription => "OFFER";
        public IPEndPoint? LocalMediaEndPoint => null;

        public event EventHandler<PeerConnectionState>? ConnectionStateChanged;
        public event EventHandler<RemoteTrack>? TrackReceived { add { } remove { } }
        public event EventHandler<string>? LocalIceCandidateDiscovered { add { } remove { } }

        public string CreateOffer() { OfferCreated = true; return "OFFER"; }

        public Task AddIceCandidateAsync(string candidate, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task GatherCandidatesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> SetRemoteDescriptionAsync(string remoteSdp, CancellationToken cancellationToken = default)
        {
            RemoteApplied = remoteSdp;
            Raise(PeerConnectionState.Connecting);
            return Task.FromResult(Answer);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            Raise(Terminal);
            return Task.CompletedTask;
        }

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task SendVideoFrameAsync(ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendVideoFrameAsync(string rid, ReadOnlyMemory<byte> encodedFrame, uint rtpTimestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IDisposable AttachMediaTap(IMediaTap tap) => NoopDisposable.Instance;
        public WebRtcStats GetStats() => new() { ConnectionState = State };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }

        private void Raise(PeerConnectionState state)
        {
            State = state;
            ConnectionStateChanged?.Invoke(this, state);
        }
    }

    private sealed class FakeSignalling(string received) : IWebRtcSignaling
    {
        public List<string> Sent { get; } = [];

        public Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default)
        {
            Sent.Add(sdp);
            return Task.CompletedTask;
        }

        public Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(received);
    }

    private sealed class BlockingSignalling : IWebRtcSignaling
    {
        public Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return string.Empty;
        }
    }

    private sealed class InMemorySignalling : IWebRtcSignaling
    {
        private readonly ChannelReader<string> _inbound;
        private readonly ChannelWriter<string> _outbound;

        private InMemorySignalling(ChannelReader<string> inbound, ChannelWriter<string> outbound)
        {
            _inbound = inbound;
            _outbound = outbound;
        }

        public Task SendDescriptionAsync(string sdp, CancellationToken cancellationToken = default)
            => _outbound.WriteAsync(sdp, cancellationToken).AsTask();

        public async Task<string> ReceiveDescriptionAsync(CancellationToken cancellationToken = default)
            => await _inbound.ReadAsync(cancellationToken).ConfigureAwait(false);

        public static (IWebRtcSignaling offerer, IWebRtcSignaling answerer) Pair()
        {
            var offererToAnswerer = Channel.CreateUnbounded<string>();
            var answererToOfferer = Channel.CreateUnbounded<string>();
            return (
                new InMemorySignalling(answererToOfferer.Reader, offererToAnswerer.Writer),
                new InMemorySignalling(offererToAnswerer.Reader, answererToOfferer.Writer));
        }
    }
}
