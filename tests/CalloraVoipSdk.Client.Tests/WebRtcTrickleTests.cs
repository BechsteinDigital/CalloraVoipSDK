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

    [Fact]
    public async Task The_public_peer_emits_a_local_candidate_and_accepts_a_trickled_one()
    {
        // Trickle-ICE slice 2 (RFC 8838): the peer surfaces its local candidate and applies a remote one
        // through the public IPeerConnection.
        var rtc = new WebRtcClient();
        await using var peer = rtc.CreatePeer();
        string? emitted = null;
        peer.LocalIceCandidateDiscovered += (_, c) => emitted = c;

        peer.CreateOffer();
        Assert.NotNull(emitted);
        Assert.StartsWith("candidate:", emitted, StringComparison.Ordinal);

        // A malformed candidate is ignored (no throw); a well-formed one is accepted.
        await peer.AddIceCandidateAsync("garbage");
        await peer.AddIceCandidateAsync("candidate:1 1 udp 2130706431 127.0.0.1 51111 typ host");
    }

    [Fact]
    public async Task LocalMediaEndPoint_is_available_after_CreateOffer_binds_the_socket()
    {
        // Early-bind binds the media socket at CreateOffer, before a session exists — LocalMediaEndPoint must
        // expose the real bound endpoint in that window (previously it stayed null until the session was built).
        var rtc = new WebRtcClient();
        await using var peer = rtc.CreatePeer();

        Assert.Null(peer.LocalMediaEndPoint);   // nothing bound yet
        peer.CreateOffer();

        Assert.NotNull(peer.LocalMediaEndPoint);
        Assert.True(peer.LocalMediaEndPoint!.Port > 0);
    }

    [Fact]
    public async Task GatherCandidatesAsync_without_ice_servers_gathers_host_only()
    {
        // Zero-config peer: no STUN servers → GatherCandidatesAsync is a no-op, only the host candidate
        // was emitted by CreateOffer.
        var rtc = new WebRtcClient();
        await using var peer = rtc.CreatePeer();
        var candidates = new List<string>();
        peer.LocalIceCandidateDiscovered += (_, c) => candidates.Add(c);

        peer.CreateOffer();
        await peer.GatherCandidatesAsync();

        Assert.Contains(candidates, c => c.Contains("typ host", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, c => c.Contains("typ srflx", StringComparison.Ordinal));
    }

    // Extracts the port of an "m=<media> <port> ..." line, e.g. "m=audio 51234 UDP/TLS/RTP/SAVPF 111" -> 51234.
    private static int MediaPort(string sdp, string media)
        => sdp.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith($"m={media} ", StringComparison.Ordinal))
            .Select(line => int.Parse(line.Split(' ')[1]))
            .First();
}
