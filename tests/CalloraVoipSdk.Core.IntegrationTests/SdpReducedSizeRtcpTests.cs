using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #162 P2-3, SDP-Hälfte: <c>a=rtcp-rsize</c> (RFC 5506) ist die Erlaubnis, ein RTCP-Datagramm zu
/// senden, das kein vollständiges Compound ist. Ohne sie verlangt RFC 3550 §6.1 für jedes Datagramm
/// ein SR/RR am Anfang und ein SDES mit CNAME — dieser Stack sendet Feedback aber als Einzelpaket.
/// Das Merkmal war bisher gar nicht vorhanden, also wurde die Erlaubnis nie eingeholt.
/// </summary>
public sealed class SdpReducedSizeRtcpTests
{
    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";
    private const string Audio = "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

    private static readonly IPEndPoint Local = new(IPAddress.Parse("192.0.2.1"), 40000);

    private static readonly SdpCodecDefinition[] AudioCapabilities =
    [
        new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
    ];

    private static SdpSessionDescription Parse(string body)
    {
        Assert.True(new SdpSessionParser().TryParse(Header + body, out var parsed));
        return parsed!;
    }

    private static SdpOfferAnswerResult Answer(string body, SdpMediaOptions? options = null) =>
        new SdpOfferAnswerNegotiator().NegotiateAnswer(
            Parse(body), Local, AudioCapabilities, SdpMediaDirection.SendRecv, options);

    // ── Parsen ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_attribute_is_parsed()
    {
        Assert.True(Parse($"{Audio}a=rtcp-rsize\r\n").Media[0].ReducedSizeRtcp);
    }

    [Fact]
    public void Its_absence_is_not_a_claim()
    {
        Assert.False(Parse(Audio).Media[0].ReducedSizeRtcp);
    }

    [Fact]
    public void It_survives_a_serialise_round_trip()
    {
        var text = new SdpSessionSerializer().Serialize(Parse($"{Audio}a=rtcp-rsize\r\n"));

        Assert.Contains("a=rtcp-rsize", text, StringComparison.Ordinal);
    }

    // ── Answer spiegelt das Angebot (RFC 5506, wie libwebrtc) ────────────────

    [Fact]
    public void An_offer_with_the_attribute_is_answered_with_it()
    {
        // libwebrtc: `answer->set_rtcp_reduced_size(offer->rtcp_reduced_size())`.
        var result = Answer($"{Audio}a=rtcp-rsize\r\n");

        Assert.True(result.Success);
        Assert.True(result.Answer!.Media[0].ReducedSizeRtcp);
    }

    [Fact]
    public void An_offer_without_it_is_not_answered_with_it()
    {
        // Die Answer darf nichts einführen, was das Offer nicht anbot (RFC 3264 §6) — und für diesen
        // Stack ist es der Unterschied zwischen erlaubtem Einzelpaket und Pflicht-Compound.
        var result = Answer(Audio);

        Assert.True(result.Success);
        Assert.False(result.Answer!.Media[0].ReducedSizeRtcp);
    }

    [Fact]
    public void The_video_answer_mirrors_it_independently()
    {
        var options = new SdpMediaOptions
        {
            Video = new SdpVideoMediaOptions
            {
                Port = 40002,
                Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }],
            },
        };

        var withRsize = Answer(
            $"{Audio}m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=rtcp-rsize\r\n", options);
        var withoutRsize = Answer(
            $"{Audio}m=video 5002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n", options);

        Assert.True(withRsize.Answer!.Media[1].ReducedSizeRtcp);
        Assert.False(withoutRsize.Answer!.Media[1].ReducedSizeRtcp);
    }

    // ── Offer bietet es an ───────────────────────────────────────────────────

    [Fact]
    public void The_offer_advertises_it()
    {
        // libwebrtc setzt es unbedingt (`offer->set_rtcp_reduced_size(true)`). Für diesen Stack ist es
        // zusätzlich eine Ehrlichkeitsfrage: der Sendepfad emittiert Feedback als Einzelpaket, also
        // muss die Erlaubnis auch eingeholt werden.
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local, AudioCapabilities, SdpMediaDirection.SendRecv);

        Assert.True(offer.Media[0].ReducedSizeRtcp);
        Assert.Contains("a=rtcp-rsize", new SdpSessionSerializer().Serialize(offer), StringComparison.Ordinal);
    }

    [Fact]
    public void The_video_offer_advertises_it_too()
    {
        // Der Video-Pfad ist der, der die Einzelpakete tatsächlich sendet (transport-cc, PLI/FIR).
        var offer = new SdpOfferAnswerNegotiator().CreateOffer(
            Local,
            AudioCapabilities,
            SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Video = new SdpVideoMediaOptions
                {
                    Port = 40002,
                    Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "VP8", ClockRate = 90000 }],
                },
            });

        Assert.True(offer.Media[1].ReducedSizeRtcp);
    }
}
