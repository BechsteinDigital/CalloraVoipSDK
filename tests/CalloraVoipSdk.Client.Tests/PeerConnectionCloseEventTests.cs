using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P2-6: an explicit peer dispose must publish the terminal
/// <see cref="PeerConnectionState.Closed"/> transition to the app — exactly once. The facade used to detach its
/// state handler as the first statement of <c>DisposeAsync</c> and only then dispose the inner peer, which is
/// where the Closed transition is raised (RFC 8829 §4.1.3), so <c>State</c> became Closed but the event never
/// reached the application. SIPSorcery raises its <c>onconnectionstatechange(closed)</c> on close; so do we now.
/// </summary>
public sealed class PeerConnectionCloseEventTests
{
    [Fact]
    public async Task Explicit_peer_dispose_publishes_Closed_exactly_once()
    {
        await using var client = new WebRtcClient();
        var peer = client.CreatePeer();

        var states = new List<PeerConnectionState>();
        peer.ConnectionStateChanged += (_, state) => states.Add(state);

        await peer.DisposeAsync();

        Assert.Equal([PeerConnectionState.Closed], states);
        Assert.Equal(PeerConnectionState.Closed, peer.State);

        // Dispose is idempotent, and the terminal transition is not republished.
        await peer.DisposeAsync();
        Assert.Single(states);
    }

    [Fact]
    public async Task Disposing_the_owning_client_publishes_Closed_on_its_peers_exactly_once()
    {
        var client = new WebRtcClient();
        var peer = client.CreatePeer();

        var closed = 0;
        peer.ConnectionStateChanged += (_, state) =>
        {
            if (state == PeerConnectionState.Closed)
                Interlocked.Increment(ref closed);
        };

        await client.DisposeAsync();

        Assert.Equal(1, Volatile.Read(ref closed));
    }

    /// <summary>
    /// A subscriber that throws on the terminal transition must not break the teardown: the peer still
    /// untracks from its owner and the dispose completes (K3 — handlers must not throw; one that does is
    /// isolated and logged).
    /// </summary>
    [Fact]
    public async Task A_throwing_Closed_subscriber_does_not_break_the_teardown()
    {
        await using var client = new WebRtcClient();
        var peer = client.CreatePeer();
        peer.ConnectionStateChanged += (_, _) => throw new InvalidOperationException("subscriber-boom");

        await peer.DisposeAsync();

        Assert.Equal(0, client.Peers.Count);
    }
}
