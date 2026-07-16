using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Builds a <see cref="BundledMediaSession"/> from the negotiated call parameters (ADR-011 B5-wire).
/// A BUNDLE call negotiates one DTLS association and one ICE agent for the whole group, so the audio
/// leg's DTLS role/fingerprint and ICE credentials key and steer the shared transport; the audio and
/// video legs contribute their endpoints, payload types, and (for video) codec. The MID tokens, the
/// MID header-extension id, and the SSRCs are BUNDLE-specific facts the signalling layer supplies
/// explicitly — <see cref="CallMediaParameters"/> does not carry them today (BUNDLE is not yet wired
/// into the negotiator), which is why they are parameters here rather than read off the call params.
/// </summary>
internal static class BundledMediaSessionBuilder
{
    /// <summary>
    /// Maps the negotiated audio (and optional video) parameters plus the negotiated BUNDLE facts onto
    /// a running <see cref="BundledMediaSession"/>. The audio leg must have negotiated DTLS-SRTP —
    /// a BUNDLE transport is DTLS-only.
    /// </summary>
    /// <param name="audio">The negotiated audio-leg parameters (shared 5-tuple, DTLS, ICE).</param>
    /// <param name="video">The negotiated video-leg parameters, or null for an audio-only bundle.</param>
    /// <param name="midExtensionId">The negotiated <c>sdes:mid</c> header-extension id.</param>
    /// <param name="audioMid">The audio m-line's MID token (<c>a=mid</c>).</param>
    /// <param name="audioSsrc">The local audio SSRC.</param>
    /// <param name="videoMid">The video m-line's MID token, required when <paramref name="video"/> is set.</param>
    /// <param name="videoSsrc">The local video SSRC, required when <paramref name="video"/> is set.</param>
    /// <param name="initialSequenceNumber">Initial outbound RTP sequence number (RFC 3550 §5.1).</param>
    /// <param name="initialTimestamp">Initial outbound RTP timestamp.</param>
    /// <param name="videoReorderDepth">Reorder-window depth for the video track (packets).</param>
    /// <exception cref="InvalidOperationException">The audio leg did not negotiate DTLS-SRTP or has no peer fingerprint.</exception>
    /// <exception cref="ArgumentException">A video leg is present without a MID or SSRC.</exception>
    public static BundledMediaSession Build(
        CallMediaParameters audio,
        CallVideoParameters? video,
        byte midExtensionId,
        string audioMid,
        uint audioSsrc,
        string? videoMid,
        uint? videoSsrc,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory,
        ushort initialSequenceNumber = 1,
        uint initialTimestamp = 0,
        int videoReorderDepth = 32)
    {
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var options = BuildOptions(
            audio, video, midExtensionId, audioMid, audioSsrc, videoMid, videoSsrc,
            initialSequenceNumber, initialTimestamp, videoReorderDepth);
        return new BundledMediaSession(options, handshaker, certificate, loggerFactory);
    }

    /// <summary>
    /// Maps the negotiated parameters and BUNDLE facts onto <see cref="BundledMediaSessionOptions"/>
    /// without constructing (and binding) the session — the pure, testable core of <see cref="Build"/>.
    /// </summary>
    /// <inheritdoc cref="Build" path="/exception"/>
    public static BundledMediaSessionOptions BuildOptions(
        CallMediaParameters audio,
        CallVideoParameters? video,
        byte midExtensionId,
        string audioMid,
        uint audioSsrc,
        string? videoMid,
        uint? videoSsrc,
        ushort initialSequenceNumber = 1,
        uint initialTimestamp = 0,
        int videoReorderDepth = 32)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrEmpty(audioMid);

        if (!audio.IsDtlsNegotiated)
            throw new InvalidOperationException("A BUNDLE transport requires DTLS-SRTP keying, but the audio leg did not negotiate it.");
        if (string.IsNullOrWhiteSpace(audio.DtlsRemoteFingerprintAlgorithm) || string.IsNullOrWhiteSpace(audio.DtlsRemoteFingerprintValue))
            throw new InvalidOperationException("A BUNDLE transport requires the peer DTLS fingerprint (RFC 5763 §6.7.1).");

        BundledTrackConfig? videoTrack = null;
        if (video is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(videoMid);
            if (videoSsrc is null)
                throw new ArgumentException("A video leg needs a local SSRC.", nameof(videoSsrc));

            videoTrack = new BundledTrackConfig
            {
                Mid = videoMid,
                Ssrc = videoSsrc.Value,
                PayloadType = (byte)video.PayloadType,
                VideoCodecName = video.CodecName,
            };
        }

        return new BundledMediaSessionOptions
        {
            LocalEndPoint = audio.LocalEndPoint,
            RemoteEndPoint = audio.RemoteEndPoint,
            MidExtensionId = midExtensionId,
            Audio = new BundledTrackConfig
            {
                Mid = audioMid,
                Ssrc = audioSsrc,
                PayloadType = (byte)audio.PayloadType,
                SamplesPerPacket = audio.SamplesPerPacket,
            },
            Video = videoTrack,
            DtlsIsClient = audio.DtlsIsClient,
            RemoteFingerprint = new DtlsFingerprint
            {
                Algorithm = audio.DtlsRemoteFingerprintAlgorithm!,
                Value = audio.DtlsRemoteFingerprintValue!,
            },
            Ice = IceMediaParameters.FromCall(audio),
            InitialSequenceNumber = initialSequenceNumber,
            InitialTimestamp = initialTimestamp,
            VideoReorderDepth = videoReorderDepth,
        };
    }
}
