using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Services the DTLS-SRTP association after key export (#190). Once the four SRTP/SRTCP contexts are
/// derived, media flows directly over SRTP (RFC 5764 §4.2) and nobody reads the DTLS channel again —
/// so a peer <c>close_notify</c> would go unnoticed and media would keep running under a keying
/// channel the peer considers closed. This loop keeps reading the control channel on a single
/// consumer: it discards (and counts) any stray DTLS application_data in pure-SRTP mode, and on a
/// peer close/alert it notifies the session owner so media ceases. Teardown is cancellation-driven
/// and leaves no worker behind. Renegotiation needs no handling: BouncyCastle discards post-handshake
/// ClientHellos automatically (no rekey), which is exactly the passive rejection we want — live
/// rekeying/MKI stays #116.
/// </summary>
internal sealed class DtlsAssociationReceiver : IAsyncDisposable
{
    // Bounded receive wait so cancellation is observed promptly (the channel never blocks longer).
    private const int ReceiveWaitMillis = 200;

    private readonly IDtlsControlChannel _channel;
    private readonly int _receiveLimit;
    private readonly Action _onPeerClosed;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private Task? _loop;
    private long _discardedApplicationDataRecords;
    private int _disposed;

    public DtlsAssociationReceiver(
        IDtlsControlChannel channel,
        int receiveLimit,
        Action onPeerClosed,
        ILogger logger,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentOutOfRangeException.ThrowIfLessThan(receiveLimit, 1);
        ArgumentNullException.ThrowIfNull(onPeerClosed);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _receiveLimit = receiveLimit;
        _onPeerClosed = onPeerClosed;
        _logger = logger;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
    }

    /// <summary>Number of stray DTLS application_data records discarded on the SRTP keying channel.</summary>
    public long DiscardedApplicationDataRecords => Interlocked.Read(ref _discardedApplicationDataRecords);

    /// <summary>Starts the control-receive loop on a dedicated long-running thread.</summary>
    public void Start() => _loop = Task.Factory.StartNew(
        Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run()
    {
        var token = _cts.Token;
        var buffer = new byte[_receiveLimit];
        var peerClosed = false;

        while (!token.IsCancellationRequested)
        {
            DtlsControlReceiveResult result;
            try
            {
                result = _channel.Receive(buffer, ReceiveWaitMillis);
            }
            catch (Exception ex)
            {
                // An unexpected control-channel fault after the handshake: fail closed. If this is our
                // own teardown (token cancelled), it is not a peer close.
                _logger.LogWarning(ex, "DTLS control-receive faulted; treating the association as closed.");
                peerClosed = !token.IsCancellationRequested;
                break;
            }

            if (result.Signal == DtlsControlSignal.Timeout)
                continue;

            if (result.Signal == DtlsControlSignal.ApplicationData)
            {
                // Pure-SRTP mode: RTP/RTCP stays SRTP/SRTCP (RFC 5764), so DTLS application_data on the
                // keying channel is unexpected — discard and count it, do not close the association.
                Interlocked.Increment(ref _discardedApplicationDataRecords);
                _logger.LogWarning(
                    "Discarded {Length}-byte DTLS application_data on the SRTP keying channel (RFC 5764: media stays SRTP/SRTCP).",
                    result.Length);
                continue;
            }

            // Closed: a peer close_notify/alert closed the association, unless it is our own teardown.
            peerClosed = !token.IsCancellationRequested;
            break;
        }

        if (peerClosed)
        {
            _logger.LogInformation("DTLS peer closed the association; the session owner ceases media for this leg.");
            _onPeerClosed();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Run observes its own faults; anything escaping here is a teardown race.
                _logger.LogDebug(ex, "DTLS association receive loop faulted during disposal.");
            }
        }

        _cts.Dispose();
    }
}
