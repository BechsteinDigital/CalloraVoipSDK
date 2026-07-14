namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Packets;

/// <summary>
/// One RFC 8285 header-extension element: a local identifier negotiated via SDP <c>a=extmap</c>
/// and its value bytes. In the one-byte form the identifier is 1..14 (0 is padding, 15 is
/// reserved) and the value is 1..16 bytes long. Used to carry per-packet metadata such as the
/// transport-wide sequence number for congestion control (RFC 8888 / transport-cc).
/// </summary>
internal readonly record struct RtpHeaderExtensionElement(byte Id, ReadOnlyMemory<byte> Value);
