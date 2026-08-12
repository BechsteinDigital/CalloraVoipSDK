using CalloraVoipSdk.Core.Infrastructure.Dtls;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #157 P2-8: the inbound DTLS path admits a record — closed, size, queue space — <em>before</em> it
/// allocates the managed copy. The bounded queue always capped the memory the transport *retains*, but
/// the copy happened in the pipeline first, so an unauthenticated sender aimed at the media port could
/// drive continuous allocation for datagrams that were then dropped. These tests pin the admission
/// order at the transport, where it is directly observable.
/// </summary>
public sealed class DtlsIngressAdmissionTests
{
    private const int QueueCapacity = 64;
    private const int DatagramLimit = 1452;

    private static QueueDatagramTransport NewTransport() => new(_ => { });

    [Fact]
    public void A_record_larger_than_the_receive_limit_is_refused()
    {
        var transport = NewTransport();

        // The handshake engine reads at most GetReceiveLimit() bytes, so a larger record could never be
        // consumed — queueing it would only burn a copy and a queue slot.
        Assert.Equal(DatagramLimit, transport.GetReceiveLimit());
        Assert.False(transport.TryEnqueue(new byte[DatagramLimit + 1]));
        Assert.True(transport.TryEnqueue(new byte[DatagramLimit]));
    }

    [Fact]
    public void An_empty_record_is_refused()
    {
        Assert.False(NewTransport().TryEnqueue(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void A_full_queue_refuses_further_records_without_growing()
    {
        var transport = NewTransport();
        var record = new byte[13];

        for (var i = 0; i < QueueCapacity; i++)
            Assert.True(transport.TryEnqueue(record));

        // Past the cap every further datagram is refused. DTLS retransmits, so dropping the newest is
        // safe; what matters is that the refusal is reported rather than swallowed.
        for (var i = 0; i < 1000; i++)
            Assert.False(transport.TryEnqueue(record));
    }

    [Fact]
    public void A_closed_transport_refuses_records()
    {
        var transport = NewTransport();
        Assert.True(transport.TryEnqueue(new byte[13]));

        transport.Close();

        Assert.True(transport.IsClosed);
        Assert.False(transport.TryEnqueue(new byte[13]));
    }

    [Fact]
    public void An_admitted_record_is_copied_and_survives_the_caller_reusing_its_buffer()
    {
        // The counterpart to the refusals: admission still has to produce an independent copy, because
        // the span points into the media receive buffer, which is reused for the next datagram.
        var transport = NewTransport();
        var buffer = new byte[13];
        buffer[0] = 22;   // handshake content type
        buffer[12] = 0xAB;

        Assert.True(transport.TryEnqueue(buffer));
        buffer.AsSpan().Fill(0xFF);   // the receive loop overwrites the buffer with the next datagram

        var received = new byte[13];
        Assert.Equal(13, transport.Receive(received, 1000));
        Assert.Equal(22, received[0]);
        Assert.Equal(0xAB, received[12]);
    }
}
