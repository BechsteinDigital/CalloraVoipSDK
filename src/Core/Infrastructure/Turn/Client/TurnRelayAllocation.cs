using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// The outcome of establishing a TURN relay over a shared socket (<see cref="TurnRelayAllocator"/>): the
/// bound data-path channel plus the allocation facts the caller needs to run the relay — the relayed
/// transport address, the granted lifetime (for the refresh timer) and the effective credentials (the
/// server's REALM/NONCE, carried into refresh/teardown).
/// </summary>
internal sealed class TurnRelayAllocation
{
    /// <summary>The bound channel media is framed through (a <see cref="TurnRelayChannel"/>).</summary>
    public required IRelayDatagramChannel Channel { get; init; }

    /// <summary>The relayed transport address the server allocated for this client.</summary>
    public required IPEndPoint RelayedEndPoint { get; init; }

    /// <summary>The granted allocation lifetime in seconds (drives the refresh schedule).</summary>
    public required uint LifetimeSeconds { get; init; }

    /// <summary>
    /// The effective credentials after the sequence — updated with the server's REALM/NONCE — to
    /// authenticate later Refresh/teardown without re-probing. <see langword="null"/> for an open server.
    /// </summary>
    public StunCredentials? EffectiveCredentials { get; init; }
}
