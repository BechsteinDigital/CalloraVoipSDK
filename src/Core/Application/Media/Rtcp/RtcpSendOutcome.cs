namespace CalloraVoipSdk.Core.Application.Media.Rtcp;

/// <summary>
/// What actually happened to an outbound RTCP compound (#162 P2-5).
/// </summary>
/// <remarks>
/// The RTCP send paths fail closed: before the outbound SRTCP key is installed — and on a send racing
/// transport teardown — the compound is dropped rather than sent in the clear. That is correct, but it
/// used to be invisible to the reporter, which returned <c>ValueTask</c> with no result. The reporter
/// therefore committed state for reports that never left the host: it advanced the average RTCP size,
/// latched "we have reported" (which gates the teardown BYE, so a BYE could announce a participant the
/// peer had never heard from), and recorded an LSR the peer could never echo — poisoning the RTT
/// attribution. Reporting state must follow the wire, not the intent.
/// </remarks>
internal enum RtcpSendOutcome
{
    /// <summary>The compound was protected and handed to the transport.</summary>
    Sent,

    /// <summary>
    /// The compound was deliberately dropped — no SRTCP context yet, or the context was disposed by a
    /// concurrent teardown. Not an error: the caller must simply not commit reporting state for it.
    /// </summary>
    Suppressed,
}
