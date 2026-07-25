namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Live handle to the implicit subscription created by an accepted inbound SIP REFER
/// (RFC 3515 / RFC 6665). The application places the referred call itself; through this handle
/// it reports that call's progress and final outcome, and the SDK translates each report into a
/// <c>message/sipfrag</c> NOTIFY on the subscription so the transferor sees the real status
/// instead of an optimistic guess.
/// </summary>
/// <remarks>
/// <para>
/// Reporting is optional: an accepted REFER already emits an immediate <c>active</c>/100 Trying
/// NOTIFY. If the application never reports, no further NOTIFY is sent and the transferor's
/// subscription lapses at its advertised expiry.
/// </para>
/// <para>
/// The handle is single-shot on termination: the first of <see cref="ReportSuccess"/> or
/// <see cref="ReportFailure"/> closes the subscription; all later reports (progress or terminal)
/// are ignored. All members are thread-safe and non-blocking — a report schedules the NOTIFY and
/// returns without awaiting the network.
/// </para>
/// </remarks>
public interface IReferSubscription
{
    /// <summary>Marks the subscription as pending (RFC 6665 §4.1.3): the transfer was accepted but the referred
    /// call has not started yet. Makes the immediate NOTIFY carry <c>Subscription-State: pending</c> instead of
    /// <c>active</c>. Only effective when called synchronously inside the transfer handler, before the first
    /// NOTIFY is sent; the first subsequent progress report transitions the subscription to <c>active</c>.
    /// Ignored once the subscription is already active or terminated.</summary>
    void ReportPending();

    /// <summary>Reports that the referred call is being tried — an <c>active</c> NOTIFY with the
    /// sipfrag <c>SIP/2.0 100 Trying</c>. Ignored once the subscription has terminated.</summary>
    void ReportTrying();

    /// <summary>Reports that the referred call is ringing — an <c>active</c> NOTIFY with the
    /// sipfrag <c>SIP/2.0 180 Ringing</c>. Ignored once the subscription has terminated.</summary>
    void ReportRinging();

    /// <summary>Reports arbitrary interim progress of the referred call as an <c>active</c> NOTIFY
    /// carrying the sipfrag <c>SIP/2.0 {statusCode} {reasonPhrase}</c>. Intended for provisional
    /// (1xx) statuses such as 183 Session Progress. Ignored once the subscription has terminated.</summary>
    /// <param name="statusCode">The provisional SIP status code to report.</param>
    /// <param name="reasonPhrase">Optional reason phrase; a sensible default is used when null.</param>
    void ReportProgress(int statusCode, string? reasonPhrase = null);

    /// <summary>Reports that the referred call completed successfully — a <c>terminated</c> NOTIFY
    /// with the sipfrag <c>SIP/2.0 {statusCode} {reasonPhrase}</c> (default 200 OK) — and closes the
    /// subscription. The first terminal report wins; later reports are ignored.</summary>
    /// <param name="statusCode">The final 2xx status code; defaults to 200.</param>
    /// <param name="reasonPhrase">Optional reason phrase; a sensible default is used when null.</param>
    void ReportSuccess(int statusCode = 200, string? reasonPhrase = null);

    /// <summary>Reports that the referred call failed — a <c>terminated</c> NOTIFY with the sipfrag
    /// <c>SIP/2.0 {statusCode} {reasonPhrase}</c> — and closes the subscription. The first terminal
    /// report wins; later reports are ignored.</summary>
    /// <param name="statusCode">The final 3xx-6xx status code (e.g. 486 Busy Here).</param>
    /// <param name="reasonPhrase">Optional reason phrase; a sensible default is used when null.</param>
    void ReportFailure(int statusCode, string? reasonPhrase = null);
}
