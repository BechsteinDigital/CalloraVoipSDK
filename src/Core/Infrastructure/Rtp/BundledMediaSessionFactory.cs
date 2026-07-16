using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Builds a <see cref="BundledMediaSession"/> for a negotiated BUNDLE call (ADR-011 B5-wire (b)): it
/// generates fresh, distinct local SSRCs (RFC 3550 §8 — the two tracks must never share a source on the
/// shared transport) and includes a video track only when both a video leg and its bundle MID were
/// negotiated, delegating the parameter mapping to <see cref="BundledMediaSessionBuilder"/>.
///
/// The caller decides a call is bundled (a shared <c>sdes:mid</c> was negotiated —
/// <c>SdpUtilities.TryExtractBundleMid</c>) and passes the recovered MID facts here as primitives.
/// Keeping the SDP extraction with the caller keeps this factory inside the RTP module's SDP-free
/// boundary (the SDP module already depends on RTP; the reverse would be a module cycle).
/// </summary>
internal static class BundledMediaSessionFactory
{
    /// <summary>
    /// Creates the bundle session. A video track joins only when <paramref name="video"/> is present and
    /// <paramref name="videoMid"/> is non-empty (the video m-line was grouped into the bundle).
    /// </summary>
    public static BundledMediaSession Create(
        CallMediaParameters audio,
        CallVideoParameters? video,
        byte midExtensionId,
        string audioMid,
        string? videoMid,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        ILoggerFactory loggerFactory)
    {
        var withVideo = video is not null && !string.IsNullOrEmpty(videoMid);
        var audioSsrc = NewSsrc();

        return BundledMediaSessionBuilder.Build(
            audio,
            withVideo ? video : null,
            midExtensionId,
            audioMid,
            audioSsrc,
            withVideo ? videoMid : null,
            withVideo ? NewSsrc(distinctFrom: audioSsrc) : null,
            handshaker, certificate, loggerFactory);
    }

    // A random non-zero SSRC (RFC 3550 §8), optionally distinct from one already assigned so the audio
    // and video tracks never collide on the shared transport. 31-bit like the single-stream RtpSession.
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
