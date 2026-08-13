using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 P2-13 and P2-16: two attributes the answer echoed rather than negotiated. An extmap mapping was
/// confirmed without checking it was unambiguous and with its direction thrown away, and the remote
/// ptime was mirrored back whatever it said. Both produce an answer that agrees to something this side
/// does not actually do.
/// </summary>
public sealed class SdpAnswerAttributeNegotiationTests
{
    private static readonly IPEndPoint Local = new(IPAddress.Parse("192.0.2.1"), 40000);

    private static readonly SdpCodecDefinition[] AudioCapabilities =
    [
        new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
    ];

    private const string Mid = "urn:ietf:params:rtp-hdrext:sdes:mid";
    private const string TransportCc =
        "http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01";

    private static SdpSessionDescription ParseOffer(string body)
    {
        Assert.True(new SdpSessionParser().TryParse(
            "v=0\r\no=- 1 1 IN IP4 198.51.100.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 198.51.100.1\r\n" + body,
            out var offer));
        return offer!;
    }

    private static SdpMediaDescription AnswerAudio(string body, SdpMediaOptions? options = null)
    {
        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            ParseOffer(body), Local, AudioCapabilities, SdpMediaDirection.SendRecv, options);

        Assert.True(result.Success);
        return result.Answer!.Media[0];
    }

    // The audio answer supports no header extensions beyond MID, so the extmap cases run on the video
    // m-line, where the supported set is configurable.
    private static SdpMediaOptions WithExtensions(params string[] uris) =>
        new()
        {
            Video = new SdpVideoMediaOptions
            {
                Port = 40002,
                Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }],
                HeaderExtensionUris = uris,
            },
        };

    private static SdpMediaDescription AnswerVideo(string extmapLines, params string[] supportedUris)
    {
        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            ParseOffer(
                "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n" +
                "m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n" + extmapLines),
            Local,
            AudioCapabilities,
            SdpMediaDirection.SendRecv,
            WithExtensions(supportedUris));

        Assert.True(result.Success);
        return result.Answer!.Media[1];
    }

    // ── P2-16: ptime states what we will do, not what we were told ───────────

    [Theory]
    [InlineData(20)]
    [InlineData(10)]    // lower bound
    [InlineData(60)]
    [InlineData(120)]   // upper bound
    public void A_usable_ptime_is_honoured(int offered)
    {
        var answer = AnswerAudio($"m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=ptime:{offered}\r\n");

        Assert.Equal(offered, answer.Ptime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(500)]     // the review's probe: agreed to, while the sender kept packetising at 20 ms
    [InlineData(100000)]
    public void An_unusable_ptime_is_answered_with_the_rate_we_actually_send_at(int offered)
    {
        // The value is not decorative — it feeds the media parameters. Confirming 500 ms would be an
        // agreement this side never honours.
        var answer = AnswerAudio($"m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=ptime:{offered}\r\n");

        Assert.Equal(20, answer.Ptime);
    }

    [Fact]
    public void A_ptime_above_the_peers_own_maxptime_is_not_confirmed()
    {
        // maxptime is the peer's own ceiling (RFC 4566 §6); a ptime above it contradicts the same offer.
        var answer = AnswerAudio(
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=ptime:80\r\na=maxptime:40\r\n");

        Assert.Equal(20, answer.Ptime);
    }

    [Fact]
    public void A_ptime_within_the_peers_maxptime_is_honoured()
    {
        var answer = AnswerAudio(
            "m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=ptime:30\r\na=maxptime:40\r\n");

        Assert.Equal(30, answer.Ptime);
    }

    [Fact]
    public void No_offered_ptime_means_none_in_the_answer()
    {
        var answer = AnswerAudio("m=audio 5000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n");

        Assert.Null(answer.Ptime);
    }

    // ── P2-13: extmap is a mapping, and mappings must be unambiguous ─────────

    [Fact]
    public void An_id_offered_twice_for_different_uris_is_confirmed_only_once()
    {
        // The demultiplexer reads one id and needs one meaning for it. Echoing both would confirm an
        // ambiguity as though it had been negotiated.
        var answer = AnswerVideo($"a=extmap:1 {Mid}\r\na=extmap:1 {TransportCc}\r\n", Mid, TransportCc);

        Assert.Single(answer.Extensions);
        Assert.Equal(1, answer.Extensions[0].Id);
        Assert.Equal(Mid, answer.Extensions[0].Uri);
    }

    [Fact]
    public void A_uri_offered_under_two_ids_is_confirmed_only_once()
    {
        var answer = AnswerVideo($"a=extmap:1 {Mid}\r\na=extmap:2 {Mid}\r\n", Mid);

        Assert.Single(answer.Extensions);
        Assert.Equal(1, answer.Extensions[0].Id);
    }

    [Fact]
    public void Distinct_mappings_are_all_confirmed()
    {
        var answer = AnswerVideo($"a=extmap:1 {Mid}\r\na=extmap:3 {TransportCc}\r\n", Mid, TransportCc);

        Assert.Equal(2, answer.Extensions.Count);
        Assert.Equal(new[] { 1, 3 }, answer.Extensions.Select(e => e.Id));
    }

    [Theory]
    [InlineData("sendonly", "recvonly")]
    [InlineData("recvonly", "sendonly")]
    [InlineData("sendrecv", "sendrecv")]
    [InlineData("inactive", "inactive")]
    public void The_direction_qualifier_is_mirrored(string offered, string expected)
    {
        // RFC 8285 §5: the direction is part of the negotiation. Dropping it, as before, silently
        // promoted an extension the peer will only send to sendrecv.
        var answer = AnswerVideo($"a=extmap:1/{offered} {Mid}\r\n", Mid);

        Assert.Single(answer.Extensions);
        Assert.Equal(expected, answer.Extensions[0].Direction);
    }

    [Fact]
    public void An_offer_without_a_qualifier_is_answered_without_one()
    {
        // Absent means sendrecv; adding the qualifier would be noise, not information.
        var answer = AnswerVideo($"a=extmap:1 {Mid}\r\n", Mid);

        Assert.Null(answer.Extensions[0].Direction);
    }

    [Fact]
    public void An_unknown_qualifier_is_not_echoed_back()
    {
        // Answering a token neither side can act on agrees to nothing meaningful.
        var answer = AnswerVideo($"a=extmap:1/nonsense {Mid}\r\n", Mid);

        Assert.Single(answer.Extensions);
        Assert.Null(answer.Extensions[0].Direction);
    }

    [Fact]
    public void An_unsupported_uri_is_not_confirmed()
    {
        var answer = AnswerVideo($"a=extmap:1 {Mid}\r\na=extmap:2 urn:example:not-supported\r\n", Mid);

        Assert.Single(answer.Extensions);
        Assert.Equal(Mid, answer.Extensions[0].Uri);
    }
}
