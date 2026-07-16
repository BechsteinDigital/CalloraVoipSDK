using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Configuration for one RTP session (RFC 3550 §6).
/// </summary>
internal sealed class RtpSessionOptions
{
    /// <summary>Local endpoint to bind the UDP socket to.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>Remote endpoint to send RTP packets to.</summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>
    /// Payload type for outgoing packets (RFC 3550 §5.1).
    /// Must match the codec negotiated via SDP.
    /// </summary>
    public required byte PayloadType { get; init; }

    /// <summary>
    /// Clock rate in Hz for the payload type (e.g. 8000 for PCMU/PCMA, 16000 for G.722).
    /// Used to increment the RTP timestamp per audio frame.
    /// </summary>
    public required int ClockRate { get; init; }

    /// <summary>
    /// Number of samples per packet (e.g. 160 for 20 ms at 8000 Hz).
    /// Determines timestamp step per sent packet.
    /// </summary>
    public required int SamplesPerPacket { get; init; }

    /// <summary>
    /// Synchronization source identifier. When null, a random value is generated
    /// at session start (RFC 3550 §5.1).
    /// </summary>
    public uint? Ssrc { get; init; }

    /// <summary>
    /// SRTP context protecting outgoing RTP packets with our own negotiated key
    /// (RFC 3711). <see langword="null"/> sends plain RTP.
    /// </summary>
    public ISrtpContext? OutboundSrtp { get; init; }

    /// <summary>
    /// SRTP context unprotecting incoming RTP packets with the peer's negotiated key
    /// (RFC 3711). <see langword="null"/> expects plain RTP; packets failing
    /// authentication or replay checks are dropped.
    /// </summary>
    public ISrtpContext? InboundSrtp { get; init; }

    /// <summary>
    /// SRTCP context protecting outgoing RTCP packets (RFC 3711 §3.4) with our own
    /// negotiated key. <see langword="null"/> sends plain RTCP.
    /// </summary>
    public ISrtcpContext? OutboundSrtcp { get; init; }

    /// <summary>
    /// SRTCP context unprotecting incoming RTCP packets (RFC 3711 §3.4) with the peer's
    /// negotiated key. <see langword="null"/> expects plain RTCP; packets failing
    /// authentication or replay checks are dropped.
    /// </summary>
    public ISrtcpContext? InboundSrtcp { get; init; }

    /// <summary>
    /// Fail-closed switch for keyed-but-not-yet-ready media (DTLS-SRTP, RFC 5763 §6.7.1):
    /// while no security contexts are installed, inbound RTP/RTCP is dropped instead of
    /// being interpreted as plaintext, and outbound media/RTCP sends are suppressed.
    /// Contexts arrive later via <see cref="RtpSession.InstallSecurityContexts"/> once the
    /// DTLS handshake exported keys. <see langword="false"/> keeps the SDES/plain-RTP
    /// behaviour where null contexts mean unencrypted media.
    /// </summary>
    public bool RequireEncryptedMedia { get; init; }

    /// <summary>
    /// Negotiated one-byte header-extension id for the transport-wide sequence number
    /// (transport-cc / RFC 8888). When set, each outgoing RTP packet carries an incrementing
    /// transport-wide counter in an RFC 8285 header extension so the receiver can report arrival
    /// times for congestion control. <see langword="null"/> stamps no extension (default).
    /// </summary>
    public byte? TransportWideCcExtensionId { get; init; }

    /// <summary>
    /// Negotiated one-byte header-extension id for the MID SDES extension (RFC 9143), used on a BUNDLE
    /// transport so the peer can associate this stream's SSRC with its m-line. When set together with
    /// <see cref="Mid"/>, each outgoing RTP packet carries the MID token (alongside the transport-wide
    /// counter when that is also negotiated). <see langword="null"/> stamps no MID (default; non-BUNDLE).
    /// </summary>
    public byte? MidExtensionId { get; init; }

    /// <summary>
    /// This stream's <c>a=mid</c> token (for example <c>audio</c>, <c>video</c>) stamped on outgoing
    /// packets when <see cref="MidExtensionId"/> is set. <see langword="null"/> outside BUNDLE.
    /// </summary>
    public string? Mid { get; init; }
}
