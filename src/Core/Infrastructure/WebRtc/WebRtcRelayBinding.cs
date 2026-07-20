using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using CalloraVoipSdk.Core.Infrastructure.Turn.Client;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.WebRtc;

/// <summary>
/// Assembles the TURN control stack that backs a relay ICE local candidate for a gathered allocation, exposed
/// as a <see cref="RelayIceBindingFactory"/> the bundle media session invokes once its shared socket exists.
/// This is the TURN-aware producer of the relay binding: it lives in the WebRTC composition layer (which may
/// depend on the TURN module) and hands the Rtp session only the protocol-agnostic <see cref="RelayIceBinding"/>,
/// keeping the media transport off the TURN module.
/// <para>
/// The allocation was made during candidate gathering on the same socket the transport later owns
/// (<c>TurnAllocationProbe</c>), so the control transactor and the relay send path both send to the relay server
/// through the transport's targeted send, and the transport routes the server's control responses back into the
/// transactor via the binding's control sink (RFC 8656 §9/§10). Permission install and NONCE rotation are owned
/// by <see cref="TurnRelayCandidateSendPath"/>.
/// </para>
/// </summary>
internal static class WebRtcRelayBinding
{
    /// <summary>
    /// Builds the factory for a gathered relay allocation. The returned factory, given the transport's targeted
    /// send, constructs the indication channel, the shared-socket control transactor, the authenticated control
    /// client, and the relay send path, and returns them as a <see cref="RelayIceBinding"/>.
    /// </summary>
    /// <param name="relayServer">The TURN server's transport address the allocation lives on.</param>
    /// <param name="allocation">The gathered allocation (its effective credentials prime the control operations).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="permissionLifetimeSeconds">
    /// The TURN permission lifetime (RFC 8656 §9 default 300 s) the permission refresh loop is paced against; it
    /// refreshes at half this value.
    /// </param>
    /// <returns>A factory the media session invokes once the shared socket exists.</returns>
    public static RelayIceBindingFactory CreateFactory(
        IPEndPoint relayServer,
        TurnAllocateResult allocation,
        ILoggerFactory loggerFactory,
        uint permissionLifetimeSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(relayServer);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return targetedSend =>
        {
            ArgumentNullException.ThrowIfNull(targetedSend);

            var codec = new StunMessageCodec();
            var indication = new TurnRelayIndicationChannel(codec, relayServer);
            var transactor = new TurnControlTransactor(
                codec,
                (bytes, ct) => targetedSend(bytes, relayServer, ct).AsTask(),
                loggerFactory.CreateLogger<TurnControlTransactor>());
            var control = new TurnRelayControlClient(new TurnTransactionEngine(codec), transactor);
            var sendPath = new TurnRelayCandidateSendPath(
                indication, control, allocation.EffectiveCredentials, targetedSend,
                loggerFactory.CreateLogger<TurnRelayCandidateSendPath>());

            // The keepalive shares the same authenticated control client (Refresh over the shared socket): the
            // transactor correlates responses by transaction id, so a Refresh in flight next to a CreatePermission
            // is fine. It rides the transport's targeted send too, so — like RelaySend — the session must dispose
            // it (running its teardown Refresh(0)) before it disposes the transport.
            var allocationKeepAlive = new TurnAllocationRefreshLoop(
                control.RefreshAsync, allocation.EffectiveCredentials, allocation.LifetimeSeconds, loggerFactory);

            // Alongside the allocation refresh, keep the per-peer permissions alive (RFC 8656 §9): a permission
            // lapses ~5 min after it was installed, so a long-lived relay path would start dropping inbound
            // datagrams. This loop re-installs every peer the send path knows about (over the same credential
            // gate) and has no teardown — a permission lapses on its own. Both loops run through the session's
            // single keepalive seam, disposed (permission first, then the allocation teardown) before the transport.
            var permissionKeepAlive = new TurnPermissionRefreshLoop(
                sendPath.RefreshInstalledPermissionsAsync, loggerFactory, permissionLifetimeSeconds);

            var keepAlive = new CompositeRelayKeepAlive(allocationKeepAlive, permissionKeepAlive);

            // ChannelBind the nominated peer (RFC 8656 §11) so media can flow as the compact ChannelData framing
            // (§12). Runs while the transport is still in direct mode, so the ChannelBind request reaches the
            // server unframed via the same shared-socket control transactor. One channel per allocation.
            const ushort relayChannelNumber = 0x4000;
            async Task<IRelayDatagramChannel> BindChannel(IPEndPoint peer, CancellationToken ct)
            {
                await control.ChannelBindAsync(peer, relayChannelNumber, allocation.EffectiveCredentials, ct)
                    .ConfigureAwait(false);
                return new TurnRelayChannel(relayServer, relayChannelNumber);
            }

            // OnControlDatagram only matches responses by transaction id (no I/O, no transport reference), so a
            // control datagram arriving after the session is disposed is a harmless no-match. The RelaySend path
            // and the keepalive, by contrast, call the transport's targeted send, so their post-disposal safety
            // relies on the session draining the ICE agent and the keepalive before disposing the transport — a
            // dispose-ordering concern owned by the session, not this producer.
            return new RelayIceBinding(indication, transactor.OnControlDatagram, sendPath.SendAsync, keepAlive, BindChannel);
        };
    }
}
