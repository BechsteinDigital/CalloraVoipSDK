using System.Net;
using System.Collections.ObjectModel;
using CalloraVoipSdk.Core.Security;

namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Negotiated media parameters for one active call leg.
/// Produced by the infrastructure adapter after SDP offer/answer exchange
/// and consumed by the application media orchestrator to set up RTP I/O.
/// </summary>
public sealed class CallMediaParameters
{
    /// <summary>Local UDP endpoint to bind the RTP socket to.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>Remote UDP endpoint to send RTP packets to.</summary>
    public required IPEndPoint RemoteEndPoint { get; init; }

    /// <summary>
    /// True when RTP and RTCP are multiplexed on one UDP port (<c>a=rtcp-mux</c>, RFC 5761).
    /// </summary>
    public bool RtcpMux { get; init; }

    /// <summary>
    /// Local UDP endpoint used for RTCP control traffic.
    /// When null, the runtime derives it from <see cref="LocalEndPoint"/> and <see cref="RtcpMux"/>.
    /// </summary>
    public IPEndPoint? LocalRtcpEndPoint { get; init; }

    /// <summary>
    /// Remote UDP endpoint used for RTCP control traffic.
    /// When null, the runtime derives it from <see cref="RemoteEndPoint"/> and <see cref="RtcpMux"/>.
    /// </summary>
    public IPEndPoint? RemoteRtcpEndPoint { get; init; }

    /// <summary>Negotiated RTP payload type (e.g. 0 = PCMU, 8 = PCMA).</summary>
    public required int PayloadType { get; init; }

    /// <summary>
    /// Normalized negotiated codec name for <see cref="PayloadType"/> (for example PCMU, PCMA, G722).
    /// </summary>
    public string CodecName { get; init; } = string.Empty;

    /// <summary>
    /// Payload-type to codec-name mapping parsed from remote SDP for this audio m-line.
    /// Used to resolve dynamic RTP payload types (96-127) at runtime.
    /// </summary>
    public IReadOnlyDictionary<int, string> PayloadTypeCodecMap { get; init; }
        = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>());

    /// <summary>
    /// Negotiated RTP payload type for RFC 4733 <c>telephone-event</c> DTMF.
    /// Null when RTP DTMF was not negotiated for this call leg.
    /// </summary>
    public int? TelephoneEventPayloadType { get; init; }

    /// <summary>Codec clock rate in Hz (e.g. 8000 for G.711).</summary>
    public required int ClockRate { get; init; }

    /// <summary>Number of audio samples per RTP packet (e.g. 160 for 20 ms at 8000 Hz).</summary>
    public required int SamplesPerPacket { get; init; }

    /// <summary>
    /// True when ICE metadata is available for this call leg and candidate checks can run.
    /// </summary>
    public bool IceEnabled { get; init; }

    /// <summary>
    /// True when this agent holds the ICE controlling role (RFC 8445 §5.1.1): the SDP offerer is
    /// controlling, the answerer controlled. Defaults to controlling to preserve the outbound-offer
    /// case; the infrastructure adapter sets it from the actual offer/answer direction.
    /// </summary>
    public bool IceControlling { get; init; } = true;

    /// <summary>
    /// Local ICE username fragment used for connectivity checks.
    /// </summary>
    public string? LocalIceUfrag { get; init; }

    /// <summary>
    /// Local ICE password used for connectivity checks.
    /// </summary>
    public string? LocalIcePwd { get; init; }

    /// <summary>
    /// Local ICE options attribute value.
    /// </summary>
    public string? LocalIceOptions { get; init; }

    /// <summary>
    /// Remote ICE username fragment used for connectivity checks.
    /// </summary>
    public string? RemoteIceUfrag { get; init; }

    /// <summary>
    /// Remote ICE password used for connectivity checks.
    /// </summary>
    public string? RemoteIcePwd { get; init; }

    /// <summary>
    /// Remote ICE options attribute value.
    /// </summary>
    public string? RemoteIceOptions { get; init; }

    /// <summary>
    /// Local ICE candidates advertised by this SDK for the media m-line.
    /// </summary>
    public IReadOnlyList<CallIceCandidate> LocalIceCandidates { get; init; } = [];

    /// <summary>
    /// Remote ICE candidates parsed from peer SDP for the media m-line.
    /// </summary>
    public IReadOnlyList<CallIceCandidate> RemoteIceCandidates { get; init; } = [];

    /// <summary>
    /// True when the remote SDP signaled <c>a=end-of-candidates</c>.
    /// </summary>
    public bool RemoteIceEndOfCandidates { get; init; }

    /// <summary>
    /// Negotiated media profile from the remote SDP m-line (for example <c>RTP/AVP</c> or <c>RTP/SAVP</c>).
    /// </summary>
    public string MediaProfile { get; init; } = "RTP/AVP";

    /// <summary>
    /// Effective SRTP policy applied to this call during offer/answer evaluation.
    /// </summary>
    public SrtpPolicy AppliedSrtpPolicy { get; init; } = SrtpPolicy.Optional;

    /// <summary>
    /// True when negotiated media is SRTP according to SDP profile/attributes.
    /// </summary>
    public bool IsSrtpNegotiated { get; init; }

    /// <summary>
    /// Stable reason code for SRTP policy decision outcome.
    /// See <see cref="SrtpDecisionReasonCodes"/>.
    /// </summary>
    public string SrtpDecisionReasonCode { get; init; } = SrtpDecisionReasonCodes.NotEvaluated;

    /// <summary>
    /// Negotiated SDES crypto-suite token (for example <c>AES_CM_128_HMAC_SHA1_80</c>) when SRTP
    /// media encryption engaged for this leg; <see langword="null"/> for plain RTP. Exposed read-only
    /// for audit/compliance; the value is stamped by the media-negotiation layer.
    /// </summary>
    public string? SrtpSuite { get; internal init; }

    /// <summary>
    /// True when RTCP for this leg is encrypted and authenticated as SRTCP (RFC 3711 §3.4). SRTCP
    /// protection engages together with SRTP media whenever SDES key material was negotiated for
    /// both directions; <see langword="false"/> for plain RTP/RTCP.
    /// </summary>
    public bool IsSrtcpEncrypted { get; internal init; }

    // ── SDES key material (RFC 4568) — internal media-layer contract ─────────
    // Kept as plain SDP value strings so the domain stays free of crypto types;
    // the RTP media session parses them into SRTP contexts. Both directions must
    // be present for encryption to engage; null means plain RTP.

    /// <summary>Our answer's inline key params — encrypts the outbound direction.</summary>
    internal string? SrtpLocalKeyParams { get; init; }

    /// <summary>The peer's inline key params — decrypts the inbound direction.</summary>
    internal string? SrtpRemoteKeyParams { get; init; }

    // ── DTLS-SRTP keying (RFC 5763/5764) — internal media-layer contract ─────
    // Kept as plain strings so the domain stays free of crypto types; the RTP media
    // session runs the DTLS handshake and derives the SRTP contexts from it.

    /// <summary>
    /// True when offer/answer negotiated DTLS-SRTP keying for this leg (RFC 5763):
    /// the media layer must complete a DTLS handshake before any media flows and stays
    /// fail-closed until then. Mutually exclusive with SDES key material.
    /// </summary>
    public bool IsDtlsNegotiated { get; internal init; }

    /// <summary>
    /// True when this endpoint takes the DTLS client role, i.e. the negotiated SDP
    /// <c>a=setup</c> resolved to <c>active</c> locally (RFC 5763 §5 / RFC 4145).
    /// </summary>
    internal bool DtlsIsClient { get; init; }

    /// <summary>Peer fingerprint hash function from SDP <c>a=fingerprint</c> (RFC 8122), e.g. <c>sha-256</c>.</summary>
    internal string? DtlsRemoteFingerprintAlgorithm { get; init; }

    /// <summary>Peer fingerprint digest from SDP <c>a=fingerprint</c> (colon-delimited hex).</summary>
    internal string? DtlsRemoteFingerprintValue { get; init; }
}
