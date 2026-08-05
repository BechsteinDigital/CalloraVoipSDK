using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #160 SDP P1-b end-to-end: an offerer applying a remote answer validates it against its own offer
/// (RFC 3264 §6 / RFC 8829) and fails closed on a mismatch, before any transport or track is built.
/// </summary>
public sealed class WebRtcAnswerValidationTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    [Fact]
    public async Task Offerer_accepts_a_conforming_answer()
    {
        await using var offerer = Peer();
        await using var answerer = Peer();

        var offer = offerer.CreateOffer();
        var answer = await answerer.SetRemoteDescriptionAsync(offer);
        await offerer.SetRemoteDescriptionAsync(answer);

        Assert.Equal(WebRtcSignalingState.Stable, offerer.SignalingState);
    }

    [Fact]
    public async Task Offerer_rejects_an_answer_with_a_renamed_mid()
    {
        await using var offerer = Peer();
        await using var answerer = Peer();

        var offer = offerer.CreateOffer();
        var answer = await answerer.SetRemoteDescriptionAsync(offer);
        // Rename the first m-line's MID to one the offer never used → no longer a 1:1 response.
        var firstMidLine = answer.Split("\r\n").First(l => l.StartsWith("a=mid:", StringComparison.Ordinal));
        var hostile = answer.Replace(firstMidLine, "a=mid:zzz", StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => offerer.SetRemoteDescriptionAsync(hostile));

        Assert.Contains("valid response to the local offer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(WebRtcConnectionState.Failed, offerer.State);
    }

    private static WebRtcPeerConnection Peer() =>
        new(
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                VideoTracks =
                [
                    new SdpVideoMediaOptions
                    {
                        Port = 6002,
                        Codecs = [new SdpCodecDefinition { PayloadType = 96, Name = "H264", ClockRate = 90000 }],
                    },
                ],
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new SdpOfferAnswerNegotiator(), new SdpSessionParser(), new SdpSessionSerializer(),
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance), DtlsCertificate.GenerateEcdsaP256(),
            NullLoggerFactory.Instance);
}
