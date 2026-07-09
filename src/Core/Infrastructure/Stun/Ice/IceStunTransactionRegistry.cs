using System.Collections.Concurrent;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Ice;

/// <summary>
/// Matches inbound STUN <em>responses</em> to the outbound checks we sent on the shared media
/// socket (RFC 8445 §7.2.5 / RFC 7675 consent checks). A caller registers a transaction before
/// sending its request and awaits the result; the media receive loop calls
/// <see cref="TryComplete"/> when a response with that transaction ID arrives. Registration is
/// synchronous so a caller can safely send immediately after calling
/// <see cref="AwaitResponseAsync"/> without racing a fast response.
/// </summary>
internal sealed class IceStunTransactionRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the transaction (synchronously, before the first await) and awaits its response.
    /// Returns <see langword="true"/> when a matching response arrives within
    /// <paramref name="timeout"/>, or <see langword="false"/> on timeout or cancellation.
    /// </summary>
    /// <param name="transactionId">The 12-byte STUN transaction ID of the request being sent.</param>
    /// <param name="timeout">How long to wait for the response.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> AwaitResponseAsync(byte[] transactionId, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transactionId);

        var key = Convert.ToHexString(transactionId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Completes the pending transaction matching <paramref name="transactionId"/>, if any.
    /// Returns <see langword="true"/> when a matching pending transaction was found.
    /// Safe to call from the media receive loop.
    /// </summary>
    /// <param name="transactionId">The transaction ID carried in the received STUN response.</param>
    public bool TryComplete(ReadOnlySpan<byte> transactionId)
    {
        var key = Convert.ToHexString(transactionId);
        return _pending.TryGetValue(key, out var tcs) && tcs.TrySetResult(true);
    }
}
