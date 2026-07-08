using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Codec preference behavior (M1 hotfix): a configured preference must drive both the
/// negotiated answer and the primary codec of the RTP session parameters, so an agent can
/// pin G.711 µ-law end to end.
/// </summary>
public sealed class SdpCodecPreferenceTests
{
    private static readonly IPEndPoint LocalEndPoint = new(IPAddress.Parse("192.168.178.20"), 40000);

    // Typical Fritz!Box offer: G.722 preferred, then PCMA, PCMU, telephone-event.
    private const string FritzBoxStyleOffer =
        "v=0\r\n" +
        "o=box 1 1 IN IP4 192.168.178.1\r\n" +
        "s=call\r\n" +
        "c=IN IP4 192.168.178.1\r\n" +
        "t=0 0\r\n" +
        "m=audio 7082 RTP/AVP 9 8 0 101\r\n" +
        "a=rtpmap:9 G722/8000\r\n" +
        "a=rtpmap:8 PCMA/8000\r\n" +
        "a=rtpmap:0 PCMU/8000\r\n" +
        "a=rtpmap:101 telephone-event/8000\r\n" +
        "a=fmtp:101 0-16\r\n" +
        "a=sendrecv\r\n";

    private static SdpMediaNegotiationOptions PreferPcmu() =>
        new() { PreferredCodecNames = ["PCMU"] };

    [Fact]
    public void Answer_without_preference_keeps_default_g722_choice()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(FritzBoxStyleOffer, LocalEndPoint, hold: false);

        Assert.NotNull(answer);
        Assert.Contains("G722", answer);
    }

    [Fact]
    public void Answer_with_pcmu_preference_offers_only_pcmu_and_dtmf()
    {
        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            FritzBoxStyleOffer, LocalEndPoint, hold: false, PreferPcmu());

        Assert.NotNull(answer);
        var audioLine = answer!.Split("\r\n").Single(l => l.StartsWith("m=audio", StringComparison.Ordinal));
        Assert.Contains(" 0", audioLine);
        Assert.DoesNotContain("G722", answer);
        Assert.DoesNotContain("PCMA", answer);
        Assert.Contains("telephone-event", answer);
    }

    [Fact]
    public void Media_parameters_with_pcmu_preference_select_payload_type_zero()
    {
        // Regression: without the preference the primary codec of the remote offer (G722,
        // PT 9) won even when the answer picked PCMU — RTP then ran on the wrong codec.
        var parameters = SdpUtilities.TryParseMediaParameters(
            FritzBoxStyleOffer, LocalEndPoint, PreferPcmu());

        Assert.NotNull(parameters);
        Assert.Equal(0, parameters!.PayloadType);
        Assert.Equal("PCMU", parameters.CodecName);
    }

    [Fact]
    public void Media_parameters_without_preference_keep_default_ranking()
    {
        var parameters = SdpUtilities.TryParseMediaParameters(FritzBoxStyleOffer, LocalEndPoint);

        Assert.NotNull(parameters);
        Assert.Equal(9, parameters!.PayloadType);
    }

    [Fact]
    public void Unknown_preference_names_fall_back_to_defaults()
    {
        var options = new SdpMediaNegotiationOptions { PreferredCodecNames = ["OPUS", "EVS"] };

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(
            FritzBoxStyleOffer, LocalEndPoint, hold: false, options);

        Assert.NotNull(answer);
        Assert.Contains("G722", answer);
    }

    [Fact]
    public void Offer_with_pcmu_preference_lists_pcmu_first()
    {
        var offer = SdpUtilities.BuildDefaultSdp(
            LocalEndPoint, hold: false, new SdpMediaNegotiationOptions { PreferredCodecNames = ["PCMU", "PCMA"] });

        var audioLine = offer.Split("\r\n").Single(l => l.StartsWith("m=audio", StringComparison.Ordinal));
        Assert.Matches(@"^m=audio \d+ RTP/AVP 0 8 101$", audioLine);
    }
}
