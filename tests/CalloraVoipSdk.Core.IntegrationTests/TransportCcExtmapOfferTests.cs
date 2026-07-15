using System.Net;
using CalloraVoipSdk.Core.Application.Ports.Sdp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The transport-cc a=extmap offer (RFC 8285 / draft-holmer): the SDK advertises the transport-wide
/// header extension on the video m-line (the switch that enables transport-cc once a peer echoes it)
/// and never on audio. Exercises the SDP generation path the channel's BuildVideoOptions feeds.
/// </summary>
public sealed class TransportCcExtmapOfferTests
{
    private static readonly IPEndPoint LocalAudio = new(IPAddress.Loopback, 43000);

    private static string OfferWithTransportCcVideo() =>
        SdpUtilities.BuildDefaultSdp(LocalAudio, hold: false,
            new SdpMediaNegotiationOptions
            {
                Video = new SdpVideoNegotiationOptions
                {
                    Port = 43002,
                    HeaderExtensionUris = [RtpHeaderExtensionUris.TransportWideCc],
                },
            });

    [Fact]
    public void Offer_video_mline_advertises_the_transport_cc_extmap()
    {
        var offer = OfferWithTransportCcVideo();

        var videoSection = offer[offer.IndexOf("m=video", StringComparison.Ordinal)..];
        Assert.Contains("a=extmap:", videoSection, StringComparison.Ordinal);
        Assert.Contains(RtpHeaderExtensionUris.TransportWideCc, videoSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_mline_carries_no_extmap()
    {
        var offer = OfferWithTransportCcVideo();

        var audioSection = offer[
            offer.IndexOf("m=audio", StringComparison.Ordinal)..offer.IndexOf("m=video", StringComparison.Ordinal)];
        Assert.DoesNotContain("a=extmap", audioSection, StringComparison.Ordinal);
    }
}
