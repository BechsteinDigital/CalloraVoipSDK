using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Sdp.OfferAnswer;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Parsing;
using CalloraVoipSdk.Core.Infrastructure.WebRtc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Outbound audio follows the negotiated direction (RFC 8829/3264), the audio counterpart to the video gate
/// (external-review finding #4, audio portion). Unlike video, audio is never dropped — the audio m-line
/// anchors the bundle transport (ICE/DTLS) and inbound audio is always received — so the session is still
/// built, but <see cref="BundledMediaSession.AudioSendEnabled"/> is false (outbound audio suppressed) when the
/// remote will not receive it (send-only / inactive) or this peer does not send it (recv-only / inactive).
/// </summary>
public sealed class WebRtcOutboundAudioNegotiationTests
{
    private static readonly IReadOnlyList<SdpCodecDefinition> Pcmu =
        [new SdpCodecDefinition { PayloadType = 0, Name = "PCMU", ClockRate = 8000 }];

    [Theory]
    [InlineData("sendrecv")] // remote receives our audio
    [InlineData("recvonly")] // remote only receives (asymmetric) -> it receives our audio
    public async Task Outbound_audio_is_enabled_when_both_sides_carry_it(string remoteAudioDirection)
    {
        var session = BuildSession(Offer(), WithAudioDirection(RemoteAnswer(), remoteAudioDirection));

        Assert.NotNull(session);
        await using var _ = session;
        Assert.True(session!.AudioSendEnabled);
    }

    [Theory]
    [InlineData("sendonly")] // remote sends only; it will not receive our audio
    [InlineData("inactive")] // remote receives nothing
    public async Task Outbound_audio_is_suppressed_when_the_remote_will_not_receive(string remoteAudioDirection)
    {
        var session = BuildSession(Offer(), WithAudioDirection(RemoteAnswer(), remoteAudioDirection));

        Assert.NotNull(session);          // audio still anchors the transport, so a session is still built
        await using var _ = session;
        Assert.False(session!.AudioSendEnabled);
    }

    [Theory]
    [InlineData("recvonly")] // this peer only receives -> it does not send audio
    [InlineData("inactive")] // this peer sends nothing
    public async Task Outbound_audio_is_suppressed_when_this_peer_does_not_send(string localAudioDirection)
    {
        var session = BuildSession(WithAudioDirection(Offer(), localAudioDirection), RemoteAnswer());

        Assert.NotNull(session);
        await using var _ = session;
        Assert.False(session!.AudioSendEnabled);
    }

    [Fact]
    public async Task Send_dtmf_is_a_no_op_when_outbound_audio_is_suppressed()
    {
        // Remote is send-only → AudioSendEnabled is false. DTMF rides the audio stream, so it must be a no-op
        // (like SendAudioAsync) rather than leaking onto a stream the peer declared it will not receive. This
        // Pcmu-only negotiation carries no telephone-event, yet SendDtmf does NOT throw "not negotiated" — the
        // audio-send gate short-circuits first (before the fix this reached the telephone-event check and threw).
        var session = BuildSession(Offer(), WithAudioDirection(RemoteAnswer(), "sendonly"));

        Assert.NotNull(session);
        await using var _ = session;
        Assert.False(session!.AudioSendEnabled);

        await session.SendDtmfAsync(toneCode: 5, durationMs: 100); // completes without throwing; nothing is sent
    }

    private static BundledMediaSession? BuildSession(SdpSessionDescription local, SdpSessionDescription remote) =>
        WebRtcSessionFactory.TryCreate(
            remote, local,
            new WebRtcPeerOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                AudioCodecs = Pcmu,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            },
            new DtlsSrtpHandshaker(NullLogger<DtlsSrtpHandshaker>.Instance),
            DtlsCertificate.GenerateEcdsaP256(), NullLoggerFactory.Instance);

    private static SdpSessionDescription Offer() =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 40090), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "11:22:33", Setup = "actpass" },
                Ice = new SdpIceParameters { Ufrag = "localU", Pwd = "localpassword1234567890" },
            });

    private static SdpSessionDescription RemoteAnswer() =>
        new SdpOfferAnswerNegotiator().CreateOffer(
            new IPEndPoint(IPAddress.Loopback, 5000), Pcmu, SdpMediaDirection.SendRecv,
            new SdpMediaOptions
            {
                Bundle = true,
                RtcpMux = true,
                Dtls = new SdpDtlsParameters { Algorithm = "sha-256", Fingerprint = "AA:BB:CC", Setup = "active" },
                Ice = new SdpIceParameters { Ufrag = "remoteU", Pwd = "remotepassword1234567890" },
            });

    // Rewrites the audio section's direction attribute (round-tripped through the serializer/parser).
    private static SdpSessionDescription WithAudioDirection(SdpSessionDescription sdp, string direction)
    {
        var lines = new SdpSessionSerializer().Serialize(sdp)
            .Replace("\r\n", "\n").Split('\n').ToList();
        var audioIdx = lines.FindIndex(l => l.StartsWith("m=audio ", StringComparison.Ordinal));
        var sectionEnd = lines.FindIndex(audioIdx + 1, l => l.StartsWith("m=", StringComparison.Ordinal));
        if (sectionEnd < 0)
            sectionEnd = lines.Count;

        for (var i = sectionEnd - 1; i > audioIdx; i--)
            if (IsDirectionAttribute(lines[i]))
                lines.RemoveAt(i);
        lines.Insert(audioIdx + 1, "a=" + direction);

        return new SdpSessionParser().Parse(string.Join("\r\n", lines));

        static bool IsDirectionAttribute(string line) =>
            line is "a=sendrecv" or "a=sendonly" or "a=recvonly" or "a=inactive";
    }
}
