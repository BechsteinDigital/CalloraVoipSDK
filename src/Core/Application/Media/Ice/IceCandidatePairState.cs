namespace CalloraVoipSdk.Core.Application.Media.Ice;

/// <summary>
/// State of a candidate pair on an ICE check list (RFC 8445 §6.1.2.6).
/// </summary>
internal enum IceCandidatePairState
{
    /// <summary>Not yet checkable — a pair with the same foundation is being checked first.</summary>
    Frozen,

    /// <summary>Ready to have a connectivity check sent.</summary>
    Waiting,

    /// <summary>A connectivity check has been sent and a response is awaited.</summary>
    InProgress,

    /// <summary>The connectivity check succeeded; the pair is valid.</summary>
    Succeeded,

    /// <summary>The connectivity check failed (error response or timeout after retries).</summary>
    Failed,
}
