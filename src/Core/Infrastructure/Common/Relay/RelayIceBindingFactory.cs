using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Common.Relay;

/// <summary>
/// Builds the <see cref="RelayIceBinding"/> for a media session once its shared socket exists, given the
/// transport's targeted raw-send (<c>(datagram, target, ct)</c>). The TURN control round-trips and relayed
/// sends ride that same socket, so the binding can only be constructed after the transport — the factory
/// defers construction to that point. Returns <see langword="null"/> when no relay allocation was gathered,
/// leaving the session direct-only. Implemented in the TURN-aware composition layer so the media transport
/// (<c>Infrastructure/Rtp</c>) depends only on this delegate and <see cref="RelayIceBinding"/>, never the TURN
/// module.
/// </summary>
/// <param name="targetedSend">The transport's targeted raw-send over the shared media socket.</param>
/// <returns>The relay ICE binding, or <see langword="null"/> for a direct-only session.</returns>
internal delegate RelayIceBinding? RelayIceBindingFactory(
    Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> targetedSend);
