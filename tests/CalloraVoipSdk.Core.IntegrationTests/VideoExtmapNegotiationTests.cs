using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP <c>a=extmap</c> negotiation (RFC 8285 §5), the signaling layer for RTP header extensions
/// (transport-wide-cc for congestion control). An offer assigns one-byte ids to the supported
/// URIs; an answer echoes the offered id for URIs it supports and drops the rest. Off by default
/// (no <c>a=extmap</c>) until opted in via <see cref="SdpVideoNegotiationOptions.HeaderExtensionUris"/>.
/// </summary>
public sealed class VideoExtmapNegotiationTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 41000);
    private const string TransportCc = RtpHeaderExtensionUris.TransportWideCc;
    private const string Unknown = "urn:ietf:params:rtp-hdrext:unknown-extension";

    // ── SdpExtmap model ──────────────────────────────────────────────────────────

    [Fact]
    public void Extmap_parses_and_serializes_round_trip()
    {
        var parsed = SdpExtmap.TryParse($"3 {TransportCc}");
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Id);
        Assert.Null(parsed.Direction);
        Assert.Equal(TransportCc, parsed.Uri);
        Assert.Equal($"3 {TransportCc}", parsed.Serialize());
    }

    [Fact]
    public void Extmap_preserves_the_direction_qualifier()
    {
        var parsed = SdpExtmap.TryParse($"4/sendrecv {TransportCc}");
        Assert.NotNull(parsed);
        Assert.Equal(4, parsed!.Id);
        Assert.Equal("sendrecv", parsed.Direction);
        Assert.Equal($"4/sendrecv {TransportCc}", parsed.Serialize());
    }

    [Theory]
    [InlineData("")]
    [InlineData("notanumber uri")]
    [InlineData("3")] // missing uri
    public void Extmap_rejects_malformed_values(string value)
        => Assert.Null(SdpExtmap.TryParse(value));

    // ── Offer emission ───────────────────────────────────────────────────────────

    [Fact]
    public void Offer_assigns_a_one_byte_id_to_the_supported_extension()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false, new SdpMediaNegotiationOptions
        {
            Video = new SdpVideoNegotiationOptions { Port = 41002, HeaderExtensionUris = [TransportCc] },
        });

        var videoSection = offer[offer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains($"a=extmap:1 {TransportCc}", videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Offer_without_extension_uris_emits_no_extmap()
    {
        var offer = SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = new SdpVideoNegotiationOptions { Port = 41002 } });

        Assert.DoesNotContain("a=extmap", offer, StringComparison.Ordinal);
    }

    // ── Answer intersection ──────────────────────────────────────────────────────

    [Fact]
    public void Answer_echoes_the_supported_offered_id_and_drops_the_unsupported()
    {
        var offer = VideoOfferWithExtmaps($"a=extmap:5 {TransportCc}\r\na=extmap:6 {Unknown}\r\n");

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions
            {
                Video = new SdpVideoNegotiationOptions { Port = 41002, HeaderExtensionUris = [TransportCc] },
            });

        Assert.NotNull(answer);
        var videoSection = answer![answer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains($"a=extmap:5 {TransportCc}", videoSection, StringComparison.Ordinal); // offered id echoed
        Assert.DoesNotContain(Unknown, videoSection, StringComparison.Ordinal);               // unsupported dropped
    }

    [Theory]
    [InlineData(0)]   // padding id
    [InlineData(15)]  // reserved id
    public void Answer_drops_a_supported_uri_offered_with_a_non_one_byte_id(int id)
    {
        // Even when the URI is supported, an id outside the one-byte range 1..14 cannot be carried
        // in the one-byte header form, so it is not echoed.
        var offer = VideoOfferWithExtmaps($"a=extmap:{id} {TransportCc}\r\n");

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions
            {
                Video = new SdpVideoNegotiationOptions { Port = 41002, HeaderExtensionUris = [TransportCc] },
            });

        Assert.NotNull(answer);
        Assert.DoesNotContain("a=extmap", answer!, StringComparison.Ordinal);
    }

    [Fact]
    public void Answer_without_supported_uris_emits_no_extmap()
    {
        var offer = VideoOfferWithExtmaps($"a=extmap:5 {TransportCc}\r\n");

        var answer = SdpUtilities.TryBuildNegotiatedAnswer(offer, LocalAudio, hold: false,
            new SdpMediaNegotiationOptions { Video = new SdpVideoNegotiationOptions { Port = 41002 } });

        Assert.NotNull(answer);
        Assert.DoesNotContain("a=extmap", answer!, StringComparison.Ordinal);
    }

    private static string VideoOfferWithExtmaps(string extmapLines) =>
        "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=peer\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n"
        + "m=audio 5002 RTP/AVP 0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n"
        + "m=video 5004 RTP/AVP 96\r\na=rtpmap:96 VP8/90000\r\n" + extmapLines;
}
