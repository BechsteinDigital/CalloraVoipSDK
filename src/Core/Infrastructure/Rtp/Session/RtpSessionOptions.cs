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
    /// SRTP context used to protect (encrypt and authenticate) outbound RTP packets
    /// before they are sent (RFC 3711). <see langword="null"/> when SRTP was not
    /// negotiated for this leg, in which case RTP is sent as cleartext.
    /// </summary>
    public ISrtpContext? OutboundSrtp { get; init; }

    /// <summary>
    /// SRTP context used to unprotect (authenticate and decrypt) inbound SRTP packets
    /// before they are decoded (RFC 3711). <see langword="null"/> when SRTP was not
    /// negotiated for this leg, in which case inbound RTP is treated as cleartext.
    /// </summary>
    public ISrtpContext? InboundSrtp { get; init; }

    /// <summary>
    /// SRTCP context used to protect (encrypt and authenticate) outbound RTCP control
    /// datagrams before they are sent (RFC 3711 §3.4). <see langword="null"/> when SRTP
    /// was not negotiated, in which case RTCP is sent as cleartext.
    /// </summary>
    public ISrtcpContext? OutboundSrtcp { get; init; }

    /// <summary>
    /// SRTCP context used to unprotect (authenticate and decrypt) inbound SRTCP control
    /// datagrams before they are dispatched (RFC 3711 §3.4). <see langword="null"/> when
    /// SRTP was not negotiated, in which case inbound RTCP is treated as cleartext.
    /// </summary>
    public ISrtcpContext? InboundSrtcp { get; init; }
}
