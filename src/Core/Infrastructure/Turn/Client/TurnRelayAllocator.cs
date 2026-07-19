using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Runs the TURN relay establishment sequence over the shared BUNDLE socket — Allocate → CreatePermission
/// → ChannelBind — and yields a bound <see cref="TurnRelayChannel"/> the media transport can frame through.
/// <para>
/// The effective long-term credentials are threaded from step to step: the Allocate learns the server's
/// REALM/NONCE on the 401 challenge, and CreatePermission/ChannelBind reuse them, so only the Allocate pays
/// the unauthenticated-probe round-trip (RFC 5389 §10.2). Any per-step 438 Stale Nonce is handled inside
/// the control client's transaction engine.
/// </para>
/// <para>
/// This is the sequencing logic only; it establishes the allocation and returns the channel plus the facts
/// a caller needs (relayed address, lifetime, effective credentials). Installing the channel into the media
/// transport (setting <c>Relay</c>, wiring the control hooks) and scheduling refresh are separate slices.
/// </para>
/// </summary>
internal sealed class TurnRelayAllocator
{
    private readonly TurnRelayControlClient _control;

    /// <summary>Creates the allocator over the authenticated shared-socket control client.</summary>
    public TurnRelayAllocator(TurnRelayControlClient control)
    {
        ArgumentNullException.ThrowIfNull(control);
        _control = control;
    }

    /// <summary>
    /// Establishes a UDP relay bound to <paramref name="peerEndPoint"/> on channel
    /// <paramref name="channelNumber"/>, running Allocate → CreatePermission → ChannelBind in order.
    /// </summary>
    /// <param name="relayServer">The TURN server's transport address (the relay's 5-tuple).</param>
    /// <param name="peerEndPoint">The peer to permit and bind the channel to (the nominated ICE remote).</param>
    /// <param name="channelNumber">The channel to bind; must be in the TURN range 0x4000..0x7FFF.</param>
    /// <param name="credentials">Long-term credentials, or <see langword="null"/> for an open server.</param>
    /// <param name="lifetimeSeconds">Requested allocation lifetime, or <see langword="null"/> for the server default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bound channel plus the relayed address, lifetime and effective credentials.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelNumber"/> is out of range.</exception>
    /// <exception cref="TurnException">Any step failed (missing relayed address, server error, timeout).</exception>
    public async Task<TurnRelayAllocation> EstablishAsync(
        IPEndPoint relayServer,
        IPEndPoint peerEndPoint,
        ushort channelNumber,
        StunCredentials? credentials,
        uint? lifetimeSeconds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(relayServer);
        ArgumentNullException.ThrowIfNull(peerEndPoint);
        if (channelNumber < 0x4000 || channelNumber > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "TURN channel numbers must be in range 0x4000..0x7FFF.");

        var allocation = await _control.AllocateAsync(credentials, lifetimeSeconds, ct).ConfigureAwait(false);

        // Thread the effective credentials (now carrying the server's REALM/NONCE) into the follow-up
        // operations so they authenticate in a single round-trip without re-probing.
        var afterPermission = await _control
            .CreatePermissionAsync(peerEndPoint, allocation.EffectiveCredentials, ct)
            .ConfigureAwait(false);

        var afterChannelBind = await _control
            .ChannelBindAsync(peerEndPoint, channelNumber, afterPermission, ct)
            .ConfigureAwait(false);

        return new TurnRelayAllocation
        {
            Channel = new TurnRelayChannel(relayServer, channelNumber),
            RelayedEndPoint = allocation.RelayedEndPoint,
            LifetimeSeconds = allocation.LifetimeSeconds,
            EffectiveCredentials = afterChannelBind
        };
    }
}
