namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// RTCP BYE packet (PT=203) — RFC 3550 §6.6.
/// Signals that one or more SSRCs/CSRCs are leaving the session.
/// An optional reason string (max 255 bytes UTF-8) may be included.
/// </summary>
internal sealed class RtcpByePacket : RtcpPacket
{
    public override RtcpPacketType Type => RtcpPacketType.Bye;

    /// <summary>One or more SSRCs/CSRCs that are leaving the session.</summary>
    public IReadOnlyList<uint> Sources { get; init; } = [];

    /// <summary>Optional human-readable reason for leaving (max 255 bytes UTF-8).</summary>
    public string? Reason { get; init; }
}
