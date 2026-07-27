namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One m-line's configuration on a bundled transport (RFC 8843): its MID, synchronisation source, and
/// payload type. A video track additionally names its codec (H.264/VP8) so the session builds a
/// <see cref="BundledVideoTrack"/> for it; an audio track leaves <see cref="VideoCodecName"/> null and
/// exchanges raw RTP packets.
/// </summary>
internal sealed record BundledTrackConfig
{
    /// <summary>The m-line's MID token (<c>a=mid</c>), e.g. "audio" or "video".</summary>
    public required string Mid { get; init; }

    /// <summary>The stream's outbound synchronisation source.</summary>
    public required uint Ssrc { get; init; }

    /// <summary>The negotiated RTP payload type for this m-line.</summary>
    public required byte PayloadType { get; init; }

    /// <summary>
    /// RTP timestamp increment per outbound audio packet (frame samples, e.g. 160 for 20 ms PCMU,
    /// 960 for 20 ms Opus). Ignored for a video track, whose packets carry an explicit frame timestamp.
    /// </summary>
    public int SamplesPerPacket { get; init; }

    /// <summary>
    /// The negotiated RTP timestamp clock rate (Hz) of this m-line's media codec — e.g. 8000 for PCMU/PCMA,
    /// 48000 for Opus, 90000 for video (RFC 3550 §5.1). It converts RTP-timestamp spans to and from wall-clock
    /// seconds: the §A.8 interarrival jitter uses it instead of inferring the clock from packet spacing, and the
    /// Sender Report extrapolates the RTP timestamp from the report instant onto this clock (CF-004e). Defaults
    /// to 8000 Hz (the narrowband audio default) when the negotiation did not carry an explicit rate.
    /// </summary>
    public int ClockRate { get; init; } = 8000;

    /// <summary>
    /// The negotiated RFC 4733 telephone-event (DTMF) payload type for an audio m-line, or
    /// <see langword="null"/> when the peer did not offer/accept telephone-event. When present the session
    /// can send and receive out-of-band DTMF on this track; when null, a DTMF send is an error. Ignored for
    /// a video m-line.
    /// </summary>
    public int? TelephoneEventPayloadType { get; init; }

    /// <summary>
    /// The clock rate (Hz) of the negotiated telephone-event line (RFC 4733 §2.1: the event stream shares the
    /// audio stream's timestamp clock). Used to convert DTMF durations to/from RTP units independently of the
    /// primary audio codec's clock rate. Defaults to 8000 Hz (the RFC 4733 default event clock).
    /// </summary>
    public int TelephoneEventClockRate { get; init; } = 8000;

    /// <summary>The video codec name ("H264"/"VP8") for a video m-line, or null for an audio m-line.</summary>
    public string? VideoCodecName { get; init; }

    /// <summary>
    /// True when the peer advertised Generic NACK (<c>a=rtcp-fb:* nack</c>, RFC 4585) for this video
    /// m-line: the track may report detected inbound loss so the peer can retransmit. Ignored for an
    /// audio m-line; defaults to <see langword="false"/> so feedback the peer did not offer is never sent.
    /// </summary>
    public bool RemoteSupportsNack { get; init; }

    /// <summary>
    /// True when the peer advertised Picture Loss Indication (<c>a=rtcp-fb:* nack pli</c>, RFC 4585 §6.3.1)
    /// for this video m-line: the track may request a keyframe (a throttled PLI) on detected inbound loss.
    /// Ignored for an audio m-line; defaults to <see langword="false"/>.
    /// </summary>
    public bool RemoteSupportsPli { get; init; }

    /// <summary>
    /// The negotiated RTX repair payload type (RFC 4588) for this video m-line — the <c>a=rtpmap</c> rtx
    /// format whose <c>a=fmtp … apt</c> points at <see cref="PayloadType"/>, or <see langword="null"/> when
    /// RTX was not negotiated. When present the track retains its sent packets and answers an inbound Generic
    /// NACK by resending them on a separate RTX stream (own SSRC + this payload type, OSN-prefixed). Ignored
    /// for an audio m-line and for simulcast tracks (RTX per encoding is follow-up work).
    /// </summary>
    public byte? RtxPayloadType { get; init; }

    /// <summary>
    /// The RTX repair stream's own synchronisation source (RFC 4588 §4), allocated distinct from every other
    /// outbound SSRC on the bundle (audio, video primary — RFC 3550 §8.1), or <see langword="null"/> when RTX
    /// was not negotiated or the caller lets the track pick one. Set by the session factory, which owns the
    /// bundle-wide SSRC allocation; when null the track falls back to a repair SSRC distinct only from
    /// <see cref="Ssrc"/>.
    /// </summary>
    public uint? RtxSsrc { get; init; }

    /// <summary>
    /// Send-side simulcast encodings for a video m-line (RFC 8853): each names an <c>a=rid</c> layer
    /// carried on its own SSRC under the shared MID. Empty for a non-simulcast track — the single stream
    /// then uses <see cref="Ssrc"/> / <see cref="PayloadType"/> directly. All layers share the codec and
    /// payload type; only the SSRC and RID differ.
    /// </summary>
    public IReadOnlyList<BundledVideoEncoding> Encodings { get; init; } = [];
}

/// <summary>
/// One send-side simulcast encoding of a video m-line (RFC 8853 / RFC 8851): an <c>a=rid</c> layer id and
/// the SSRC its RTP stream is carried on. The RID is stamped per packet (RFC 8852) so the peer can
/// associate the SSRC with the encoding.
/// </summary>
internal sealed record BundledVideoEncoding
{
    /// <summary>The <c>a=rid</c> layer id (e.g. <c>hi</c>, <c>mid</c>, <c>lo</c>).</summary>
    public required string Rid { get; init; }

    /// <summary>The synchronisation source carrying this encoding's RTP stream.</summary>
    public required uint Ssrc { get; init; }
}
