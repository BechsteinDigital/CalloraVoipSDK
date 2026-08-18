using System.Collections.Concurrent;
using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Discovers the server-reflexive address of a <em>live</em> media socket (RFC 8445 §5.1.1.2) — a plain STUN
/// Binding transaction (RFC 5389 §7) sent over a transport whose receive loop is already running.
/// </summary>
/// <remarks>
/// <para>
/// Pre-start gathering can own the socket and run its own receive loop; after the transport has started it
/// cannot, because the datagram would be read by the receive loop instead. So this inverts the read side: the
/// request goes out through the transport's raw send, and the response comes back in through the same inbound
/// STUN feed that serves the ICE agent — the owner wires <see cref="OnStunPacketReceived"/> to it. Transactions
/// are matched by id (RFC 5389 §6), so a datagram belonging to anyone else is ignored here, and a response to a
/// probe is equally a no-op for the ICE agent, whose consent registry matches ids the same way.
/// </para>
/// <para>
/// This is what makes an ICE restart able to re-gather without giving up the socket — and giving up the socket
/// is what would cost the DTLS association and the SRTP contexts riding on it.
/// </para>
/// <para>Thread-safety (K3): the pending-transaction map is concurrent; probes may run in parallel.</para>
/// </remarks>
internal sealed class IceReflexiveProbe
{
    // RFC 5389 §7.2.1: a UDP Binding request is retransmitted, because a single lost datagram must not be
    // reported as "no reflexive address". Kept short — this runs alongside live media, not during setup.
    private const int Attempts = 3;

    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _send;
    private readonly IStunMessageCodec _codec;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint?>> _pending = new(StringComparer.Ordinal);

    /// <param name="send">Raw, unframed send over the live transport (the request must reach the STUN server as-is).</param>
    /// <param name="codec">Encodes the request and decodes the response.</param>
    /// <param name="logger">Diagnostics for a probe that finds nothing.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public IceReflexiveProbe(
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> send,
        IStunMessageCodec codec,
        ILogger logger)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Feeds an inbound STUN datagram from the transport's demux. Completes a pending probe when the transaction
    /// id matches and the message is a Binding success response carrying XOR-MAPPED-ADDRESS; anything else is
    /// ignored, so this can sit on the shared feed alongside the ICE agent.
    /// </summary>
    /// <param name="datagram">The demultiplexed STUN datagram.</param>
    public void OnStunPacketReceived(byte[] datagram)
    {
        if (datagram is null || _pending.IsEmpty)
            return;

        var message = _codec.Decode(datagram);
        if (message is not { MessageClass: StunMessageClass.SuccessResponse, MessageMethod: StunMessageMethod.Binding })
            return;

        if (!_pending.TryRemove(Key(message.TransactionId), out var pending))
            return;

        if (message.Attributes.OfType<XorMappedAddressAttribute>().FirstOrDefault() is { } mapped)
            pending.TrySetResult(mapped.EndPoint);
        else
            pending.TrySetResult(null); // answered, but without the one attribute that makes it useful
    }

    /// <summary>
    /// Runs one Binding transaction against <paramref name="server"/> over the live transport and returns the
    /// reflexive endpoint it reports, or <see langword="null"/> when the server does not answer within
    /// <paramref name="timeout"/> (per attempt) or answers without XOR-MAPPED-ADDRESS. Never throws for an
    /// unreachable server — a missing candidate is not an error, it is one fewer path to try.
    /// </summary>
    /// <param name="server">The STUN server's transport address.</param>
    /// <param name="timeout">Per-attempt wait before retransmitting.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The server-reflexive endpoint, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public async Task<IPEndPoint?> ProbeAsync(IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var request = StunMessage.CreateBindingRequest();
        var key = Key(request.TransactionId);
        var completion = new TaskCompletionSource<IPEndPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion))
            return null; // transaction id collision is not credible; treat it as no result rather than throwing

        try
        {
            var datagram = _codec.Encode(request);
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                // The same transaction id is retransmitted, so a late answer to an earlier attempt still counts
                // (RFC 5389 §7.2.1) — retransmission is not a new transaction.
                await _send(datagram, server, cancellationToken).ConfigureAwait(false);

                var completed = await Task.WhenAny(
                    completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
                if (completed == completion.Task)
                    return await completion.Task.ConfigureAwait(false);
            }

            _logger.LogDebug(
                "STUN server {Server} did not answer a Binding request over the live transport after {Attempts} attempts; " +
                "no server-reflexive candidate from it.", server, Attempts);
            return null;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private static string Key(byte[] transactionId) => Convert.ToHexString(transactionId);
}
