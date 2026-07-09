namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// One SDES chunk: an SSRC/CSRC followed by one or more SDES items (RFC 3550 §6.5).
/// On the wire a chunk is padded to a 4-byte boundary after the END item.
/// </summary>
internal sealed class RtcpSdesChunk
{
    public required uint Ssrc { get; init; }
    public IReadOnlyList<RtcpSdesItem> Items { get; init; } = [];
}
