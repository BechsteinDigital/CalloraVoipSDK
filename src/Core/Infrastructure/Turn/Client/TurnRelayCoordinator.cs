using System.Net;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Composition root for one TURN relay over the shared media transport: it owns the shared-socket
/// transaction engine (<see cref="TurnControlTransactor"/> → <see cref="TurnTransactionEngine"/> →
/// <see cref="TurnRelayControlClient"/> → <see cref="TurnRelayAllocator"/>) and drives them against the
/// transport's control surface (<see cref="IRelayControlTransport"/>). It sends TURN requests through the
/// transport's control path and is fed inbound control datagrams through <see cref="OnControlDatagram"/>
/// (which the caller wires to the transport's relay-control callback), closing the loop.
/// <para>
/// <see cref="EstablishAsync"/> runs Allocate → CreatePermission → ChannelBind and then installs the bound
/// channel on the transport, moving it from the control phase into the data phase. Refresh scheduling and
/// teardown build on the returned <see cref="TurnRelayAllocation"/> in a later slice.
/// </para>
/// </summary>
internal sealed class TurnRelayCoordinator
{
    private readonly IRelayControlTransport _transport;
    private readonly IPEndPoint _relayServer;
    private readonly TurnControlTransactor _transactor;
    private readonly TurnRelayAllocator _allocator;

    /// <summary>
    /// Wires the coordinator to <paramref name="transport"/>'s control surface and the relay server. The
    /// caller must route the transport's relay-control callback to <see cref="OnControlDatagram"/> for the
    /// response path to close (a construction cycle broken with a capturing lambda at the call site).
    /// </summary>
    /// <param name="transport">The shared media transport's relay control surface.</param>
    /// <param name="relayServer">The TURN server's transport address (must match the transport's relay server).</param>
    /// <param name="codec">The STUN wire codec.</param>
    /// <param name="logger">Logger for the underlying transactor.</param>
    public TurnRelayCoordinator(
        IRelayControlTransport transport,
        IPEndPoint relayServer,
        IStunMessageCodec codec,
        ILogger<TurnRelayCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(relayServer);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _relayServer = relayServer;
        _transactor = new TurnControlTransactor(
            codec,
            (request, ct) => transport.SendControlAsync(request, ct).AsTask(),
            logger);
        _allocator = new TurnRelayAllocator(new TurnRelayControlClient(new TurnTransactionEngine(codec), _transactor));
    }

    /// <summary>
    /// Feeds one inbound TURN control datagram (from the transport's relay-control callback) into the
    /// transaction engine to be matched to its pending request by transaction id.
    /// </summary>
    /// <param name="datagram">The raw inbound control datagram.</param>
    public void OnControlDatagram(ReadOnlyMemory<byte> datagram) => _transactor.OnControlDatagram(datagram);

    /// <summary>
    /// Establishes the relay — Allocate → CreatePermission → ChannelBind for <paramref name="peerEndPoint"/>
    /// — and installs the resulting channel on the transport, activating the data path. Returns the
    /// allocation (relayed address, lifetime, effective credentials) for refresh/teardown.
    /// </summary>
    /// <param name="peerEndPoint">The peer to relay for (the nominated ICE remote).</param>
    /// <param name="channelNumber">The channel to bind; must be in the TURN range 0x4000..0x7FFF.</param>
    /// <param name="credentials">Long-term credentials, or <see langword="null"/> for an open server.</param>
    /// <param name="lifetimeSeconds">Requested allocation lifetime, or <see langword="null"/> for the server default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The established allocation.</returns>
    /// <exception cref="TurnException">Any establishment step failed; the data path is not activated.</exception>
    public async Task<TurnRelayAllocation> EstablishAsync(
        IPEndPoint peerEndPoint,
        ushort channelNumber,
        StunCredentials? credentials,
        uint? lifetimeSeconds,
        CancellationToken ct)
    {
        var allocation = await _allocator
            .EstablishAsync(_relayServer, peerEndPoint, channelNumber, credentials, lifetimeSeconds, ct)
            .ConfigureAwait(false);

        // Only after the whole sequence succeeds do we flip the transport to the data phase — a failure
        // above throws before this, leaving the transport suppressing media rather than framing to an
        // unbound channel.
        _transport.SetRelayChannel(allocation.Channel);
        return allocation;
    }
}
