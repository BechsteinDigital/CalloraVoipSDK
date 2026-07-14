using System.Net;

namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Negotiated video parameters for one call leg (WebRTC phase 2). Present on
/// <see cref="CallMediaParameters.Video"/> only when the SDP exchange negotiated an
/// active video m-line with a codec the SDK supports; <see langword="null"/> keeps the
/// call audio-only. Grouped into one object so parameter enrichment passes video
/// through as a unit.
/// </summary>
public sealed class CallVideoParameters
{
    /// <summary>Negotiated video RTP payload type.</summary>
    public required int PayloadType { get; init; }

    /// <summary>Normalized codec name, e.g. <c>VP8</c> or <c>H264</c>.</summary>
    public required string CodecName { get; init; }

    /// <summary>RTP clock rate — 90 kHz for all supported video codecs.</summary>
    public int ClockRate { get; init; } = 90000;

    /// <summary>
    /// Raw SDP <c>a=fmtp</c> parameters of the negotiated payload type (e.g. H.264
    /// <c>packetization-mode=1;profile-level-id=…</c>); <see langword="null"/> when the
    /// peer sent none.
    /// </summary>
    public string? FormatParameters { get; init; }

    /// <summary>
    /// Negotiated RTX repair payload type for retransmission (RFC 4588 §8.1);
    /// <see langword="null"/> when RTX was not negotiated for this stream.
    /// </summary>
    public int? RtxPayloadType { get; init; }

    /// <summary>
    /// True when the peer advertised Generic NACK (<c>a=rtcp-fb:* nack</c>, RFC 4585):
    /// the SDK may report lost packets so the peer can retransmit. When false, loss is not
    /// NACKed (feedback the peer did not offer is not sent).
    /// </summary>
    public bool RemoteSupportsNack { get; init; }

    /// <summary>
    /// True when the peer advertised Picture Loss Indication (<c>a=rtcp-fb:* nack pli</c>,
    /// RFC 4585 §6.3.1): the SDK may request a keyframe on unrecoverable loss.
    /// </summary>
    public bool RemoteSupportsPli { get; init; }

    /// <summary>
    /// Negotiated SDES crypto-suite token for the video m-line (RFC 4568), e.g.
    /// <c>AES_CM_128_HMAC_SHA1_80</c>; <see langword="null"/> when the video stream is not
    /// SDES-keyed (plain RTP or DTLS-keyed). Mutually exclusive with DTLS keying.
    /// </summary>
    public string? SrtpSuite { get; internal init; }

    // ── SDES key material (RFC 4568) for the video m-line — internal media-layer contract ──
    // Kept as plain SDP inline value strings so the domain stays free of crypto types; the
    // video RTP stream parses them into SRTP/SRTCP contexts. Both directions must be present
    // together with SrtpSuite for the stream to key.

    /// <summary>Our answer's inline key params for the video m-line — encrypts outbound video.</summary>
    internal string? SrtpLocalKeyParams { get; init; }

    /// <summary>The peer's inline key params for the video m-line — decrypts inbound video.</summary>
    internal string? SrtpRemoteKeyParams { get; init; }

    /// <summary>Local UDP endpoint to bind the video RTP socket to.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>Remote UDP endpoint to send video RTP to.</summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    // ── ICE for the video 5-tuple (RFC 8445 §7.3 inbound checks + RFC 7675 consent) —
    // internal media-layer contract ─────────────────────────────────────────────────
    // This SDK runs lite-ICE on the negotiated media 5-tuple; the ufrag/pwd are shared
    // session-wide (no BUNDLE — the video m-line reuses the audio/session credentials),
    // while the video stream runs its own consent loop and inbound-check responder on the
    // video port. The remote credentials are recovered from the SDP video/session m-line;
    // the local credentials and role are stamped from the channel's local ICE description.

    /// <summary>
    /// True when ICE is active for this call leg and the video stream should answer inbound
    /// connectivity checks (RFC 8445 §7.3) and run consent freshness (RFC 7675) on the video
    /// 5-tuple. <see langword="false"/> keeps the video port free of ICE (plain media path).
    /// </summary>
    internal bool IceEnabled { get; init; }

    /// <summary>
    /// True when this agent holds the ICE controlling role (RFC 8445 §5.1.1) — the same role
    /// as the audio m-line, since ICE credentials are shared session-wide. Defaults to
    /// controlling to mirror <see cref="CallMediaParameters.IceControlling"/>.
    /// </summary>
    internal bool IceControlling { get; init; } = true;

    /// <summary>Local ICE username fragment for the video stream's connectivity checks (shared session-wide).</summary>
    internal string? LocalIceUfrag { get; init; }

    /// <summary>Local ICE password for the video stream's connectivity checks (shared session-wide).</summary>
    internal string? LocalIcePwd { get; init; }

    /// <summary>Remote ICE username fragment recovered from the peer's SDP for the video m-line.</summary>
    internal string? RemoteIceUfrag { get; init; }

    /// <summary>Remote ICE password recovered from the peer's SDP for the video m-line.</summary>
    internal string? RemoteIcePwd { get; init; }
}
