using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P1-1: <see cref="WebRtcClient.CreatePeer"/> must be serialised against disposal. Previously it
/// did not check the disposed flag and tracked unconditionally, so a peer created after a fully-completed
/// DisposeAsync was registered in the dead owner and never torn down. These tests pin the fix: creation after
/// disposal is rejected and leaves nothing tracked, while the normal create/dispose path still clears cleanly.
/// </summary>
public sealed class WebRtcClientCreatePeerDisposeTests
{
    [Fact]
    public async Task CreatePeer_after_dispose_is_rejected_and_tracks_nothing()
    {
        var client = new WebRtcClient();
        await client.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => client.CreatePeer());
        Assert.Equal(0, client.Peers.Count);
    }

    [Fact]
    public async Task CreatePeer_before_dispose_is_tracked_and_dispose_clears_it()
    {
        var client = new WebRtcClient();

        _ = client.CreatePeer();
        Assert.Equal(1, client.Peers.Count);

        await client.DisposeAsync();
        Assert.Equal(0, client.Peers.Count);

        // A second dispose is a no-op and creation stays rejected.
        await client.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => client.CreatePeer());
    }
}
