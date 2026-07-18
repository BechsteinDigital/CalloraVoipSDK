using System.Net;
using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// The public simulcast surface: <see cref="WebRtcConfiguration.SimulcastLayers"/> flows through the client
/// into the offer, which then advertises <c>a=rid … send</c> and <c>a=simulcast:send</c> (RFC 8853). Without
/// the option the offer carries a single video stream.
/// </summary>
public sealed class WebRtcSimulcastConfigTests
{
    [Fact]
    public async Task Configured_simulcast_layers_appear_in_the_offer()
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            EnableVideo = true,
            VideoCodecs = ["VP8"],
            SimulcastLayers = ["hi", "mid", "lo"],
        });

        await using var peer = client.CreatePeer();
        var offer = peer.CreateOffer();

        Assert.Contains("a=rid:hi send", offer, StringComparison.Ordinal);
        Assert.Contains("a=rid:mid send", offer, StringComparison.Ordinal);
        Assert.Contains("a=rid:lo send", offer, StringComparison.Ordinal);
        Assert.Contains("a=simulcast:send hi;mid;lo", offer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_simulcast_layers_the_offer_has_no_rid()
    {
        var client = new WebRtcClient(new WebRtcConfiguration
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            EnableVideo = true,
            VideoCodecs = ["VP8"],
        });

        await using var peer = client.CreatePeer();
        var offer = peer.CreateOffer();

        Assert.DoesNotContain("a=rid:", offer, StringComparison.Ordinal);
        Assert.DoesNotContain("a=simulcast:", offer, StringComparison.Ordinal);
    }
}
