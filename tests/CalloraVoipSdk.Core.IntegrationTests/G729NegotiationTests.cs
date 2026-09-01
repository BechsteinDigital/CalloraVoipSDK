using System.Net;
using CalloraVoipSdk.Core.Application.Media.Sessions;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// G.729 (RFC 3551, static payload type 18): negotiable, never decodable.
/// </summary>
/// <remarks>
/// The algorithm needs a licence the SDK does not carry, so offering G.729 is a promise to
/// <em>forward</em> the payload, not to understand it — right for a leg bridged to another leg speaking
/// the same codec, wrong for one whose audio somebody wants to hear. It is therefore opt-in, and the
/// point of these tests is that both halves hold: it is available when asked for, and it never appears
/// when it was not.
/// </remarks>
public sealed class G729NegotiationTests
{
    /// <summary>A carrier that offers G.729 first and G.711 after it.</summary>
    private static string CarrierOffer() =>
        "v=0\r\n"
        + "o=- 1 1 IN IP4 127.0.0.1\r\n"
        + "s=carrier\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + "m=audio 20000 RTP/AVP 18 8 0 101\r\n"
        + "a=rtpmap:18 G729/8000\r\n"
        + "a=rtpmap:8 PCMA/8000\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + "a=rtpmap:101 telephone-event/8000\r\n"
        + "a=fmtp:101 0-16\r\n"
        + "a=sendrecv\r\n";

    /// <summary>A carrier that offers nothing but G.729 — the case that fails outright today.</summary>
    private static string G729OnlyOffer() =>
        "v=0\r\n"
        + "o=- 1 1 IN IP4 127.0.0.1\r\n"
        + "s=carrier\r\n"
        + "c=IN IP4 127.0.0.1\r\n"
        + "t=0 0\r\n"
        + "m=audio 20000 RTP/AVP 18\r\n"
        + "a=rtpmap:18 G729/8000\r\n"
        + "a=sendrecv\r\n";

    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Loopback, 40000);

    [Fact]
    public void A_g729_only_offer_can_be_answered_when_it_was_asked_for()
    {
        // Without this the call does not happen at all: no common codec means 488 Not Acceptable Here,
        // which is the same failure a µ-law-only offer produced in Europe, one codec further along.
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            G729OnlyOffer(),
            LocalEndPoint,
            hold: false,
            new SdpMediaNegotiationOptions { PreferredCodecNames = ["G729"] });

        Assert.NotNull(answer);
        Assert.Contains("m=audio 40000 RTP/AVP 18", answer);
        Assert.Contains("a=rtpmap:18 G729/8000", answer);
    }

    [Fact]
    public void A_g729_only_offer_is_still_refused_when_nobody_asked_for_it()
    {
        // The default has to stay what it was. Answering G.729 unasked would hand a caller a call
        // whose audio nothing in this process can read, and it would look like it worked.
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            G729OnlyOffer(), LocalEndPoint, hold: false, new SdpMediaNegotiationOptions());

        Assert.True(answer is null || !answer.Contains("RTP/AVP 18", StringComparison.Ordinal));
    }

    [Fact]
    public void G711_still_wins_when_the_carrier_offers_both_and_nobody_asked_for_g729()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            CarrierOffer(), LocalEndPoint, hold: false, new SdpMediaNegotiationOptions());

        Assert.NotNull(answer);
        Assert.DoesNotContain("a=rtpmap:18 G729/8000", answer);
    }

    [Fact]
    public void The_transcoder_names_g729_rather_than_calling_it_unknown()
    {
        // The whole reason it has its own value: "some codec we could not transcode" and "G.729, which
        // needs a licence we do not have" are the same event and very different sentences.
        Assert.Equal(PayloadCodecKind.G729, AudioPayloadTranscoder.ResolveCodecKind("G729", 18));
        Assert.Equal(PayloadCodecKind.G729, AudioPayloadTranscoder.ResolveCodecKind("G.729", 18));
        // Also without an rtpmap: 18 is statically assigned (RFC 3551) and implies the codec.
        Assert.Equal(PayloadCodecKind.G729, AudioPayloadTranscoder.ResolveCodecKind("PT18", 18));
    }

    [Fact]
    public void There_is_no_transcoding_plan_for_g729_and_the_refusal_says_so()
    {
        // Silence here would be the dangerous half: a plan that "succeeds" and hands back untouched
        // G.729 bytes labelled as PCM is noise that looks like audio.
        var created = AudioPayloadTranscoder.TryCreatePcmFilePlanForCall(
            PayloadCodecKind.G729,
            payloadType: 18,
            clockRate: 8000,
            samplesPerFrame: 160,
            codecName: "G729",
            out var plan,
            out var error);

        Assert.False(created);
        Assert.Null(plan);
        Assert.Contains("G729", error, StringComparison.OrdinalIgnoreCase);
    }
}
