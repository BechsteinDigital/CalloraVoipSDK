using CalloraVoipSdk.WebRtc;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// Trickle-ICE slice 1 (early-bind): the peer binds its media socket before creating the offer, so a
/// default (port-0) configuration advertises the real ephemeral port and a host candidate — the zero-port
/// disabled-offer bug is gone. (The two-default-peers connection is proven in WebRtcSignalingTests.)
/// </summary>
public sealed class WebRtcTrickleTests
{
    [Fact]
    public async Task Early_bind_gives_a_port_zero_offer_a_real_media_port_and_host_candidate()
    {
        var rtc = new WebRtcClient();   // default LocalEndPoint = loopback:0
        await using var peer = rtc.CreatePeer();

        var offer = peer.CreateOffer();

        var audioPort = MediaPort(offer, "audio");
        Assert.True(audioPort > 0, $"the audio m-line should carry the real ephemeral port, not 0 (was {audioPort})");
        Assert.Contains("a=candidate:", offer, StringComparison.Ordinal);   // a host candidate is emitted
    }

    // Extracts the port of an "m=<media> <port> ..." line, e.g. "m=audio 51234 UDP/TLS/RTP/SAVPF 111" -> 51234.
    private static int MediaPort(string sdp, string media)
        => sdp.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith($"m={media} ", StringComparison.Ordinal))
            .Select(line => int.Parse(line.Split(' ')[1]))
            .First();
}
