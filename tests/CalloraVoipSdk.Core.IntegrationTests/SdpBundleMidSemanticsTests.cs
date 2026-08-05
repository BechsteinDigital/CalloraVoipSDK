using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 SDP P1-c: the BUNDLE group and its MIDs must be handled semantically, not by string prefix.
/// The negotiator previously built the answer group from every accepted MID, so a hostile offer that
/// listed only "BUNDLE video" while tagging the audio m-line "a=mid:audio" produced "BUNDLE audio
/// video" — pulling an un-grouped m-line onto the shared transport (RFC 5888 / RFC 8843 / RFC 9143).
/// </summary>
public sealed class SdpBundleMidSemanticsTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 41000);

    private static SdpMediaNegotiationOptions VideoEnabled() =>
        new() { Video = new SdpVideoNegotiationOptions { Port = 41002 } };

    // A two-m-line offer (audio mid=audio, video mid=video) with a session-level a=group line.
    private static string OfferWithGroup(string groupLine) =>
        "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nc=IN IP4 127.0.0.1\r\n" +
        groupLine + "\r\n" +
        "m=audio 40000 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=mid:audio\r\n" +
        "m=video 40002 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\na=mid:video\r\n";

    private static string Answer(string groupLine)
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(OfferWithGroup(groupLine), LocalAudio, hold: false, VideoEnabled());
        Assert.NotNull(answer);
        return answer!;
    }

    [Fact]
    public void A_well_formed_bundle_group_is_preserved()
    {
        Assert.Contains("a=group:BUNDLE audio video", Answer("a=group:BUNDLE audio video"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_un_offered_mid_is_never_added_to_the_answer_group()
    {
        // The exact repro: only "video" is in the offered group, audio is not a member. The answer must
        // not smuggle audio into a BUNDLE group; with the primary audio outside the group it is not bundled.
        var answer = Answer("a=group:BUNDLE video");

        Assert.DoesNotContain("a=group:BUNDLE audio", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("audio video", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_foreign_group_mid_is_dropped_from_the_answer()
    {
        // "foobar" is listed in the group but matches no m-line — it must not appear in the answer group.
        var answer = Answer("a=group:BUNDLE audio video foobar");

        Assert.Contains("a=group:BUNDLE audio video", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("foobar", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundlex_prefix_is_not_treated_as_a_bundle_group()
    {
        Assert.DoesNotContain("a=group:", Answer("a=group:BUNDLEX audio video"), StringComparison.Ordinal);
    }

    // ── SdpBundleGroup.TryParse ────────────────────────────────────────────────

    [Fact]
    public void TryParse_reads_the_ordered_member_mids_of_a_bundle_group()
    {
        Assert.True(SdpBundleGroup.TryParse("BUNDLE 0 1 2", out var mids));
        Assert.Equal(["0", "1", "2"], mids);
    }

    [Fact]
    public void TryParse_deduplicates_repeated_member_mids()
    {
        // RFC 5888 §5: a MID appears in the group at most once — a repeated member must not survive.
        Assert.True(SdpBundleGroup.TryParse("BUNDLE audio audio video", out var mids));
        Assert.Equal(["audio", "video"], mids);
    }

    [Fact]
    public void A_duplicate_offered_group_mid_is_not_repeated_in_the_answer()
    {
        Assert.Contains("a=group:BUNDLE audio video", Answer("a=group:BUNDLE audio audio video"), StringComparison.Ordinal);
        Assert.DoesNotContain("audio audio", Answer("a=group:BUNDLE audio audio video"), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_is_case_insensitive_on_the_semantics_token()
    {
        Assert.True(SdpBundleGroup.TryParse("bundle audio", out var mids));
        Assert.Equal(["audio"], mids);
    }

    [Fact]
    public void TryParse_accepts_an_empty_bundle_group()
    {
        Assert.True(SdpBundleGroup.TryParse("BUNDLE", out var mids));
        Assert.Empty(mids);
    }

    [Theory]
    [InlineData("BUNDLEX audio")]
    [InlineData("LS audio video")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_non_bundle_groups(string? raw)
    {
        Assert.False(SdpBundleGroup.TryParse(raw, out var mids));
        Assert.Empty(mids);
    }
}
