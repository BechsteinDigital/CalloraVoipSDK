using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SDP <c>a=rid</c> (RFC 8851) and <c>a=simulcast</c> (RFC 8853) wire support: the models parse and
/// serialize the send-side simulcast declaration, the parser recovers them from a media section, and the
/// values round-trip. This is the wire foundation only — the multi-encoding send path is a separate slice.
/// </summary>
public sealed class SdpSimulcastWireTests
{
    private const string SdpWithSimulcast =
        "v=0\r\n" +
        "o=- 1 1 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "c=IN IP4 127.0.0.1\r\n" +
        "t=0 0\r\n" +
        "m=video 5002 UDP/TLS/RTP/SAVPF 96\r\n" +
        "a=mid:video\r\n" +
        "a=rtpmap:96 VP8/90000\r\n" +
        "a=rid:hi send pt=96;max-width=1280;max-height=720\r\n" +
        "a=rid:mid send pt=96;max-width=640\r\n" +
        "a=rid:lo send pt=96\r\n" +
        "a=simulcast:send hi;mid;lo\r\n";

    [Fact]
    public void Rid_parses_id_direction_and_restrictions()
    {
        var rid = SdpRid.TryParse("hi send pt=96;max-width=1280");

        Assert.NotNull(rid);
        Assert.Equal("hi", rid!.Id);
        Assert.Equal("send", rid.Direction);
        Assert.Equal("pt=96;max-width=1280", rid.Restrictions);
        Assert.Equal("hi send pt=96;max-width=1280", rid.Serialize());
    }

    [Fact]
    public void Rid_parses_without_restrictions()
    {
        var rid = SdpRid.TryParse("lo send");

        Assert.NotNull(rid);
        Assert.Equal("lo", rid!.Id);
        Assert.Equal("send", rid.Direction);
        Assert.Null(rid.Restrictions);
        Assert.Equal("lo send", rid.Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hi")]              // no direction
    [InlineData("hi sideways pt=96")] // not send/recv
    public void Rid_is_null_for_invalid(string? value)
        => Assert.Null(SdpRid.TryParse(value!));

    [Fact]
    public void Simulcast_parses_send_list()
    {
        var sc = SdpSimulcast.TryParse("send hi;mid;lo");

        Assert.NotNull(sc);
        Assert.Equal(["hi", "mid", "lo"], sc!.Send);
        Assert.Empty(sc.Recv);
        Assert.Equal("send hi;mid;lo", sc.Serialize());
    }

    [Fact]
    public void Simulcast_parses_send_and_recv()
    {
        var sc = SdpSimulcast.TryParse("send hi;lo recv m");

        Assert.NotNull(sc);
        Assert.Equal(["hi", "lo"], sc!.Send);
        Assert.Equal(["m"], sc.Recv);
        Assert.Equal("send hi;lo recv m", sc.Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Simulcast_is_null_for_empty(string? value)
        => Assert.Null(SdpSimulcast.TryParse(value!));

    [Fact]
    public void Parser_recovers_rids_and_simulcast_from_a_media_section()
    {
        var session = new SdpSessionParser().Parse(SdpWithSimulcast);

        var video = Assert.Single(session.Media);
        Assert.Equal(3, video.Rids.Count);
        Assert.Equal(["hi", "mid", "lo"], video.Rids.Select(r => r.Id));
        Assert.All(video.Rids, r => Assert.Equal("send", r.Direction));
        Assert.NotNull(video.Simulcast);
        Assert.Equal(["hi", "mid", "lo"], video.Simulcast!.Send);
    }

    [Fact]
    public void Serialize_then_reparse_round_trips_rids_and_simulcast()
    {
        var session = new SdpSessionParser().Parse(SdpWithSimulcast);

        var sdp = new SdpSessionSerializer().Serialize(session);
        Assert.Contains("a=rid:hi send pt=96;max-width=1280;max-height=720", sdp, StringComparison.Ordinal);
        Assert.Contains("a=simulcast:send hi;mid;lo", sdp, StringComparison.Ordinal);

        var reparsed = new SdpSessionParser().Parse(sdp);
        var before = session.Media.Single();
        var after = reparsed.Media.Single();
        Assert.Equal(before.Rids, after.Rids);   // SdpRid is a value record over string members
        Assert.Equal(before.Simulcast!.Send, after.Simulcast!.Send);
        Assert.Equal(before.Simulcast.Recv, after.Simulcast.Recv);
    }

    [Fact]
    public void A_section_without_simulcast_has_none_and_emits_none()
    {
        const string sdp =
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "c=IN IP4 127.0.0.1\r\n" +
            "t=0 0\r\n" +
            "m=video 5002 UDP/TLS/RTP/SAVPF 96\r\n" +
            "a=mid:video\r\n" +
            "a=rtpmap:96 VP8/90000\r\n";

        var session = new SdpSessionParser().Parse(sdp);
        var video = session.Media.Single();
        Assert.Empty(video.Rids);
        Assert.Null(video.Simulcast);

        var serialized = new SdpSessionSerializer().Serialize(session);
        Assert.DoesNotContain("a=rid", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("a=simulcast", serialized, StringComparison.Ordinal);
    }
}
