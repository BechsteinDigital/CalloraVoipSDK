using System.Linq;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The WebRTC offer/answer negotiator (<see cref="SdpOfferAnswerNegotiator"/>, RFC 3264 / RFC 7587) negotiates
/// Opus — the mandatory browser audio codec. A browser-style offer carrying opus/48000/2 on a dynamic payload
/// type is answered with Opus at the mirrored PT when Opus is a local capability; a peer that does not offer Opus
/// while Opus is our only (opt-in) codec fails negotiation rather than producing an audio-less answer. The
/// existing <c>OpusCodecTests</c> cover the SIP path (<c>SdpUtilities</c>); this guards the distinct WebRTC
/// negotiator path the browser-audio interop depends on.
/// </summary>
public sealed class WebRtcOpusNegotiationTests
{
    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Parse("192.0.2.10"), 40000);

    private static readonly SdpCodecDefinition Opus = new()
    {
        PayloadType = 111,
        Name = "opus",
        ClockRate = 48000,
        Channels = 2
    };

    private static readonly SdpCodecDefinition TelephoneEvent48k = new()
    {
        PayloadType = 110,
        Name = "telephone-event",
        ClockRate = 48000
    };

    private static SdpSessionDescription AudioOffer(params SdpCodecDefinition[] codecs) => new()
    {
        OriginAddress = "203.0.113.5",
        ConnectionAddress = "203.0.113.5",
        Media =
        [
            new SdpMediaDescription
            {
                MediaType = "audio",
                Port = 9,
                Profile = "RTP/AVP",
                Codecs = codecs,
                Direction = SdpMediaDirection.SendRecv
            }
        ]
    };

    [Fact]
    public void A_browser_opus_offer_is_answered_with_opus_at_the_mirrored_pt()
    {
        var offer = AudioOffer(
            new SdpCodecDefinition { PayloadType = 111, Name = "opus", ClockRate = 48000, Channels = 2 },
            new SdpCodecDefinition { PayloadType = 110, Name = "telephone-event", ClockRate = 48000 });

        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer, LocalEndPoint, [Opus, TelephoneEvent48k], SdpMediaDirection.SendRecv);

        Assert.True(result.Success);
        var opus = result.NegotiatedCodecs.Single(
            c => c.Name.Equals("opus", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(111, opus.PayloadType);   // mirrors the offer's dynamic PT
        Assert.Equal(48000, opus.ClockRate);
        Assert.Equal(2, opus.Channels);
        // The answer m-line carries Opus at the same PT.
        Assert.Contains(result.Answer!.Media[0].Codecs,
            c => c.Name.Equals("opus", StringComparison.OrdinalIgnoreCase) && c.PayloadType == 111);
    }

    [Fact]
    public void Opus_is_opt_in_a_peer_without_opus_fails_negotiation_when_opus_is_our_only_codec()
    {
        // The peer offers only PCMU while we pin Opus (opt-in). No shared real codec → negotiation fails (488),
        // rather than producing an audio-less or mismatched answer (the SdpOfferAnswerNegotiator design guard).
        var offer = AudioOffer(new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 });

        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            offer, LocalEndPoint, [Opus], SdpMediaDirection.SendRecv);

        Assert.False(result.Success);
    }
}
