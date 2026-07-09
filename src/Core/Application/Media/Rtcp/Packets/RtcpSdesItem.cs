namespace CalloraVoipSdk.Core.Application.Media.Rtcp.Packets;

/// <summary>
/// A single SDES item: type code + UTF-8 value (RFC 3550 §6.5).
/// Items are encoded as [type(1)] [length(1)] [value(length)] on the wire;
/// the END item (type=0) is implicit and has no length or value.
/// </summary>
internal sealed class RtcpSdesItem
{
    public required RtcpSdesItemType ItemType { get; init; }
    public string Value { get; init; } = string.Empty;
}
