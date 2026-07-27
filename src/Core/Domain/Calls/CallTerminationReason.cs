namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Coarse, protocol-neutral classification of why a call ended. Derived from the underlying SIP
/// status (RFC 3261 §21) when the termination came from signaling, but expressed so an application
/// can branch on the outcome without knowing SIP.
/// </summary>
public enum CallTerminationCategory
{
    /// <summary>The call ended normally after (or without) being answered — no failure.</summary>
    Completed,

    /// <summary>The remote party was busy (SIP 486 Busy Here / 600 Busy Everywhere).</summary>
    Busy,

    /// <summary>The call was not answered (SIP 408 Request Timeout / 480 Temporarily Unavailable).</summary>
    NoAnswer,

    /// <summary>The remote party actively declined or refused the call (SIP 603/403/401/407).</summary>
    Rejected,

    /// <summary>The pending invitation was cancelled before completion (SIP 487 Request Terminated).</summary>
    Canceled,

    /// <summary>The call failed for another reason (any other SIP 4xx/5xx/6xx, or a non-SIP fault).</summary>
    Failed,
}

/// <summary>
/// Which side ended the call.
/// </summary>
public enum CallTerminatedBy
{
    /// <summary>This endpoint ended the call (hangup, reject, redirect, or a local policy termination).</summary>
    Local,

    /// <summary>The remote party ended the call (BYE, a 4xx/5xx/6xx response, or a CANCEL).</summary>
    Remote,

    /// <summary>The originating side could not be determined.</summary>
    Unknown,
}

/// <summary>
/// Protocol-neutral description of why a call terminated, surfaced on the public call surface. It is
/// populated for both locally and remotely initiated terminations and is readable at (and before) the
/// <see cref="CallState.Terminated"/> state change. <see cref="SipStatusCode"/> and
/// <see cref="ReasonPhrase"/> carry the raw SIP detail when the termination came from SIP signaling;
/// <see cref="Category"/> and <see cref="TerminatedBy"/> classify it without requiring SIP knowledge.
/// </summary>
public sealed record CallTerminationReason
{
    /// <summary>
    /// SIP status code (RFC 3261 §21) that terminated the call, when the termination originated from a
    /// SIP response; <see langword="null"/> for a normal BYE-based completion or a non-SIP termination.
    /// </summary>
    public int? SipStatusCode { get; init; }

    /// <summary>
    /// Human-readable reason phrase or SIP Reason-header text (RFC 3326) when available;
    /// <see langword="null"/> otherwise.
    /// </summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>Coarse, protocol-neutral outcome category.</summary>
    public CallTerminationCategory Category { get; init; }

    /// <summary>Which side ended the call.</summary>
    public CallTerminatedBy TerminatedBy { get; init; }

    /// <summary>
    /// Seconds to wait before retrying, from a SIP <c>Retry-After</c> header (RFC 3261 §20.33);
    /// <see langword="null"/> when the response carried no such hint.
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// Maps a SIP status code to a coarse <see cref="CallTerminationCategory"/> per RFC 3261 §21.
    /// </summary>
    /// <param name="code">
    /// The terminating SIP status code, or <see langword="null"/> for a normal BYE-based completion.
    /// </param>
    /// <returns>The protocol-neutral category for the status.</returns>
    public static CallTerminationCategory CategoryForSipStatus(int? code)
    {
        // A normal completion carries no failure status. Provisional (1xx), success (2xx) and
        // redirection (3xx, RFC 3261 §21.3 — resolved by the redirecting UAC) are all non-failure.
        if (code is null || (code >= 100 && code < 400))
            return CallTerminationCategory.Completed;

        return code switch
        {
            486 or 600 => CallTerminationCategory.Busy,        // RFC 3261 §21.5.6 / §21.6.1 (Busy Here / Busy Everywhere)
            408 or 480 => CallTerminationCategory.NoAnswer,    // RFC 3261 §21.4.7 / §21.5.2 (Request Timeout / Temporarily Unavailable)
            487        => CallTerminationCategory.Canceled,    // RFC 3261 §21.4.26 (Request Terminated — answer to a CANCEL)
            603        => CallTerminationCategory.Rejected,    // RFC 3261 §21.6.2 (Decline)
            403        => CallTerminationCategory.Rejected,    // RFC 3261 §21.4.4 (Forbidden)
            401 or 407 => CallTerminationCategory.Rejected,    // RFC 3261 §21.4.2 / §21.5.5 (Unauthorized / Proxy Auth Required)
            // Any other 4xx/5xx/6xx (RFC 3261 §21.4/§21.5/§21.6) is an unclassified failure.
            >= 400     => CallTerminationCategory.Failed,
            _          => CallTerminationCategory.Completed,   // <100 is not a valid terminating status; treat as non-failure.
        };
    }
}
