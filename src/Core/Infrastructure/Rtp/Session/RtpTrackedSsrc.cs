namespace CalloraVoipSdk.Core.Infrastructure.Rtp.Session;

/// <summary>
/// Per-SSRC inbound receive state: the RFC 3550 §A.1 sequence validator plus a monotonic
/// last-activity marker. The marker lets <see cref="RtpSession"/> evict the least-recently-active
/// SSRC when the tracked-SSRC cap is reached, so a source spoofing many SSRCs cannot grow the
/// validator table without bound.
/// </summary>
internal sealed class RtpTrackedSsrc
{
    /// <summary>
    /// Creates a tracker for one SSRC.
    /// </summary>
    public RtpTrackedSsrc(RtpSequenceValidator validator, long lastActivity)
    {
        Validator = validator;
        LastActivity = lastActivity;
    }

    /// <summary>
    /// Sequence validator for this SSRC.
    /// </summary>
    public RtpSequenceValidator Validator { get; }

    /// <summary>
    /// Monotonic order value of the most recent packet seen for this SSRC.
    /// </summary>
    public long LastActivity { get; set; }
}
