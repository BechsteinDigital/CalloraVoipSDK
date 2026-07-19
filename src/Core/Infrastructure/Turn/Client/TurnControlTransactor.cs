using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Client;

/// <summary>
/// Drives TURN control transactions (Allocate / CreatePermission / ChannelBind / Refresh) over a UDP
/// socket the transactor does <em>not</em> own — the one shared BUNDLE 5-tuple the media transport binds.
/// <para>
/// Unlike <see cref="TurnClientTransport"/>, which opens a fresh socket per transaction and runs its own
/// receive loop, this transactor sends via an injected delegate (the bundle transport's control-send
/// path) and is fed inbound control datagrams from the bundle receive loop through
/// <see cref="OnControlDatagram"/>, correlating a response to its request by STUN transaction id. That
/// is what lets a relay allocation live on the media socket's 5-tuple, so the relayed data path (ICE
/// checks, DTLS flights, RTP/RTCP) can be framed through the same channel.
/// </para>
/// <para>
/// It applies RFC 8489 §6.2.1 UDP retransmission (RTO backoff) because the shared socket is unreliable,
/// registers the pending transaction synchronously before sending (so a fast response cannot be missed,
/// mirroring the ICE shared-socket registry), and validates the response via
/// <see cref="TurnResponseValidator"/>. Long-term-credential auth orchestration and the
/// Allocate→Permission→ChannelBind sequence that yields a bound channel sit on top of this primitive in
/// later slices; this type is the request/response engine they build on.
/// </para>
/// </summary>
internal sealed class TurnControlTransactor
{
    /// <summary>Default initial UDP retransmission timeout (RFC 8489 §6.2.1 RTO).</summary>
    private static readonly TimeSpan DefaultInitialRto = TimeSpan.FromMilliseconds(500);

    /// <summary>Default cap for the doubled UDP retransmission timeout.</summary>
    private static readonly TimeSpan DefaultMaxRto = TimeSpan.FromMilliseconds(16_000);

    /// <summary>Default total transmission attempts (1 send + 6 retransmissions).</summary>
    private const int DefaultMaxAttempts = 7;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<StunMessage>> _pending
        = new(StringComparer.Ordinal);

    private readonly IStunMessageCodec _codec;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _send;
    private readonly ILogger _logger;
    private readonly TimeSpan _initialRto;
    private readonly TimeSpan _maxRto;
    private readonly int _maxAttempts;

    /// <summary>
    /// Creates a transactor that sends encoded requests through <paramref name="send"/> and is driven by
    /// inbound datagrams via <see cref="OnControlDatagram"/>.
    /// </summary>
    /// <param name="codec">The STUN wire codec used to decode inbound control datagrams.</param>
    /// <param name="send">
    /// Sends an already-encoded datagram to the relay server over the shared socket (the bundle
    /// transport's raw control-send path).
    /// </param>
    /// <param name="logger">Logger for discarded/unmatched datagrams and retransmission traces.</param>
    /// <param name="initialRto">Initial retransmission timeout; defaults to 500 ms.</param>
    /// <param name="maxRto">Cap for the doubled retransmission timeout; defaults to 16 s.</param>
    /// <param name="maxAttempts">Total transmission attempts; defaults to 7.</param>
    public TurnControlTransactor(
        IStunMessageCodec codec,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> send,
        ILogger logger,
        TimeSpan? initialRto = null,
        TimeSpan? maxRto = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(logger);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one transmission attempt is required.");

        _codec = codec;
        _send = send;
        _logger = logger;
        _initialRto = initialRto ?? DefaultInitialRto;
        _maxRto = maxRto ?? DefaultMaxRto;
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// Feeds one inbound STUN control datagram (surfaced by the bundle transport's relay-control hook)
    /// into the transactor: decodes it and, when its transaction id matches a pending request, completes
    /// that request. Non-STUN datagrams, undecodable datagrams and unmatched transaction ids are ignored
    /// — this is not the media path, so a stray datagram is dropped rather than raised.
    /// </summary>
    /// <param name="datagram">The raw inbound control datagram from the relay server.</param>
    public void OnControlDatagram(ReadOnlyMemory<byte> datagram)
    {
        StunMessage? decoded;
        try
        {
            decoded = _codec.Decode(datagram.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "TURN control datagram ({Bytes} bytes) could not be decoded; ignored.", datagram.Length);
            return;
        }

        if (decoded is null)
        {
            _logger.LogTrace("TURN control datagram ({Bytes} bytes) was not a STUN message; ignored.", datagram.Length);
            return;
        }

        var key = Convert.ToHexString(decoded.TransactionId);
        if (_pending.TryGetValue(key, out var tcs))
            tcs.TrySetResult(decoded);
        else
            _logger.LogTrace("TURN control response for unknown transaction {TxId}; ignored.", key);
    }

    /// <summary>
    /// Sends <paramref name="requestBytes"/> and returns the transaction-matched, validated success
    /// response. Registration happens synchronously before the first send so a fast response cannot race
    /// the await. Retransmits with RTO backoff until a response arrives or the attempts are exhausted.
    /// </summary>
    /// <remarks>
    /// Cancellation via <paramref name="ct"/> propagates as an <see cref="OperationCanceledException"/> —
    /// deliberately unlike the ICE transaction registry, which absorbs cancellation and returns a plain
    /// failure. A control transaction's caller must be able to distinguish "cancelled" from "the server
    /// rejected/ignored the request", so the exception is surfaced rather than folded into a result.
    /// </remarks>
    /// <param name="request">The request message (its transaction id keys the correlation).</param>
    /// <param name="requestBytes">The already-encoded request datagram to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The validated success response.</returns>
    /// <exception cref="TurnChallengeException">The response is a 401/438 authentication challenge.</exception>
    /// <exception cref="TurnException">
    /// The send failed, or the response is an error/mismatch, or no response arrived after all attempts.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<StunMessage> RoundTripAsync(StunMessage request, byte[] requestBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestBytes);

        var key = Convert.ToHexString(request.TransactionId);
        var tcs = new TaskCompletionSource<StunMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, tcs))
            throw new TurnException($"A TURN transaction with id {key} is already in flight.");

        try
        {
            var rto = _initialRto;
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    await _send(requestBytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Match TurnClientTransport's contract: a transport send failure surfaces as a
                    // TurnException so callers face one exception family, not the injected delegate's
                    // unpredictable type (e.g. SocketException).
                    throw new TurnException($"TURN relay control send failed: {ex.Message}", ex);
                }

                try
                {
                    var response = await tcs.Task.WaitAsync(rto, ct).ConfigureAwait(false);
                    return TurnResponseValidator.Validate(response, request.MessageMethod);
                }
                catch (TimeoutException)
                {
                    _logger.LogTrace("TURN transaction {TxId} attempt {Attempt} timed out after {Rto} ms.",
                        key, attempt, rto.TotalMilliseconds);
                    rto = TimeSpan.FromTicks(Math.Min(rto.Ticks * 2, _maxRto.Ticks));
                }
            }

            throw new TurnException($"TURN relay server did not respond after {_maxAttempts} attempts.");
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }
}
