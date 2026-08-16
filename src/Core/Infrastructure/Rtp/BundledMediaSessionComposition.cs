using System.Collections.Concurrent;
using CalloraVoipSdk.Core.Infrastructure.Rtp.Session;
using Microsoft.Extensions.Logging;

using CalloraVoipSdk.Core.Application.Media.Rtcp.Wire;

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
        // Each additional inbound audio m-line (4.7.0: N audio tracks) resolves to its own negotiated codec
        // clock / Audio, attributed to its MID. When two audio tracks share a payload type (two same-codec
        // streams), a single PT-keyed clock entry cannot carry both MIDs — the primary already holds that PT and
        // wins the key; the extra track keeps the same Audio/clock, so only the per-track MID attribution of
        // inbound jitter is affected for a shared PT (routing itself is by MID, RFC 9143, unaffected).
        foreach (var audio in options.AdditionalAudioTracks)
        {
            if (!map.ContainsKey(audio.PayloadType))
                map[audio.PayloadType] = new BundledInboundClockDescriptor(
                    audio.ClockRate > 0 ? (uint)audio.ClockRate : 0u, BundledStreamKind.Audio, audio.Mid);
        }
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
    /// <para>
    /// Mutable and concurrent by design (#161 P2-11): a live-added track contributes its SSRCs here and a
    /// deactivated one drops them, so the attribution follows the bundle instead of freezing at construction.
    /// The metrics snapshot reads it while the control plane mutates it.
    /// </para>
    /// </summary>
    public static ConcurrentDictionary<uint, BundledOutboundStreamIdentity> BuildOutboundStreamIdentity(
        BundledMediaSessionOptions options)
    {
        var map = new ConcurrentDictionary<uint, BundledOutboundStreamIdentity>();
        map[options.Audio.Ssrc] = new BundledOutboundStreamIdentity(options.Audio.Mid, BundledStreamKind.Audio);
        foreach (var audio in options.AdditionalAudioTracks)
            map[audio.Ssrc] = new BundledOutboundStreamIdentity(audio.Mid, BundledStreamKind.Audio);
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
    /// Baut die Transport-CC-Ebene des Bundles (draft-holmer), oder <see langword="null"/>, wenn die
    /// <c>a=extmap</c> nicht ausgehandelt wurde und der Transport folglich keine transport-weite
    /// Sequenz stempelt.
    /// </summary>
    /// <remarks>
    /// #162 P2-3: Ein Bundle hat einen Transport, also auch eine Entscheidung über die RTCP-Form.
    /// Sobald auch nur eine Sektion kein <c>a=rtcp-rsize</c> ausgehandelt hat, sendet die Ebene
    /// Compounds — im Zweifel die Form, die jeder Empfänger annehmen muss (RFC 3550 §6.1).
    /// </remarks>
    public static BundledCongestionPlane? BuildCongestionPlane(
        BundledMediaSessionOptions options,
        BundledOutboundPipeline outbound,
        BundledInboundPipeline inbound,
        IRtcpPacketCodec rtcpCodec,
        ILoggerFactory loggerFactory)
    {
        if (options.TransportWideCcExtensionId is not { } transportCcExtensionId)
            return null;

        var reducedSizeRtcp = options.VideoTracks.Count > 0
                              && options.VideoTracks.All(v => v.ReducedSizeRtcp);

        return new BundledCongestionPlane(
            transportCcExtensionId, outbound, inbound, rtcpCodec, options.Audio.Ssrc, loggerFactory,
            reducedSizeRtcp);
    }

    public static BundledVideoTrack BuildVideoTrack(
        BundledMediaSessionOptions options, BundledTrackConfig video, BundledOutboundPipeline outbound, ILoggerFactory loggerFactory)
    {
        var codecName = video.VideoCodecName
            ?? throw new ArgumentException("A video track must name its codec.", nameof(options));

        // The track is built BEFORE anything is registered, and every registration this call makes is undone
        // if a later step throws (#161 P2-11). Registering first left a live outbound sender behind whenever
        // the track constructor rejected the config — sending on a MID with no track, holding its SSRCs and
        // its MID key, so even a corrected retry failed with "already registered".
        if (video.Encodings.Count > 0)
        {
            // Send-side simulcast (RFC 8853): one outbound RTP stream per a=rid layer under the shared
            // MID, each on its own SSRC with the negotiated RID header extension (RFC 8852) stamped.
            var ridExtensionId = options.RidExtensionId ?? throw new ArgumentException(
                "A simulcast video track needs a negotiated RID header-extension id.", nameof(options));

            var track = new BundledVideoTrack(
                video.Mid, codecName, video.PayloadType, video.Ssrc,
                video.RemoteSupportsNack, video.RemoteSupportsPli,
                video.Encodings.Select(e => e.Rid).ToArray(),
                outbound, options.VideoReorderDepth, loggerFactory,
                receiveRids: video.ReceiveRids);

            var registered = new List<string?>(video.Encodings.Count);
            try
            {
                foreach (var encoding in video.Encodings)
                {
                    outbound.RegisterTrack(video.Mid, encoding.Rid,
                        BuildEncodingTrack(options, video.Mid, encoding.Ssrc, video.PayloadType, encoding.Rid, ridExtensionId));
                    registered.Add(encoding.Rid);
                }
            }
            catch
            {
                // Undo exactly the layers this call registered — never a MID-wide sweep, which on the
                // (gated, unreachable) duplicate path would tear down someone else's live registration.
                foreach (var rid in registered)
                    outbound.UnregisterTrack(video.Mid, rid);
                track.Dispose();
                throw;
            }

            return track;
        }

        var plain = new BundledVideoTrack(
            video.Mid, codecName, video.PayloadType, video.Ssrc,
            video.RemoteSupportsNack, video.RemoteSupportsPli,
            outbound, options.VideoReorderDepth, loggerFactory,
            // RTX repair stream (RFC 4588): retain sent packets and resend on an inbound NACK. Wired for
            // the non-simulcast track only — per-encoding simulcast RTX is follow-up work. Its repair
            // SSRC is allocated bundle-wide-distinct by the factory (RFC 3550 §8.1).
            rtxPayloadType: video.RtxPayloadType,
            rtxSsrc: video.RtxSsrc,
            // The negotiated inbound RID allowlist (#161 P3-15); empty admits every RID, as before.
            receiveRids: video.ReceiveRids);

        try
        {
            outbound.RegisterTrack(video.Mid, BuildOutboundTrack(options, video));
        }
        catch
        {
            plain.Dispose();
            throw;
        }

        return plain;
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

    /// <summary>
    /// Folds the per-SSRC outbound quality (RTT + the loss the peer reports on our media, per <em>our sending</em>
    /// SSRC) and the per-SSRC inbound interarrival jitter (per <em>remote</em> SSRC) into one per-stream list
    /// (CF-004f). Streams with a known MID are keyed by MID so both directions of a track land in one entry (a
    /// simulcast MID folds its encodings' SSRCs into one video entry, taking the worst RTT/loss); an inbound
    /// source whose payload type was not negotiated has no MID and is surfaced on its own SSRC with a null MID —
    /// the honest limit of inbound remote-SSRC attribution. The two directions do not share an SSRC, so an entry
    /// carries RTT/loss (outbound) or jitter (inbound); a MID active in both folds them together. Pure — no
    /// session state beyond the three snapshots the session passes in.
    /// </summary>
    public static IReadOnlyList<BundledStreamQuality> FoldStreamQuality(
        IReadOnlyList<BundledOutboundSsrcQuality> outboundPerSsrc,
        IReadOnlyList<BundledInboundSsrcJitter> inboundJitterPerSsrc,
        IReadOnlyDictionary<uint, BundledOutboundStreamIdentity> outboundStreamIdentity)
    {
        var byMid = new Dictionary<string, BundledStreamQualityAccumulator>(StringComparer.Ordinal);
        var unkeyed = new List<BundledStreamQuality>();

        foreach (var outbound in outboundPerSsrc)
        {
            if (!outboundStreamIdentity.TryGetValue(outbound.Ssrc, out var identity))
                continue; // a report about an SSRC we do not send (should not happen) — do not fabricate a stream.

            var acc = GetOrAddMid(byMid, identity.Mid, identity.Kind, outbound.Ssrc);
            acc.MergeOutbound(outbound.RoundTripTimeMs, outbound.RemotePacketLossFraction);
        }

        foreach (var inbound in inboundJitterPerSsrc)
        {
            if (inbound.Mid is { } mid)
            {
                var acc = GetOrAddMid(byMid, mid, inbound.Kind, inbound.Ssrc);
                acc.MergeInboundJitter(inbound.JitterMs);
            }
            else
            {
                // No MID resolvable (unmapped payload type / SR-only source): surface it on its own SSRC with the
                // kind we could derive — the honest limit of inbound remote-SSRC attribution.
                unkeyed.Add(new BundledStreamQuality(
                    Mid: null, inbound.Ssrc, inbound.Kind, PacketLoss: null, JitterMs: inbound.JitterMs, RoundTripTimeMs: null));
            }
        }

        var result = new List<BundledStreamQuality>(byMid.Count + unkeyed.Count);
        foreach (var acc in byMid.Values)
            result.Add(acc.ToStreamQuality());
        result.AddRange(unkeyed);
        return result;
    }

    private static BundledStreamQualityAccumulator GetOrAddMid(
        Dictionary<string, BundledStreamQualityAccumulator> byMid, string mid, BundledStreamKind kind, uint ssrc)
    {
        if (!byMid.TryGetValue(mid, out var acc))
        {
            acc = new BundledStreamQualityAccumulator(mid, ssrc, kind);
            byMid[mid] = acc;
        }

        return acc;
    }

    /// <summary>
    /// Emits one out-of-band DTMF tone as an RFC 4733 telephone-event burst on the primary audio track: an
    /// event-start packet (marker set, half the duration) followed by two end-of-event packets (E-bit, full
    /// duration — the second a reliability retransmission per RFC 4733 §2.5.1.4), all sharing one RTP timestamp on
    /// the telephone-event payload type. The event shares the audio stream's timestamp clock (RFC 4733 §2.1): the
    /// whole burst is stamped with the audio track's current cursor, and the event's full duration is reserved so
    /// the cursor advances past it — otherwise a following event (or media) reuses this timestamp and a receiver
    /// folds it into this event, dropping the repeated tone. Extracted from <see cref="BundledMediaSession"/> for
    /// the size limit; the caller has already validated the tone/duration and resolved the payload type.
    /// </summary>
    public static async ValueTask SendDtmfBurstAsync(
        BundledOutboundPipeline outbound, string audioMid, byte toneCode, int durationMs, int clockRate,
        byte payloadType, CancellationToken cancellationToken)
    {
        var durationRtpUnits = RtpTelephoneEventCodec.DurationMsToRtpUnits(durationMs, clockRate);
        var startDurationRtpUnits = (ushort)Math.Max(1, durationRtpUnits / 2);
        var eventTimestamp = outbound.ReserveTrackTimestamp(audioMid, durationRtpUnits);

        var startPayload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: false, durationRtpUnits: startDurationRtpUnits);
        var endPayload = RtpTelephoneEventCodec.BuildPayload(toneCode, endOfEvent: true, durationRtpUnits: durationRtpUnits);

        await outbound.SendTimestampedAsync(
            audioMid, startPayload, marker: true, payloadType: payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await outbound.SendTimestampedAsync(
            audioMid, endPayload, marker: false, payloadType: payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // RFC 4733 §2.5.1.4 reliability recommendation: repeat the final (end-of-event) packet.
        await outbound.SendTimestampedAsync(
            audioMid, endPayload, marker: false, payloadType: payloadType, timestamp: eventTimestamp,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

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
