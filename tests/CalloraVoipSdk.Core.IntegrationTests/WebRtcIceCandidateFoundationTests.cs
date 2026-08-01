using System.Net;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// ICE candidate foundations (RFC 8445 §5.1.1.3 — same foundation only for candidates of the same
/// type/base/server/transport). Guards the 4.7.2 review finding: with the old numeric scheme a multi-homed
/// second host candidate got foundation "2" and collided with the fixed srflx "2" (and a third with relay "3"),
/// which can wrongly hold a peer's NAT/relay fallback in one frozen group.
/// </summary>
public sealed class WebRtcIceCandidateFoundationTests
{
    private static IPEndPoint Ep(string ip, int port) => new(IPAddress.Parse(ip), port);

    [Fact]
    public void Host_srflx_and_relay_foundations_never_collide_across_multi_homed_hosts()
    {
        var host0 = WebRtcIceCandidateFactory.LocalHostCandidate(Ep("192.0.2.1", 5000), preferenceIndex: 0);
        var host1 = WebRtcIceCandidateFactory.LocalHostCandidate(Ep("198.51.100.1", 5000), preferenceIndex: 1);
        var host2 = WebRtcIceCandidateFactory.LocalHostCandidate(Ep("203.0.113.1", 5000), preferenceIndex: 2);
        var srflx = WebRtcIceCandidateFactory.ServerReflexiveCandidate(Ep("192.0.2.9", 5000), Ep("192.0.2.1", 5000));
        var relay = WebRtcIceCandidateFactory.RelayCandidate(Ep("192.0.2.10", 5000), Ep("192.0.2.1", 5000));

        var foundations = new[] { host0, host1, host2, srflx, relay }.Select(c => c.Foundation).ToArray();
        Assert.Equal(foundations.Length, foundations.Distinct().Count());   // all distinct across types
        Assert.NotEqual(host1.Foundation, srflx.Foundation);               // the specific pre-fix host2↔srflx collision
        Assert.NotEqual(host2.Foundation, relay.Foundation);               // and host3↔relay
    }

    [Fact]
    public void Each_host_candidate_has_a_distinct_type_scoped_foundation()
    {
        // Different base addresses → different foundations (RFC 8445 §5.1.1.3).
        var a = WebRtcIceCandidateFactory.LocalHostCandidate(Ep("192.0.2.1", 5000), 0);
        var b = WebRtcIceCandidateFactory.LocalHostCandidate(Ep("198.51.100.1", 5000), 1);
        Assert.NotEqual(a.Foundation, b.Foundation);
    }
}
