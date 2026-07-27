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

    /// <summary>The remote party actively declined or refused the call (SIP 603 Decline / 403 Forbidden).</summary>
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
    /// The terminating SIP status code, or <see langword="null"/> when the termination carried no SIP
    /// response status (a BYE-based teardown or a non-SIP fault).
    /// </param>
    /// <param name="wasConnected">
    /// Whether the call had reached the connected/established state before it ended. Only consulted for
    /// the <paramref name="code"/>-is-<see langword="null"/> case: a null-status teardown is a normal
    /// completion only if the media session was actually up (a graceful remote BYE). A null-status
    /// teardown that never connected (dial/ring abort with no SIP failure response — a transport drop or
    /// an internal fault) is a technical <see cref="CallTerminationCategory.Failed"/>, matching the
    /// reference-stack consensus (Twilio <c>failed</c>, Ozeki <c>Error</c>), not a false Completed.
    /// A non-null <paramref name="code"/> is classified purely from the status and ignores this flag.
    /// </param>
    /// <returns>The protocol-neutral category for the status.</returns>
    public static CallTerminationCategory CategoryForSipStatus(int? code, bool wasConnected = true)
    {
        // No SIP failure status: Completed only if the call was actually connected; otherwise a
        // never-connected abort with no SIP failure signal is a technical failure, not a completion.
        if (code is null)
            return wasConnected ? CallTerminationCategory.Completed : CallTerminationCategory.Failed;

        // Provisional (1xx), success (2xx) and redirection (3xx, RFC 3261 §21.3 — resolved by the
        // redirecting UAC) are all non-failure status ranges.
        if (code >= 100 && code < 400)
            return CallTerminationCategory.Completed;

        return code switch
        {
            486 or 600 => CallTerminationCategory.Busy,        // RFC 3261 §21.5.6 / §21.6.1 (Busy Here / Busy Everywhere)
            408 or 480 => CallTerminationCategory.NoAnswer,    // RFC 3261 §21.4.7 / §21.5.2 (Request Timeout / Temporarily Unavailable)
            487        => CallTerminationCategory.Canceled,    // RFC 3261 §21.4.26 (Request Terminated — answer to a CANCEL)
            603        => CallTerminationCategory.Rejected,    // RFC 3261 §21.6.2 (Decline — an active refusal)
            403        => CallTerminationCategory.Rejected,    // RFC 3261 §21.4.4 (Forbidden — an active refusal)
            // RFC 3261 §21.4.2 / §21.5.5 (401 Unauthorized / 407 Proxy Authentication Required) are
            // authentication challenges, not an active decline — a technical failure, not a rejection.
            401 or 407 => CallTerminationCategory.Failed,
            // Any other 4xx/5xx/6xx (RFC 3261 §21.4/§21.5/§21.6) is an unclassified failure.
            >= 400     => CallTerminationCategory.Failed,
            _          => CallTerminationCategory.Completed,   // <100 is not a valid terminating status; treat as non-failure.
        };
    }
}
