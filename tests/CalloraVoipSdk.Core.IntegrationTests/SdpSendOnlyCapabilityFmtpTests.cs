using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #275 — RFC 6184 §8.2.2: the capability parameters declare what the sender of that SDP is willing to
/// <b>receive</b>, and they "MUST NOT be present when the direction attribute is sendonly". The answer
/// carries the offer's fmtp forward — which for <c>profile-level-id</c> and <c>packetization-mode</c> is
/// required, since §8.2.2 makes those symmetric — so without a filter a sendonly answer would state a
/// receiving limit, copied from the peer, on a line that receives nothing.
/// </summary>
public sealed class SdpSendOnlyCapabilityFmtpTests
{
    private static SdpFmtpAttribute Fmtp(string parameters) => new() { PayloadType = 96, Parameters = parameters };

    private static string Strip(string parameters, SdpMediaDirection direction) =>
        SdpReceiverCapabilityFmtp.StripForSendOnly([Fmtp(parameters)], direction) is [var only]
            ? only.Parameters
            : string.Empty;

    // ── The filter itself ────────────────────────────────────────────────────

    [Fact]
    public void Sendonly_drops_the_capability_parameters()
    {
        var kept = Strip("profile-level-id=42e01f;packetization-mode=1;max-fs=3600;max-mbps=108000",
            SdpMediaDirection.SendOnly);

        Assert.Equal("profile-level-id=42e01f;packetization-mode=1", kept);
    }

    [Fact]
    public void The_configuration_parameters_survive_because_they_must()
    {
        // §8.2.2 requires profile-level-id and packetization-mode to be symmetric: keep them or drop the
        // payload type. Filtering them would break the negotiation this fix is meant to leave intact.
        var kept = Strip("profile-level-id=42e01f;packetization-mode=1", SdpMediaDirection.SendOnly);

        Assert.Equal("profile-level-id=42e01f;packetization-mode=1", kept);
    }

    // The direction enum is internal, so these stay separate facts rather than a Theory with an
    // inaccessible parameter type.
    private const string WithCapability = "profile-level-id=42e01f;max-fs=3600";

    [Fact]
    public void Sendrecv_is_left_alone() =>
        Assert.Equal(WithCapability, Strip(WithCapability, SdpMediaDirection.SendRecv));

    [Fact]
    public void Recvonly_is_left_alone() =>
        Assert.Equal(WithCapability, Strip(WithCapability, SdpMediaDirection.RecvOnly));

    [Fact]
    public void Inactive_is_left_alone() =>
        Assert.Equal(WithCapability, Strip(WithCapability, SdpMediaDirection.Inactive));

    [Fact]
    public void An_entry_of_nothing_but_capabilities_disappears_entirely()
    {
        // Emitting "a=fmtp:96 " with an empty value would be malformed; the attribute has to go.
        var result = SdpReceiverCapabilityFmtp.StripForSendOnly(
            [Fmtp("max-fs=3600;max-mbps=108000")], SdpMediaDirection.SendOnly);

        Assert.Empty(result);
    }

    [Fact]
    public void All_ten_listed_parameters_are_covered()
    {
        // The list is the one §8.2.2 enumerates — a missing entry would silently keep leaking.
        const string all = "max-mbps=1;max-smbps=1;max-fs=1;max-cpb=1;max-dpb=1;max-br=1;"
            + "redundant-pic-cap=0;max-rcmd-nalu-size=1;sar-understood=1;sar-supported=1;profile-level-id=42e01f";

        Assert.Equal("profile-level-id=42e01f", Strip(all, SdpMediaDirection.SendOnly));
    }

    [Fact]
    public void Parameter_names_are_matched_case_insensitively() =>
        Assert.Equal("profile-level-id=42e01f", Strip("MAX-FS=3600;profile-level-id=42e01f", SdpMediaDirection.SendOnly));

    [Fact]
    public void Unknown_and_valueless_parameters_are_kept()
    {
        // The filter removes exactly what the RFC lists; anything it does not recognise stays, including a
        // bare token that carries no '=' at all.
        var kept = Strip("something-else=7;bare;max-fs=3600", SdpMediaDirection.SendOnly);

        Assert.Equal("something-else=7;bare", kept);
    }

    [Fact]
    public void Sprop_parameters_stay_because_they_describe_what_we_send()
    {
        // §8.2.2 singles these out as describing the emitted stream rather than receiving capability —
        // exactly the reason they are not in the MUST NOT list.
        var kept = Strip("sprop-parameter-sets=Z0IACpZTBYmI;max-fs=3600", SdpMediaDirection.SendOnly);

        Assert.Equal("sprop-parameter-sets=Z0IACpZTBYmI", kept);
    }

    // ── End to end through the negotiator ────────────────────────────────────

    private const string Header = "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n";
    private const string Audio = "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\n";

    private static readonly IPEndPoint Local = new(IPAddress.Parse("192.0.2.1"), 40000);

    private static readonly SdpCodecDefinition[] AudioCapabilities =
    [
        new() { PayloadType = 0, Name = "PCMU", ClockRate = 8000 },
    ];

    private static SdpMediaOptions VideoOptions() => new()
    {
        Video = new SdpVideoMediaOptions
        {
            Port = 40002,
            Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
        },
    };

    private static SdpMediaDescription? AnswerVideo(string videoDirectionAttribute, SdpMediaDirection localDirection)
    {
        var offer =
            Header + Audio
            + "m=video 5002 RTP/AVP 96\r\n"
            + "a=rtpmap:96 H264/90000\r\n"
            + "a=fmtp:96 profile-level-id=42e01f;packetization-mode=1;max-fs=3600;max-mbps=108000\r\n"
            + videoDirectionAttribute;

        Assert.True(new SdpSessionParser().TryParse(offer, out var parsed));
        var result = new SdpOfferAnswerNegotiator().NegotiateAnswer(
            parsed!, Local, AudioCapabilities, localDirection, VideoOptions());

        Assert.True(result.Success);
        return result.Answer!.Media.FirstOrDefault(m => m.MediaType == "video");
    }

    [Fact]
    public void A_sendonly_video_answer_carries_no_capability_parameters()
    {
        // The peer offers recvonly, so our answer is sendonly — the case §8.2.2 forbids.
        var video = AnswerVideo("a=recvonly\r\n", SdpMediaDirection.SendRecv);

        Assert.NotNull(video);
        Assert.Equal(SdpMediaDirection.SendOnly, video!.Direction);
        var fmtp = Assert.Single(video.Fmtp, f => f.PayloadType == 96);
        Assert.DoesNotContain("max-fs", fmtp.Parameters, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-mbps", fmtp.Parameters, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile-level-id=42e01f", fmtp.Parameters, StringComparison.Ordinal);
        Assert.Contains("packetization-mode=1", fmtp.Parameters, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sendrecv_video_answer_is_unchanged()
    {
        // Byte-for-byte the previous behaviour on the path that actually occurs in practice.
        var video = AnswerVideo("a=sendrecv\r\n", SdpMediaDirection.SendRecv);

        Assert.NotNull(video);
        var fmtp = Assert.Single(video!.Fmtp, f => f.PayloadType == 96);
        Assert.Contains("max-fs=3600", fmtp.Parameters, StringComparison.Ordinal);
        Assert.Contains("max-mbps=108000", fmtp.Parameters, StringComparison.Ordinal);
    }
}
