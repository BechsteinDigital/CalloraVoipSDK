using System.Net;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// The identity of the ICE pair currently nominated (RFC 8445 §8): the remote endpoint checks and media
/// flow to, and the send path they use — <see langword="null"/> for the direct media socket, or a TURN-framed
/// relay path when a relay pair was nominated. Held as one immutable value so the remote and its send path are
/// published and observed atomically (a single volatile reference swap), never as two independently-torn
/// fields.
/// </summary>
/// <param name="Remote">The nominated remote transport address.</param>
/// <param name="Send">
/// The send path for the nominated pair — <c>(datagram, remoteTarget, ct)</c> — or <see langword="null"/> to
/// use the direct media socket.
/// </param>
internal sealed record IceNominatedTarget(
    IPEndPoint Remote,
    Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask>? Send);
