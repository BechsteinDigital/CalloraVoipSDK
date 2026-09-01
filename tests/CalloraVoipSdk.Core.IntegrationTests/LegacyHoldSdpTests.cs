using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Hold signalled the old way: <c>c=0.0.0.0</c> instead of a direction attribute.
/// </summary>
/// <remarks>
/// RFC 2543 put a call on hold by blanking the connection address, and RFC 3264 §8.4 records what it
/// means. Plenty of gateways and older PBXs still send it. Reading only <c>a=sendonly</c> misses them
/// silently — the call reports itself connected while the other side has us on hold, and the media path
/// keeps aiming at an address that goes nowhere.
/// </remarks>
public sealed class LegacyHoldSdpTests
{
    private static string Sdp(string connection, string direction = "a=sendrecv") =>
        "v=0\r\n"
        + "o=- 1 1 IN IP4 192.0.2.1\r\n"
        + "s=-\r\n"
        + $"c=IN IP4 {connection}\r\n"
        + "t=0 0\r\n"
        + "m=audio 20000 RTP/AVP 0\r\n"
        + "a=rtpmap:0 PCMU/8000\r\n"
        + direction + "\r\n";

    [Fact]
    public void A_blanked_connection_address_is_hold()
    {
        Assert.True(SdpUtilities.IsRemoteHoldSdp(Sdp("0.0.0.0")));
    }

    [Fact]
    public void The_ipv6_form_counts_too()
    {
        // "::" and "::0" are the same address written two ways; a string comparison catches one.
        Assert.True(SdpUtilities.IsRemoteHoldSdp(
            "v=0\r\no=- 1 1 IN IP6 ::1\r\ns=-\r\nc=IN IP6 ::\r\nt=0 0\r\nm=audio 20000 RTP/AVP 0\r\na=sendrecv\r\n"));
    }

    [Fact]
    public void A_real_address_with_sendrecv_is_not_hold()
    {
        Assert.False(SdpUtilities.IsRemoteHoldSdp(Sdp("192.0.2.1")));
    }

    [Fact]
    public void The_direction_still_decides_when_there_is_one()
    {
        Assert.True(SdpUtilities.IsRemoteHoldSdp(Sdp("192.0.2.1", "a=sendonly")));
        Assert.True(SdpUtilities.IsRemoteHoldSdp(Sdp("192.0.2.1", "a=inactive")));
    }

    [Fact]
    public void A_media_level_address_overrides_the_session_one()
    {
        // RFC 8866 §5.7: a connection line on the m-section wins. Held that way, only the audio stream
        // is on hold — reading the session line would report the opposite.
        var sdp =
            "v=0\r\no=- 1 1 IN IP4 192.0.2.1\r\ns=-\r\nc=IN IP4 192.0.2.1\r\nt=0 0\r\n"
            + "m=audio 20000 RTP/AVP 0\r\nc=IN IP4 0.0.0.0\r\na=rtpmap:0 PCMU/8000\r\na=sendrecv\r\n";

        Assert.True(SdpUtilities.IsRemoteHoldSdp(sdp));
    }

    [Fact]
    public void An_address_that_is_not_an_address_is_not_hold()
    {
        // A malformed line must not be read as an instruction. Silence here means "nothing said",
        // which is what an unparsable value is.
        Assert.False(SdpUtilities.IsUnspecifiedAddress("nonsense"));
        Assert.False(SdpUtilities.IsUnspecifiedAddress(null));
        Assert.False(SdpUtilities.IsUnspecifiedAddress("   "));
    }
}
