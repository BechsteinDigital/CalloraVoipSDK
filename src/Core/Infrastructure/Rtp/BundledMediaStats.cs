namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Point-in-time transport counters for a bundled media session (the WebRTC media path). Cumulative since
/// the session was created; the facade derives rates (bitrate) from successive snapshots.
/// </summary>
internal readonly record struct BundledMediaStats(
    long PacketsSent,
    long BytesSent,
    long SuppressedSends,
    long PacketsReceived,
    long BytesReceived,
    long DroppedDatagrams);
