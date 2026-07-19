using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Result of a successful TURN Allocate transaction.
/// </summary>
internal sealed class TurnAllocateResult
{
    /// <summary>
    /// Relayed transport endpoint allocated on the TURN server.
    /// </summary>
    public required IPEndPoint RelayedEndPoint { get; init; }

    /// <summary>
    /// Client reflexive endpoint as observed by the TURN server, when provided.
    /// </summary>
    public IPEndPoint? MappedEndPoint { get; init; }

    /// <summary>
    /// Allocation lifetime in seconds.
    /// </summary>
    public uint LifetimeSeconds { get; init; }

    /// <summary>
    /// Credentials including latest realm/nonce values for follow-up TURN requests.
    /// </summary>
    public StunCredentials? EffectiveCredentials { get; init; }

    /// <summary>
    /// RFC 8016 mobility ticket returned by the server, when requested/supported.
    /// </summary>
    public byte[]? MobilityTicket { get; init; }

    /// <summary>
    /// The RESERVATION-TOKEN returned when the allocation was made with EVEN-PORT (reserve), or
    /// <see langword="null"/> otherwise. Replay it via <c>TurnAllocateOptions.ReservationToken</c> in a later
    /// Allocate to claim the reserved companion port (RFC 8656 §7).
    /// </summary>
    public ulong? ReservationToken { get; init; }
}
