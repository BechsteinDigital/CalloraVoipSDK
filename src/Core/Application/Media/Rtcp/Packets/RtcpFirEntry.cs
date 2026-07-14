namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// One Full Intra Request entry (RFC 5104 §4.3.1): the targeted media source and a
/// command sequence number the sender increments per distinct request, so a retransmitted
/// FIR carries the same number and the receiver ignores duplicates.
/// </summary>
internal sealed record RtcpFirEntry
{
    /// <summary>SSRC of the media source that must produce a keyframe.</summary>
    public required uint MediaSsrc { get; init; }

    /// <summary>Command sequence number (8-bit, wraps) identifying this request.</summary>
    public required byte SequenceNumber { get; init; }
}
