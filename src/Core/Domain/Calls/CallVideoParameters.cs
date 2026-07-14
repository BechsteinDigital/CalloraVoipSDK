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

    /// <summary>Local UDP endpoint to bind the video RTP socket to.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>Remote UDP endpoint to send video RTP to.</summary>
    public required IPEndPoint RemoteEndPoint { get; init; }
}
