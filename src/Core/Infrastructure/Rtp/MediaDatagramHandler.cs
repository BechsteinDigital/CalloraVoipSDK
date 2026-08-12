using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Rtp;

/// <summary>
/// Receives one classified inbound datagram straight off the media receive buffer, without the
/// pipeline copying it first (#157 P2-8).
/// </summary>
/// <remarks>
/// The buffer is reused for the next datagram, so a handler that needs to keep the bytes past the call
/// must copy them itself — but only <em>after</em> it has decided to keep them. That ordering is the
/// whole point: the previous <c>Action&lt;byte[], IPEndPoint&gt;</c> forced an allocation before anyone
/// had checked the source, the size, or whether there was queue space, so an unauthenticated sender
/// could drive continuous Gen0 pressure with datagrams that were then dropped. A span-taking delegate
/// (rather than <c>Action&lt;ReadOnlySpan&lt;byte&gt;, …&gt;</c>, which does not exist — a ref struct
/// cannot be a generic type argument) lets the handler own that decision.
/// </remarks>
/// <param name="datagram">The datagram, valid only for the duration of the call.</param>
/// <param name="source">The remote endpoint it arrived from.</param>
internal delegate void MediaDatagramHandler(ReadOnlySpan<byte> datagram, IPEndPoint source);
