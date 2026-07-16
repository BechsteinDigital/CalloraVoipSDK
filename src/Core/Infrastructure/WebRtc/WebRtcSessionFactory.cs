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
/// description plus the peer's — so it derives the transport options directly (endpoints, DTLS role and
/// remote fingerprint, ICE credentials, payload types, and the BUNDLE MID facts) without the SIP call
/// path's CallMediaParameters/enricher machinery, keeping the WebRTC path off the SIP module. It is
/// role-neutral: <paramref name="localDescription"/> is this peer's description (offer or answer) and
/// <paramref name="remoteDescription"/> is the peer's, so it serves both the offerer and the answerer.
/// </summary>
internal static class WebRtcSessionFactory
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Creates the bundle session for a negotiated WebRTC exchange, or returns <see langword="null"/>
    /// when the descriptions are not a keyed BUNDLE session (no audio, no sdes:mid, or no DTLS
    /// fingerprint) — WebRTC media is DTLS-SRTP over one BUNDLE group.
    /// </summary>
    /// <param name="localDescription">This peer's description (the offer if offering, else the answer).</param>
    /// <param name="remoteDescription">The other peer's description (the answer if we offered, else the offer).</param>
    public static BundledMediaSession? TryCreate(
        SdpSessionDescription remoteDescription,
        SdpSessionDescription localDescription,
        WebRtcPeerOptions options,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(remoteDescription);
        ArgumentNullException.ThrowIfNull(localDescription);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var remoteAudio = remoteDescription.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci) && !m.Disabled);
        // The local m-line's port is a placeholder under ICE (the real address comes via candidates), so
        // the local side is gated by its MID below, not by a zero port.
        var localAudio = localDescription.Media.FirstOrDefault(m => m.MediaType.Equals("audio", Ci));
        if (remoteAudio is null || localAudio is null)
            return null;

        // BUNDLE facts from our own description: the shared sdes:mid id and the audio m-line's MID.
        var midExtensionId = MidExtensionId(localAudio);
        if (midExtensionId is null || string.IsNullOrEmpty(localAudio.Mid))
            return null;

        // Remote media address: the best ICE candidate (RFC 8839), or the m-line address/port.
        var remoteEndPoint = WebRtcRemoteEndPoint.Resolve(remoteAudio, remoteDescription.ConnectionAddress);
        if (remoteEndPoint is null)
            return null;

        // DTLS-SRTP (WebRTC is DTLS only): the peer's fingerprint, and our role from both a=setup values
        // (RFC 5763 §5 / RFC 4145: the active side is the client).
        var fingerprint = remoteAudio.Fingerprint ?? remoteDescription.Fingerprint;
        if (fingerprint is null)
            return null;
        var dtlsIsClient = ResolveIsClient(localAudio.DtlsSetup, remoteAudio.DtlsSetup);

        var audioCodec = localAudio.Codecs.FirstOrDefault(c => !c.Name.Equals("telephone-event", Ci));
        if (audioCodec is null)
            return null;
        var clockRate = audioCodec.ClockRate > 0 ? audioCodec.ClockRate : 8000;

        var audioSsrc = NewSsrc();
        var audioTrack = new BundledTrackConfig
        {
            Mid = localAudio.Mid,
            Ssrc = audioSsrc,
            PayloadType = (byte)audioCodec.PayloadType,
            SamplesPerPacket = clockRate * 20 / 1000, // 20 ms frames
        };

        var videoTrack = TryBuildVideoTrack(localDescription, audioSsrc);

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
                IceControlling: false,
                LocalIceUfrag: options.Ice.Ufrag,
                LocalIcePwd: options.Ice.Pwd,
                RemoteIceUfrag: remoteAudio.IceUfrag,
                RemoteIcePwd: remoteAudio.IcePwd),
        };

        return new BundledMediaSession(sessionOptions, handshaker, certificate, loggerFactory);
    }

    private static BundledTrackConfig? TryBuildVideoTrack(SdpSessionDescription localDescription, uint audioSsrc)
    {
        // Gated by the MID (grouped into the bundle), not the placeholder port under ICE.
        var video = localDescription.Media.FirstOrDefault(m => m.MediaType.Equals("video", Ci));
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

    // Local DTLS role from both a=setup values (RFC 4145 §4 / RFC 5763 §5): a concrete local role wins;
    // an offerer's actpass defers to the peer's answer (peer active → we are server, peer passive → we
    // are client). Defaults to the client role when both are silent (WebRTC answerer default).
    private static bool ResolveIsClient(string? localSetup, string? remoteSetup)
    {
        if (string.Equals(localSetup, "active", Ci)) return true;
        if (string.Equals(localSetup, "passive", Ci)) return false;
        if (string.Equals(remoteSetup, "active", Ci)) return false;
        if (string.Equals(remoteSetup, "passive", Ci)) return true;
        return true;
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
