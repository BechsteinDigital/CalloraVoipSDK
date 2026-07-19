using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Turn.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Turn.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Runs the TURN allocation control operations (Allocate / CreatePermission / ChannelBind / Refresh) over
/// the one shared BUNDLE socket, authenticating with long-term credentials. It composes the
/// transport-agnostic <see cref="TurnTransactionEngine"/> (message construction + the 401/438
/// challenge/response flow) with the <see cref="TurnControlTransactor"/> (a single round-trip over the
/// shared socket), so a relay allocation is established on the media socket's 5-tuple — the precondition
/// for the relayed data path to flow through that socket.
/// <para>
/// Attribute sets here are the relay-data-path subset (Allocate is always REQUESTED-TRANSPORT=UDP), which
/// is deliberately narrower than <see cref="TurnClient"/>'s full option surface. Sequencing these into a
/// bound channel, and wiring the transactor to the transport's control hooks, is the orchestrator's job
/// (later slice); this type is the authenticated per-operation surface it drives.
/// </para>
/// </summary>
internal sealed class TurnRelayControlClient
{
    private readonly TurnTransactionEngine _engine;
    private readonly TurnControlTransactor _transactor;

    /// <summary>Creates the control client over the shared auth engine and shared-socket transactor.</summary>
    public TurnRelayControlClient(TurnTransactionEngine engine, TurnControlTransactor transactor)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(transactor);
        _engine = engine;
        _transactor = transactor;
    }

    /// <summary>
    /// Allocates a UDP relay on the TURN server, authenticating as needed. Returns the relayed address and
    /// lifetime plus the effective credentials (updated with the server's REALM/NONCE) for the follow-up
    /// permission/channel-bind/refresh operations.
    /// </summary>
    /// <param name="credentials">Long-term credentials, or <see langword="null"/> for an unauthenticated server.</param>
    /// <param name="lifetimeSeconds">Requested allocation lifetime, or <see langword="null"/> for the server default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocation result (relayed endpoint, lifetime, effective credentials).</returns>
    /// <exception cref="TurnException">The success response is missing XOR-RELAYED-ADDRESS.</exception>
    public async Task<TurnAllocateResult> AllocateAsync(
        StunCredentials? credentials,
        uint? lifetimeSeconds,
        CancellationToken ct)
    {
        var (response, effectiveCredentials) = await _engine.ExecuteWithAuthAsync(
                TurnMessageMethod.Allocate,
                _ => BuildAllocateAttributes(lifetimeSeconds),
                credentials,
                _transactor.RoundTripAsync,
                ct)
            .ConfigureAwait(false);

        var relayed = TurnAttributeMapper.DecodeXorRelayedAddress(response)?.EndPoint;
        if (relayed is null)
            throw new TurnException("TURN Allocate success response is missing XOR-RELAYED-ADDRESS.");

        var mapped = response.Attributes.OfType<XorMappedAddressAttribute>().FirstOrDefault()?.EndPoint
                     ?? response.Attributes.OfType<MappedAddressAttribute>().FirstOrDefault()?.EndPoint;

        return new TurnAllocateResult
        {
            RelayedEndPoint = relayed,
            MappedEndPoint = mapped,
            LifetimeSeconds = TurnAttributeMapper.DecodeLifetime(response)?.Seconds ?? 0,
            EffectiveCredentials = effectiveCredentials,
            MobilityTicket = TurnAttributeMapper.DecodeMobilityTicket(response)?.Ticket.ToArray()
        };
    }

    /// <summary>
    /// Installs a permission for <paramref name="peerEndPoint"/> (keyed by IP, RFC 8656 §9). Returns the
    /// effective credentials to carry into subsequent operations.
    /// </summary>
    /// <param name="peerEndPoint">The peer whose traffic the relay should accept.</param>
    /// <param name="credentials">The (already realm/nonce-primed) long-term credentials, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The effective credentials.</returns>
    public async Task<StunCredentials?> CreatePermissionAsync(
        IPEndPoint peerEndPoint,
        StunCredentials? credentials,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peerEndPoint);

        var (_, effectiveCredentials) = await _engine.ExecuteWithAuthAsync(
                TurnMessageMethod.CreatePermission,
                transactionId =>
                [
                    TurnAttributeMapper.Encode(
                        new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint },
                        transactionId)
                ],
                credentials,
                _transactor.RoundTripAsync,
                ct)
            .ConfigureAwait(false);

        return effectiveCredentials;
    }

    /// <summary>
    /// Binds <paramref name="channelNumber"/> to <paramref name="peerEndPoint"/> (keyed by IP:port,
    /// RFC 8656 §11) so relayed traffic can use the compact ChannelData framing. Returns the effective
    /// credentials.
    /// </summary>
    /// <param name="peerEndPoint">The peer transport address to bind the channel to.</param>
    /// <param name="channelNumber">The channel number; must be in the TURN range 0x4000..0x7FFF.</param>
    /// <param name="credentials">The (already realm/nonce-primed) long-term credentials, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The effective credentials.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelNumber"/> is out of range.</exception>
    public async Task<StunCredentials?> ChannelBindAsync(
        IPEndPoint peerEndPoint,
        ushort channelNumber,
        StunCredentials? credentials,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peerEndPoint);
        if (channelNumber < 0x4000 || channelNumber > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "TURN channel numbers must be in range 0x4000..0x7FFF.");

        var (_, effectiveCredentials) = await _engine.ExecuteWithAuthAsync(
                TurnMessageMethod.ChannelBind,
                transactionId =>
                [
                    TurnAttributeMapper.Encode(new TurnChannelNumberAttribute { ChannelNumber = channelNumber }),
                    TurnAttributeMapper.Encode(
                        new TurnXorPeerAddressAttribute { EndPoint = peerEndPoint },
                        transactionId)
                ],
                credentials,
                _transactor.RoundTripAsync,
                ct)
            .ConfigureAwait(false);

        return effectiveCredentials;
    }

    /// <summary>
    /// Refreshes the allocation to <paramref name="lifetimeSeconds"/> (0 deletes it, RFC 8656 §7). Returns
    /// the granted lifetime and effective credentials.
    /// </summary>
    /// <param name="credentials">The (already realm/nonce-primed) long-term credentials, or null.</param>
    /// <param name="lifetimeSeconds">The requested lifetime; 0 to delete the allocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The refresh result (granted lifetime, effective credentials).</returns>
    public async Task<TurnRefreshResult> RefreshAsync(
        StunCredentials? credentials,
        uint lifetimeSeconds,
        CancellationToken ct)
    {
        var (response, effectiveCredentials) = await _engine.ExecuteWithAuthAsync(
                TurnMessageMethod.Refresh,
                _ =>
                [
                    TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds })
                ],
                credentials,
                _transactor.RoundTripAsync,
                ct)
            .ConfigureAwait(false);

        return new TurnRefreshResult
        {
            LifetimeSeconds = TurnAttributeMapper.DecodeLifetime(response)?.Seconds ?? 0,
            EffectiveCredentials = effectiveCredentials,
            MobilityTicket = TurnAttributeMapper.DecodeMobilityTicket(response)?.Ticket.ToArray()
        };
    }

    private static IReadOnlyList<StunAttribute> BuildAllocateAttributes(uint? lifetimeSeconds)
    {
        var attributes = new List<StunAttribute>
        {
            TurnAttributeMapper.Encode(new TurnRequestedTransportAttribute
            {
                Protocol = TurnRequestedTransportProtocol.Udp
            })
        };

        if (lifetimeSeconds.HasValue)
            attributes.Add(TurnAttributeMapper.Encode(new TurnLifetimeAttribute { Seconds = lifetimeSeconds.Value }));

        return attributes;
    }
}
