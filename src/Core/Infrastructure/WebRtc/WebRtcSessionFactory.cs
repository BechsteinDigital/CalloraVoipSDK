using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Rtp;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;
using CalloraVoipSdk.Core.Infrastructure.Sdp.Models;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Builds the shared media transport (<see cref="BundledMediaSession"/>) for a WebRTC peer from the
/// negotiated descriptions (Weg 1 / ADR-011 B5). A WebRTC peer holds all the facts itself — its own
/// answer plus the peer's offer — so it derives the transport options directly (endpoints, DTLS role and
/// remote fingerprint, ICE credentials, payload types, and the BUNDLE MID facts) without the SIP call
/// path's CallMediaParameters/enricher machinery, keeping the WebRTC path off the SIP module.
/// </summary>
internal static class WebRtcSessionFactory
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Creates the bundle session for a negotiated WebRTC exchange, or returns <see langword="null"/>
    /// when the descriptions are not a keyed BUNDLE session (no audio, no sdes:mid, or no DTLS
    /// fingerprint) — WebRTC media is DTLS-SRTP over one BUNDLE group.
    /// </summary>
    public static BundledMediaSession? TryCreate(
        SdpSessionDescription remoteOffer,
        SdpSessionDescription localAnswer,
        WebRtcPeerOptions options,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(remoteOffer);
        ArgumentNullException.ThrowIfNull(localAnswer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var offerAudio = remoteOffer.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci) && !m.Disabled);
        var answerAudio = localAnswer.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci) && !m.Disabled);
        if (offerAudio is null || answerAudio is null)
            return null;

        // BUNDLE facts from the answer we produced: the shared sdes:mid id and the audio m-line's MID.
        var midExtensionId = MidExtensionId(answerAudio);
        if (midExtensionId is null || string.IsNullOrEmpty(answerAudio.Mid))
            return null;

        // Remote media address from the offer's audio m-line (or the session connection line).
        var remoteAddress = offerAudio.ConnectionAddress ?? remoteOffer.ConnectionAddress;
        if (!IPAddress.TryParse(remoteAddress, out var remoteIp))
            return null;
        var remoteEndPoint = new IPEndPoint(remoteIp, offerAudio.Port);

        // DTLS-SRTP (WebRTC is DTLS only): the peer's fingerprint, and our role from the answer's setup
        // (RFC 5763 §5: active = client).
        var fingerprint = offerAudio.Fingerprint ?? remoteOffer.Fingerprint;
        if (fingerprint is null)
            return null;
        var dtlsIsClient = string.Equals(answerAudio.DtlsSetup, "active", Ci);

        var audioCodec = answerAudio.Codecs.FirstOrDefault(c => !c.Name.Equals("telephone-event", Ci));
        if (audioCodec is null)
            return null;
        var clockRate = audioCodec.ClockRate > 0 ? audioCodec.ClockRate : 8000;

        var audioSsrc = NewSsrc();
        var audioTrack = new BundledTrackConfig
        {
            Mid = answerAudio.Mid,
            Ssrc = audioSsrc,
            PayloadType = (byte)audioCodec.PayloadType,
            SamplesPerPacket = clockRate * 20 / 1000, // 20 ms frames
        };

        var videoTrack = TryBuildVideoTrack(localAnswer, audioSsrc);

        var sessionOptions = new BundledMediaSessionOptions
        {
            LocalEndPoint = options.LocalEndPoint,
            RemoteEndPoint = remoteEndPoint,
            MidExtensionId = midExtensionId.Value,
            Audio = audioTrack,
            Video = videoTrack,
            DtlsIsClient = dtlsIsClient,
            RemoteFingerprint = new DtlsFingerprint { Algorithm = fingerprint.Algorithm, Value = fingerprint.Value },
            Ice = new IceMediaParameters(
                remoteEndPoint,
                IceEnabled: true,
                IceControlling: false, // the answerer is ICE-controlled (RFC 8445 §5.1.1)
                LocalIceUfrag: options.Ice.Ufrag,
                LocalIcePwd: options.Ice.Pwd,
                RemoteIceUfrag: offerAudio.IceUfrag,
                RemoteIcePwd: offerAudio.IcePwd),
        };

        return new BundledMediaSession(sessionOptions, handshaker, certificate, loggerFactory);
    }

    private static BundledTrackConfig? TryBuildVideoTrack(SdpSessionDescription localAnswer, uint audioSsrc)
    {
        var video = localAnswer.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci) && !m.Disabled);
        if (video is null || string.IsNullOrEmpty(video.Mid))
            return null;

        // The primary video codec (skip the RTX repair codec, RFC 4588).
        var codec = video.Codecs.FirstOrDefault(c => !c.Name.Equals("rtx", Ci));
        if (codec is null)
            return null;

        return new BundledTrackConfig
        {
            Mid = video.Mid,
            Ssrc = NewSsrc(distinctFrom: audioSsrc),
            PayloadType = (byte)codec.PayloadType,
            VideoCodecName = codec.Name,
        };
    }

    private static byte? MidExtensionId(SdpMediaDescription media)
    {
        var mid = media.Extensions.FirstOrDefault(e => string.Equals(e.Uri, RtpHeaderExtensionUris.Mid, StringComparison.Ordinal));
        return mid is not null && mid.Id is >= 1 and <= 14 ? (byte)mid.Id : null;
    }

    private static uint NewSsrc(uint? distinctFrom = null)
    {
        uint ssrc;
        do
        {
            ssrc = (uint)Random.Shared.Next(1, int.MaxValue);
        }
        while (ssrc == distinctFrom);
        return ssrc;
    }
}
