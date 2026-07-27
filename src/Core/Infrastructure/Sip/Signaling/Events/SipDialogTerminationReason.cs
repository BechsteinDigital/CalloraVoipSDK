namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Represents one parsed SIP Reason header value (RFC 3326) associated with dialog termination.
/// </summary>
internal sealed class SipDialogTerminationReason
{
    /// <summary>
    /// Creates one immutable dialog termination reason value.
    /// </summary>
    public SipDialogTerminationReason(
        string protocol,
        int? cause = null,
        string? text = null,
        int? retryAfterSeconds = null,
        int? sipStatusCode = null,
        bool remoteInitiated = false)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            throw new ArgumentException("Reason protocol is required.", nameof(protocol));

        Protocol = protocol.Trim();
        Cause = cause;
        Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        RetryAfterSeconds = retryAfterSeconds;
        SipStatusCode = sipStatusCode;
        RemoteInitiated = remoteInitiated;
    }

    /// <summary>
    /// Reason protocol token, for example <c>SIP</c> or <c>Q.850</c>.
    /// </summary>
    public string Protocol { get; }

    /// <summary>
    /// Optional numeric cause code parameter.
    /// </summary>
    public int? Cause { get; }

    /// <summary>
    /// Optional human-readable reason text parameter.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Seconds to wait before retrying the request, parsed from the <c>Retry-After</c> header
    /// of a 503 Service Unavailable response (RFC 7339 §5.3 / RFC 3261 §20.33).
    /// Non-null only when the termination was caused by a 503 that carried a <c>Retry-After</c> header.
    /// </summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>
    /// The authoritative SIP response status code (RFC 3261 §21) that terminated the dialog, when the
    /// termination originated from a SIP final response. This is set independently of
    /// <see cref="Protocol"/>/<see cref="Cause"/>: a response may also carry an RFC 3326 <c>Reason</c>
    /// header (for example <c>Q.850;cause=17</c>) that populates <see cref="Protocol"/>/<see cref="Cause"/>
    /// with protocol-neutral detail, but the SIP status is the authoritative classification signal and
    /// is preserved here so it is not lost behind a non-SIP <c>Reason</c> protocol.
    /// <see langword="null"/> when the termination carried no SIP response status (a BYE-based teardown).
    /// </summary>
    public int? SipStatusCode { get; }

    /// <summary>
    /// <see langword="true"/> when this termination is provably remote-initiated (an inbound BYE or an
    /// inbound CANCEL from the far end), even when the request carried no <c>Reason</c> header. Lets the
    /// public surface report <c>TerminatedBy = Remote</c> for a graceful remote BYE without a SIP status,
    /// while a null-status teardown of unknown origin (transport loss, internal fault) stays
    /// unattributed.
    /// </summary>
    public bool RemoteInitiated { get; }
}
