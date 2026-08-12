using System.Collections.Concurrent;
using Org.BouncyCastle.Tls;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// BouncyCastle <see cref="DatagramTransport"/> backed by an in-memory inbound queue and
/// an outbound send callback. The media receive loop demultiplexes DTLS records off the
/// shared RTP socket (RFC 5764 §5.1.2) and feeds them in via <see cref="Enqueue"/>; the
/// handshake engine polls <see cref="Receive(Span{byte}, int)"/> on its own thread.
/// Also used in loopback pairs for handshake tests.
/// </summary>
internal sealed class QueueDatagramTransport : DatagramTransport
{
    // Conservative UDP payload budget below a 1500-byte Ethernet MTU (RFC 5764 handshakes
    // fragment at the DTLS layer, so an exact path-MTU probe is unnecessary here).
    private const int DefaultDatagramLimit = 1452;

    // Bounded so a flood of stray datagrams cannot grow memory; DTLS flights retransmit,
    // so dropping the newest datagram when full is safe.
    private const int InboundQueueCapacity = 64;

    private readonly BlockingCollection<byte[]> _inbound = new(InboundQueueCapacity);
    private readonly Action<byte[]> _send;

    /// <param name="send">
    /// Synchronous outbound datagram delivery (e.g. a UDP send on the media socket).
    /// Exceptions propagate into the handshake engine and abort the handshake.
    /// </param>
    public QueueDatagramTransport(Action<byte[]> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
    }

    /// <summary>
    /// Feeds one inbound DTLS datagram to the handshake engine. Drops the datagram when
    /// the queue is full or the transport is closed — DTLS retransmission recovers.
    /// Returns whether the datagram was accepted.
    /// </summary>
    public bool Enqueue(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        try
        {
            return _inbound.TryAdd(datagram);
        }
        catch (InvalidOperationException)
        {
            return false; // CompleteAdding raced with this producer — transport is closing.
        }
    }

    /// <summary>
    /// Feeds one inbound DTLS datagram straight from the media receive buffer, allocating the queued
    /// copy only once the datagram has been admitted (#157 P2-8). Returns whether it was accepted.
    /// </summary>
    /// <remarks>
    /// The closed and full checks run <em>before</em> the copy, so a flood against a full queue or a
    /// torn-down transport costs no allocation at all — the bounded queue capped retained memory, never
    /// the allocation rate. The capacity check is advisory: <see cref="BlockingCollection{T}.TryAdd(T)"/>
    /// below remains the real gate, and losing that race merely means one copy was made needlessly.
    /// Oversized records are refused here rather than queued: the handshake engine reads at most
    /// <see cref="GetReceiveLimit"/> bytes, so a larger record could never be consumed anyway.
    /// </remarks>
    public bool TryEnqueue(ReadOnlySpan<byte> datagram)
    {
        if (_inbound.IsAddingCompleted
            || datagram.Length == 0
            || datagram.Length > DefaultDatagramLimit
            || _inbound.Count >= InboundQueueCapacity)
        {
            return false;
        }

        return Enqueue(datagram.ToArray());
    }

    /// <summary>
    /// Whether the transport has stopped accepting inbound datagrams — either torn down locally via
    /// <see cref="Close"/>, or closed by the DTLS record layer after it processed a peer
    /// <c>close_notify</c> (BouncyCastle calls <see cref="Close"/> on the underlying transport when
    /// the association closes). The control-receive loop uses this to tell a peer close from a plain
    /// receive timeout, since both surface as <see cref="Receive(Span{byte}, int)"/> returning -1 (#190).
    /// </summary>
    public bool IsClosed => _inbound.IsAddingCompleted;

    /// <inheritdoc />
    public int GetReceiveLimit() => DefaultDatagramLimit;

    /// <inheritdoc />
    public int GetSendLimit() => DefaultDatagramLimit;

    /// <inheritdoc />
    public int Receive(byte[] buf, int off, int len, int waitMillis)
    {
        ArgumentNullException.ThrowIfNull(buf);
        return Receive(buf.AsSpan(off, len), waitMillis);
    }

    /// <inheritdoc />
    public int Receive(Span<byte> buffer, int waitMillis)
    {
        if (!_inbound.TryTake(out var datagram, waitMillis))
        {
            // Closed and drained means no further datagram can ever arrive — fail the
            // handshake immediately instead of spinning until the DTLS timeout.
            if (_inbound.IsAddingCompleted)
                throw new ObjectDisposedException(nameof(QueueDatagramTransport));

            return -1; // Timeout — lets the DTLS retransmission timer fire.
        }

        var count = Math.Min(buffer.Length, datagram.Length);
        datagram.AsSpan(0, count).CopyTo(buffer);
        return count;
    }

    /// <inheritdoc />
    public void Send(byte[] buf, int off, int len)
    {
        ArgumentNullException.ThrowIfNull(buf);
        Send(buf.AsSpan(off, len));
    }

    /// <inheritdoc />
    public void Send(ReadOnlySpan<byte> buffer) => _send(buffer.ToArray());

    /// <inheritdoc />
    public void Close()
    {
        if (!_inbound.IsAddingCompleted)
            _inbound.CompleteAdding();
    }
}
