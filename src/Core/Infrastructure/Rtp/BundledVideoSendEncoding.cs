using CalloraVoipSdk.Core.Infrastructure.Rtp.Packetisation;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// One outbound RTP stream of a <see cref="BundledVideoTrack"/>: a non-simulcast single stream (RID null)
/// or one simulcast <c>a=rid</c> layer (RFC 8853). It owns the layer's stateful packetiser and a send lock
/// that serialises whole frames on that stream, so a frame's packets never interleave with another frame's
/// on the same SSRC while distinct layers still send independently.
/// </summary>
internal sealed class BundledVideoSendEncoding(string? rid, byte payloadType, IVideoPacketiser packetiser) : IDisposable
{
    /// <summary>The simulcast <c>a=rid</c> layer id, or <see langword="null"/> for the single non-simulcast stream.</summary>
    public string? Rid { get; } = rid;

    /// <summary>The RTP payload type for this encoding's packets.</summary>
    public byte PayloadType { get; } = payloadType;

    /// <summary>The stateful video payload-format packetiser for this encoding.</summary>
    public IVideoPacketiser Packetiser { get; } = packetiser;

    /// <summary>Serialises whole-frame sends on this encoding's RTP stream.</summary>
    public SemaphoreSlim SendSync { get; } = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => SendSync.Dispose();
}
