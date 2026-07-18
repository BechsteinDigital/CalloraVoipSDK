using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The negotiated parameters a <see cref="BundledMediaSession"/> assembles a BUNDLE group from
/// (RFC 8843): the shared 5-tuple, the MID header-extension id, the audio and optional video tracks,
/// and the DTLS-SRTP (RFC 5763) and ICE (RFC 8445) views of the one shared association and agent.
/// </summary>
internal sealed record BundledMediaSessionOptions
{
    /// <summary>The local endpoint the shared UDP socket binds to.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>
    /// A socket the caller already bound (Trickle-ICE early-bind), reused instead of binding a new one so
    /// the offer could advertise the real ephemeral port before the session existed; <see langword="null"/>
    /// binds a fresh socket. The session takes ownership and disposes it.
    /// </summary>
    public UdpClient? PreBoundSocket { get; init; }

    /// <summary>The peer endpoint media, DTLS, and consent checks are sent to.</summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>The negotiated MID header-extension id (<c>a=extmap … sdes:mid</c>).</summary>
    public required byte MidExtensionId { get; init; }

    /// <summary>
    /// The negotiated RID header-extension id (<c>a=extmap … sdes:rtp-stream-id</c>, RFC 8852), or
    /// <see langword="null"/> when no simulcast encoding is configured. Required to stamp the RID on a
    /// simulcast video track's outbound packets.
    /// </summary>
    public byte? RidExtensionId { get; init; }

    /// <summary>The audio m-line configuration.</summary>
    public required BundledTrackConfig Audio { get; init; }

    /// <summary>The video m-line configuration, or null for an audio-only bundle.</summary>
    public BundledTrackConfig? Video { get; init; }

    /// <summary>Whether this side runs the DTLS client role (RFC 5763 setup:active).</summary>
    public required bool DtlsIsClient { get; init; }

    /// <summary>The peer certificate fingerprint that authenticates the DTLS handshake.</summary>
    public required DtlsFingerprint RemoteFingerprint { get; init; }

    /// <summary>The ICE view of the shared 5-tuple (credentials, role, nominated remote).</summary>
    public required IceMediaParameters Ice { get; init; }

    /// <summary>Reorder-window depth for the video track (packets); ignored for audio-only.</summary>
    public int VideoReorderDepth { get; init; } = 32;

    /// <summary>Initial RTP sequence number for the outbound tracks (RFC 3550 §5.1 random start).</summary>
    public ushort InitialSequenceNumber { get; init; } = 1;

    /// <summary>Initial RTP timestamp for the outbound tracks.</summary>
    public uint InitialTimestamp { get; init; }
}
