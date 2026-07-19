using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// The control surface a relay coordinator drives on the shared media transport, kept protocol-agnostic so
/// the TURN orchestration depends on this abstraction rather than the concrete media transport (mirroring
/// how <see cref="IRelayDatagramChannel"/> keeps the transport off the TURN types). It carries the two
/// operations the coordinator needs: sending a TURN control request to the relay server on the shared
/// socket, and installing the bound channel once the channel-bind completes.
/// </summary>
internal interface IRelayControlTransport
{
    /// <summary>
    /// Sends a TURN control request (unwrapped, addressed to the relay server) on the shared socket. Its
    /// response arrives on the receive loop and is surfaced via the transport's relay-control callback.
    /// </summary>
    /// <param name="request">The already-encoded TURN request datagram.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SendControlAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken);

    /// <summary>
    /// Installs the bound relay channel, switching the transport's data path from suppressed to
    /// ChannelData-framed.
    /// </summary>
    /// <param name="channel">The channel produced by the allocation sequence.</param>
    void SetRelayChannel(IRelayDatagramChannel channel);
}
