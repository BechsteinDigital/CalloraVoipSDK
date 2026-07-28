using System.Net;
using System.Net.Sockets;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Dtls;
using CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// The negotiated parameters a <see cref="BundledMediaSession"/> assembles a BUNDLE group from
/// (RFC 8843): the shared 5-tuple, the MID header-extension id, the audio and the (zero or more) video
/// tracks, and the DTLS-SRTP (RFC 5763) and ICE (RFC 8445) views of the one shared association and agent.
/// Each video m-line (P2b: N video tracks — a camera plus a screen-share pattern) is one entry in
/// <see cref="VideoTracks"/>, carried under its own MID on its own bundle-wide-distinct SSRC(s).
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

    /// <summary>
    /// The negotiated transport-wide-cc header-extension id (<c>a=extmap … transport-wide-cc</c>, RFC 8888 /
    /// draft-holmer), or <see langword="null"/> when the extension was not negotiated on the bundle. When
    /// present the transport stamps a single transport-wide sequence number across every track (transport-cc
    /// is transport-wide, not per-stream) so the peer can report arrivals and this side can run congestion
    /// control, and this side sends receive-side feedback for the peer's own controller.
    /// </summary>
    public byte? TransportWideCcExtensionId { get; init; }

    /// <summary>The audio m-line configuration.</summary>
    public required BundledTrackConfig Audio { get; init; }

    /// <summary>
    /// Whether outbound audio is sent. The audio m-line always anchors the bundle transport (ICE/DTLS ride
    /// it) and inbound audio is always received, but a remote that will not receive audio (a send-only or
    /// inactive answer) or a local side that does not send it must not have audio streamed at it (RFC 3264).
    /// Defaults to <see langword="true"/>; the SIP path leaves it so.
    /// </summary>
    public bool AudioSendEnabled { get; init; } = true;

    /// <summary>
    /// The video m-line configurations (P2b: N video tracks, RFC 8843 §9). Empty for an audio-only bundle;
    /// one entry per negotiated sending video m-line, each keyed by its own <see cref="BundledTrackConfig.Mid"/>
    /// and carried on its own bundle-wide-distinct SSRC(s) (RFC 3550 §8.1). The order is stable — the first
    /// entry is the primary video track (the one the single-track mid-less send/receive facade addresses for
    /// backward compatibility). All entries share the one transport, DTLS association, and ICE agent; inbound
    /// packets are demultiplexed to the right track by MID header extension (RFC 9143) when they share a
    /// payload type, so two same-codec video streams never cross-talk.
    /// </summary>
    public IReadOnlyList<BundledTrackConfig> VideoTracks { get; init; } = [];

    /// <summary>Whether this side runs the DTLS client role (RFC 5763 setup:active).</summary>
    public required bool DtlsIsClient { get; init; }

    /// <summary>The peer certificate fingerprint that authenticates the DTLS handshake.</summary>
    public required DtlsFingerprint RemoteFingerprint { get; init; }

    /// <summary>The ICE view of the shared 5-tuple (credentials, role, nominated remote).</summary>
    public required IceMediaParameters Ice { get; init; }

    /// <summary>
    /// Builds the relay ICE binding (TURN Send/Data indications + per-peer permissions, RFC 8656) once the
    /// shared socket exists, enabling a relay ICE local candidate alongside the direct one. <see langword="null"/>
    /// — the SIP path and any session without a gathered TURN allocation — leaves the transport direct-only.
    /// Constructed by the TURN-aware composition layer so this Rtp session depends only on the binding
    /// abstraction, not the TURN module.
    /// </summary>
    public RelayIceBindingFactory? RelayIceBindingFactory { get; init; }

    /// <summary>Reorder-window depth for the video track (packets); ignored for audio-only.</summary>
    public int VideoReorderDepth { get; init; } = 32;

    /// <summary>Initial RTP sequence number for the outbound tracks (RFC 3550 §5.1 random start).</summary>
    public ushort InitialSequenceNumber { get; init; } = 1;

    /// <summary>Initial RTP timestamp for the outbound tracks.</summary>
    public uint InitialTimestamp { get; init; }

    /// <summary>
    /// An explicit RTCP SDES CNAME (RFC 3550 §6.5.1) for the session, or <see langword="null"/> to generate a
    /// random opaque per-session CNAME (RFC 7022). Never defaults to the machine name (privacy/correlation).
    /// </summary>
    public string? Cname { get; init; }
}
