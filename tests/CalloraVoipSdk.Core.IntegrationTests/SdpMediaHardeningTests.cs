using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP / offer-answer hardening (issue #16): rtcp-mux is only confirmed when offered
/// (RFC 5761 §5.1.1); the bandwidth type token round-trips (RFC 4566 §5.8 / RFC 3890 TIAS);
/// SDP with no connection address or missing mandatory lines is rejected (RFC 4566 §5 / §5.7);
/// dynamic payload types are not mis-assigned by the static-PT fallback; RTCP-port resolution
/// does not overflow at the top of the port range; RTX payload types stay ≤127 (RFC 3550 §5.1).
/// </summary>
public sealed class SdpMediaHardeningTests
{
    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Parse("192.0.2.10"), 40000);

    private static readonly SdpCodecDefinition Pcmu = new()
    {
        PayloadType = 0,
        Name = "PCMU",
        ClockRate = 8000
    };

    private static SdpSessionDescription AudioOffer(bool rtcpMux) => new()
    {
        OriginAddress = "203.0.113.5",
        ConnectionAddress = "203.0.113.5",
        Media =
        [
            new SdpMediaDescription
            {
                MediaType = "audio",
                Port = 20000,
                Profile = "RTP/AVP",
                Codecs = [Pcmu],
                Direction = SdpMediaDirection.SendRecv,
                RtcpMux = rtcpMux
            }
        ]
    };

    // ── SDP 1: rtcp-mux only confirmed when offered ─────────────────────────────

    [Fact]
    public void Answer_does_not_confirm_rtcp_mux_when_the_offer_did_not_advertise_it()
    {
        // Local prefers mux, but the offer carried no a=rtcp-mux — the answer must NOT mux,
        // else RTCP is lost against a peer still listening on the separate RTCP port.
        var options = new SdpMediaOptions { RtcpMux = true };

        var result = new SdpOfferAnswerNegotiator()
            .NegotiateAnswer(AudioOffer(rtcpMux: false), LocalEndPoint, [Pcmu], SdpMediaDirection.SendRecv, options);

        Assert.True(result.Success);
        Assert.False(result.RtcpMuxNegotiated);
        Assert.False(result.Answer!.Media[0].RtcpMux);
    }

    [Fact]
    public void Answer_confirms_rtcp_mux_when_the_offer_advertised_it()
    {
        var result = new SdpOfferAnswerNegotiator()
            .NegotiateAnswer(AudioOffer(rtcpMux: true), LocalEndPoint, [Pcmu], SdpMediaDirection.SendRecv);

        Assert.True(result.Success);
        Assert.True(result.RtcpMuxNegotiated);
        Assert.True(result.Answer!.Media[0].RtcpMux);
    }

    // ── SDP 2: bandwidth type round-trips ───────────────────────────────────────

    [Fact]
    public void Tias_bandwidth_round_trips_as_tias_not_as()
    {
        const string sdp =
            "v=0\r\no=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nc=IN IP4 203.0.113.5\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\nb=TIAS:64000\r\na=rtpmap:0 PCMU/8000\r\n";

        var parsed = new SdpSessionParser().Parse(sdp);
        Assert.Equal("TIAS", parsed.Media[0].Bandwidth!.Type);
        Assert.Equal(64000, parsed.Media[0].Bandwidth!.Value);

        var reserialized = new SdpSessionSerializer().Serialize(parsed);
        Assert.Contains("b=TIAS:64000", reserialized);
        Assert.DoesNotContain("b=AS:", reserialized);
    }

    [Fact]
    public void As_bandwidth_round_trips_as_as()
    {
        const string sdp =
            "v=0\r\no=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nc=IN IP4 203.0.113.5\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\nb=AS:64\r\na=rtpmap:0 PCMU/8000\r\n";

        var parsed = new SdpSessionParser().Parse(sdp);
        Assert.Equal("AS", parsed.Media[0].Bandwidth!.Type);
        Assert.Equal(64, parsed.Media[0].Bandwidth!.Value);

        Assert.Contains("b=AS:64", new SdpSessionSerializer().Serialize(parsed));
    }

    // ── SDP 3: reject missing connection address / mandatory lines ───────────────

    [Fact]
    public void Sdp_with_no_connection_address_is_rejected()
    {
        const string sdp =
            "v=0\r\no=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

        Assert.Throws<FormatException>(() => new SdpSessionParser().Parse(sdp));
    }

    [Fact]
    public void Sdp_with_missing_version_line_is_rejected()
    {
        const string sdp =
            "o=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nc=IN IP4 203.0.113.5\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

        Assert.Throws<FormatException>(() => new SdpSessionParser().Parse(sdp));
    }

    [Fact]
    public void Valid_sdp_with_session_level_connection_is_still_accepted()
    {
        const string sdp =
            "v=0\r\no=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nc=IN IP4 203.0.113.5\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

        var parsed = new SdpSessionParser().Parse(sdp);
        Assert.Equal("203.0.113.5", parsed.ConnectionAddress);
        Assert.Single(parsed.Media);
    }

    [Fact]
    public void Valid_sdp_with_only_media_level_connection_is_still_accepted()
    {
        // No session-level c=, but every media section carries its own — legal (RFC 4566 §5.7).
        const string sdp =
            "v=0\r\no=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\nc=IN IP4 198.51.100.9\r\na=rtpmap:0 PCMU/8000\r\n";

        var parsed = new SdpSessionParser().Parse(sdp);
        Assert.Equal("198.51.100.9", parsed.Media[0].ConnectionAddress);
    }

    // ── SDP 4: dynamic PT not mis-assigned by the static-PT fallback ─────────────

    [Fact]
    public void Unknown_dynamic_payload_type_without_a_name_match_is_not_mis_assigned()
    {
        // Offer lists only a dynamic PT (100) with no rtpmap — the parser names it "PT100".
        // Local supports PCMU on PT 100. The static-PT fallback must NOT bind them: 100 is a
        // dynamic number that carries no implied codec, so the answer has no real audio codec
        // and negotiation fails (rather than answering a codec the peer never offered).
        var offer = new SdpSessionDescription
        {
            OriginAddress = "203.0.113.5",
            ConnectionAddress = "203.0.113.5",
            Media =
            [
                new SdpMediaDescription
                {
                    MediaType = "audio",
                    Port = 20000,
                    Profile = "RTP/AVP",
                    Codecs = [new SdpCodecDefinition { PayloadType = 100, Name = "PT100", ClockRate = 8000 }],
                    Direction = SdpMediaDirection.SendRecv
                }
            ]
        };
        var local = new SdpCodecDefinition { PayloadType = 100, Name = "PCMU", ClockRate = 8000 };

        var result = new SdpOfferAnswerNegotiator()
            .NegotiateAnswer(offer, LocalEndPoint, [local], SdpMediaDirection.SendRecv);

        Assert.False(result.Success);
    }

    // ── SDP 7: RTCP-port resolution does not overflow at the top of the range ────

    [Fact]
    public void Parsing_media_at_port_65535_without_rtcp_mux_does_not_throw()
    {
        const string sdp =
            "v=0\r\no=- 1 1 IN IP4 203.0.113.5\r\ns=s\r\nc=IN IP4 203.0.113.5\r\nt=0 0\r\n"
            + "m=audio 65535 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

        // Previously checked(rtpPort+1) threw OverflowException here, and the surrounding
        // catch discarded the whole media negotiation. The port is clamped instead.
        var parameters = SdpUtilities.TryParseMediaParameters(sdp, LocalEndPoint);

        Assert.NotNull(parameters);
        Assert.NotNull(parameters!.RemoteRtcpEndPoint);
        Assert.Equal(65535, parameters.RemoteRtcpEndPoint!.Port);
    }

    // ── SDP 8: RTX payload types stay within the 7-bit range ────────────────────

    [Fact]
    public void Rtx_payload_types_never_exceed_127()
    {
        // Video PTs at the very top of the range: a naive max+1 would emit 128 (invalid).
        var codecs = new[]
        {
            new SdpCodecDefinition { PayloadType = 126, Name = "VP8", ClockRate = 90000 },
            new SdpCodecDefinition { PayloadType = 127, Name = "H264", ClockRate = 90000 },
        };

        var (rtx, _) = VideoCodecCatalog.BuildRtx(codecs);

        Assert.All(rtx, c => Assert.InRange(c.PayloadType, 0, 127));
    }
}
