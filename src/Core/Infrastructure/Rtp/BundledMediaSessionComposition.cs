using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Pure composition helpers a <see cref="BundledMediaSession"/> uses to map its negotiated
/// <see cref="BundledMediaSessionOptions"/> onto the collaborators it wires up: the inbound clock/kind/MID
/// map (per payload type), the outbound SSRC→track identity map, and the per-video-m-line outbound senders
/// and <see cref="BundledVideoTrack"/> (P2b: N video tracks, RFC 8843 §9). Extracted so the session stays a
/// wiring/lifecycle unit under the 1000-line rule; these are side-effect-free apart from registering the
/// video senders on the passed-in outbound pipeline.
/// </summary>
internal static class BundledMediaSessionComposition
{
    /// <summary>
    /// The RTP clock rate used for the SR RTP-timestamp extrapolation of video (CF-004e): the fixed 90 kHz
    /// RTP clock (RFC 3551 §5). The bundle video track config carries no per-codec rate, and every supported
    /// video codec (H.264/VP8) runs at 90 kHz. Audio uses its own negotiated codec clock.
    /// </summary>
    public const uint VideoRtpClockRate = 90000;

    /// <summary>
    /// Maps each negotiated inbound payload type to its clock/kind/MID so the reception stats can seed an
    /// inbound source's exact §A.8 clock (and attribute it to a track) by matching the first packet's payload
    /// type — the inbound SSRC is the remote's choice, unknown ahead of time. Audio uses its negotiated codec
    /// clock; each video PT uses 90 kHz (RFC 3551 §5). The RFC 4733 telephone-event PT shares the audio clock
    /// but is DTMF, not media, so it is left out (no inbound reception stream is attributed to it).
    /// </summary>
    public static IReadOnlyDictionary<byte, BundledInboundClockDescriptor> BuildInboundClockMap(
        BundledMediaSessionOptions options)
    {
        var map = new Dictionary<byte, BundledInboundClockDescriptor>
        {
            [options.Audio.PayloadType] = new BundledInboundClockDescriptor(
                options.Audio.ClockRate > 0 ? (uint)options.Audio.ClockRate : 0u,
                BundledStreamKind.Audio,
                options.Audio.Mid),
        };
        // Each video PT resolves to 90 kHz / Video. When two video tracks share a payload type (two same-codec
        // streams), a single PT-keyed clock entry cannot carry both MIDs — the first video track's MID is kept
        // (both are Video/90 kHz, so only the per-track MID attribution of inbound jitter is affected, and only
        // for a shared PT; distinct PTs attribute exactly). The primary is listed first, so it wins the shared
        // key — the honest limit of PT-based inbound attribution, not a media-path concern (routing is by MID).
        foreach (var video in options.VideoTracks)
        {
            if (!map.ContainsKey(video.PayloadType))
                map[video.PayloadType] = new BundledInboundClockDescriptor(VideoRtpClockRate, BundledStreamKind.Video, video.Mid);
        }

        return map;
    }

    /// <summary>
    /// Maps each of our local sending SSRCs to the track (MID + kind) it belongs to, so a per-SSRC outbound
    /// quality snapshot (RTT/loss keyed per our sending SSRC) can be attributed to a stream. Audio SSRC → audio
    /// MID; each video track's single SSRC (or each simulcast encoding's SSRC) → that track's MID. SSRCs are
    /// bundle-wide-distinct (RFC 3550 §8.1), so every entry maps cleanly across N video tracks (P2b).
    /// </summary>
    public static IReadOnlyDictionary<uint, BundledOutboundStreamIdentity> BuildOutboundStreamIdentity(
        BundledMediaSessionOptions options)
    {
        var map = new Dictionary<uint, BundledOutboundStreamIdentity>
        {
            [options.Audio.Ssrc] = new BundledOutboundStreamIdentity(options.Audio.Mid, BundledStreamKind.Audio),
        };
        foreach (var video in options.VideoTracks)
        {
            if (video.Encodings.Count > 0)
            {
                foreach (var encoding in video.Encodings)
                    map[encoding.Ssrc] = new BundledOutboundStreamIdentity(video.Mid, BundledStreamKind.Video);
            }
            else
            {
                map[video.Ssrc] = new BundledOutboundStreamIdentity(video.Mid, BundledStreamKind.Video);
            }
        }

        return map;
    }

    /// <summary>
    /// Builds one video track (P2b): registers its outbound sender(s) on its MID on <paramref name="outbound"/>
    /// and returns the <see cref="BundledVideoTrack"/> that will be the router sink for that MID. Simulcast
    /// (RFC 8853) registers one outbound stream per <c>a=rid</c> encoding on its own SSRC with the RID stamped;
    /// a plain track registers a single stream and wires RTX (RFC 4588) when negotiated. All SSRCs (primary,
    /// per-encoding, and RTX repair) are bundle-wide-distinct — the session factory owns that allocation
    /// (RFC 3550 §8.1).
    /// </summary>
    public static BundledVideoTrack BuildVideoTrack(
        BundledMediaSessionOptions options, BundledTrackConfig video, BundledOutboundPipeline outbound, ILoggerFactory loggerFactory)
    {
        var codecName = video.VideoCodecName
            ?? throw new ArgumentException("A video track must name its codec.", nameof(options));

        if (video.Encodings.Count > 0)
        {
            // Send-side simulcast (RFC 8853): one outbound RTP stream per a=rid layer under the shared
            // MID, each on its own SSRC with the negotiated RID header extension (RFC 8852) stamped.
            var ridExtensionId = options.RidExtensionId ?? throw new ArgumentException(
                "A simulcast video track needs a negotiated RID header-extension id.", nameof(options));
            foreach (var encoding in video.Encodings)
                outbound.RegisterTrack(video.Mid, encoding.Rid,
                    BuildEncodingTrack(options, video.Mid, encoding.Ssrc, video.PayloadType, encoding.Rid, ridExtensionId));

            return new BundledVideoTrack(
                video.Mid, codecName, video.PayloadType, video.Ssrc,
                video.RemoteSupportsNack, video.RemoteSupportsPli,
                video.Encodings.Select(e => e.Rid).ToArray(),
                outbound, options.VideoReorderDepth, loggerFactory);
        }

        outbound.RegisterTrack(video.Mid, BuildOutboundTrack(options, video));
        return new BundledVideoTrack(
            video.Mid, codecName, video.PayloadType, video.Ssrc,
            video.RemoteSupportsNack, video.RemoteSupportsPli,
            outbound, options.VideoReorderDepth, loggerFactory,
            // RTX repair stream (RFC 4588): retain sent packets and resend on an inbound NACK. Wired for
            // the non-simulcast track only — per-encoding simulcast RTX is follow-up work. Its repair
            // SSRC is allocated bundle-wide-distinct by the factory (RFC 3550 §8.1).
            rtxPayloadType: video.RtxPayloadType,
            rtxSsrc: video.RtxSsrc);
    }

    /// <summary>
    /// Builds a single (audio or plain-video) outbound track: its SSRC, payload type, samples-per-packet, and
    /// a header stamper for the MID (and transport-wide-cc, when negotiated). Audio uses its negotiated codec
    /// clock; video uses the fixed 90 kHz RTP clock (RFC 3551 §5).
    /// </summary>
    public static BundledOutboundTrack BuildOutboundTrack(BundledMediaSessionOptions options, BundledTrackConfig track) =>
        new(track.Ssrc, track.PayloadType, track.SamplesPerPacket,
            new RtpOutboundHeaderExtensionStamper(options.TransportWideCcExtensionId, options.MidExtensionId, track.Mid),
            options.InitialSequenceNumber, options.InitialTimestamp,
            clockRate: track.VideoCodecName is null ? (uint)Math.Max(0, track.ClockRate) : VideoRtpClockRate);

    // One simulcast encoding's outbound stream: its own SSRC, the shared video payload type, and a stamper
    // that marks every packet with the MID and this encoding's RID (RFC 8852). Video packets carry an
    // explicit frame timestamp, so the timestamp cursor never advances (samplesPerPacket: 0).
    private static BundledOutboundTrack BuildEncodingTrack(
        BundledMediaSessionOptions options, string mid, uint ssrc, byte payloadType, string rid, byte ridExtensionId) =>
        new(ssrc, payloadType, samplesPerPacket: 0,
            new RtpOutboundHeaderExtensionStamper(
                options.TransportWideCcExtensionId, options.MidExtensionId, mid, ridExtensionId, rid),
            options.InitialSequenceNumber, options.InitialTimestamp,
            clockRate: VideoRtpClockRate);
}
