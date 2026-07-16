using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Resolving the remote media address from a WebRTC description (Weg 1, RFC 8839): a browser leaves the
/// m-line port a placeholder and carries the real address in a=candidate, so the best component-1 UDP
/// candidate wins; a description with no candidates (loopback / SIP style) uses its m-line address/port.
/// </summary>
public sealed class WebRtcRemoteEndPointTests
{
    [Fact]
    public void The_highest_priority_udp_host_candidate_wins()
    {
        var audio = Audio(port: 9, connection: "127.0.0.1",
            Candidate("127.0.0.2", 12345, priority: 100),
            Candidate("127.0.0.3", 54321, priority: 200));

        var endPoint = WebRtcRemoteEndPoint.Resolve(audio, "127.0.0.1");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 54321), endPoint);
    }

    [Fact]
    public void The_m_line_address_and_port_are_used_when_there_are_no_candidates()
    {
        var audio = Audio(port: 5000, connection: "127.0.0.5");

        var endPoint = WebRtcRemoteEndPoint.Resolve(audio, sessionConnectionAddress: null);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 5000), endPoint);
    }

    [Fact]
    public void Non_udp_and_non_rtp_component_candidates_are_ignored()
    {
        var audio = Audio(port: 5000, connection: "127.0.0.1",
            Candidate("127.0.0.2", 12345, priority: 500, component: 2),   // RTCP component
            Candidate("127.0.0.3", 12346, priority: 500, transport: "tcp")); // TCP

        var endPoint = WebRtcRemoteEndPoint.Resolve(audio, "127.0.0.1");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5000), endPoint); // fell back to the m-line
    }

    [Fact]
    public void No_candidate_and_a_placeholder_port_yields_null()
    {
        var audio = Audio(port: 0, connection: "127.0.0.1");
        Assert.Null(WebRtcRemoteEndPoint.Resolve(audio, "127.0.0.1"));
    }

    private static SdpMediaDescription Audio(int port, string? connection, params SdpIceCandidate[] candidates) => new()
    {
        MediaType = "audio",
        Port = port,
        Profile = "UDP/TLS/RTP/SAVPF",
        Direction = SdpMediaDirection.SendRecv,
        Codecs = [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }],
        ConnectionAddress = connection,
        Candidates = candidates,
    };

    private static SdpIceCandidate Candidate(
        string address, int port, long priority, int component = 1, string transport = "udp", string type = "host") => new()
    {
        Foundation = "1",
        Component = component,
        Transport = transport,
        Priority = priority,
        Address = address,
        Port = port,
        Type = type,
    };
}
