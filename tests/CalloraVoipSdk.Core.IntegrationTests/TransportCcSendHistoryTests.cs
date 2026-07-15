using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-cc send-timestamp history: recalls a sent sequence number's timestamp, reports unknown
/// sequences as absent, evicts an old sequence when a later one reuses its ring slot, validates
/// capacity, and records correctly under concurrent producers/consumers.
/// </summary>
public sealed class TransportCcSendHistoryTests
{
    [Fact]
    public void Recalls_a_recorded_send_timestamp()
    {
        var history = new TransportCcSendHistory(16);
        history.Record(100, 5_000);

        Assert.True(history.TryGetSendTimestamp(100, out var ts));
        Assert.Equal(5_000, ts);
    }

    [Fact]
    public void Reports_an_unrecorded_sequence_as_absent()
    {
        var history = new TransportCcSendHistory(16);
        history.Record(100, 5_000);

        Assert.False(history.TryGetSendTimestamp(101, out var ts));
        Assert.Equal(0, ts);
    }

    [Fact]
    public void Evicts_an_old_sequence_when_a_later_one_reuses_the_slot()
    {
        var history = new TransportCcSendHistory(8);
        history.Record(1, 1_000);
        history.Record(9, 2_000); // 9 % 8 == 1 % 8 → same slot, overwrites sequence 1

        Assert.False(history.TryGetSendTimestamp(1, out _));
        Assert.True(history.TryGetSendTimestamp(9, out var ts));
        Assert.Equal(2_000, ts);
    }

    [Fact]
    public void Distinguishes_sequences_across_the_16bit_wrap()
    {
        var history = new TransportCcSendHistory(1024);
        history.Record(65535, 9_000);
        history.Record(1, 9_100);

        Assert.True(history.TryGetSendTimestamp(65535, out var high));
        Assert.True(history.TryGetSendTimestamp(1, out var low));
        Assert.Equal(9_000, high);
        Assert.Equal(9_100, low);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_capacity_is_rejected(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TransportCcSendHistory(capacity));

    [Fact]
    public void Concurrent_record_and_lookup_do_not_corrupt_entries()
    {
        const int perProducer = 2000;
        var history = new TransportCcSendHistory(4096);

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
                history.Record((ushort)i, i);
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
                _ = history.TryGetSendTimestamp((ushort)i, out _); // must not throw or tear
        });

        Task.WaitAll(writer, reader);

        // Every distinct sequence (all < capacity, no slot reuse) is recallable with its timestamp.
        for (var i = 0; i < perProducer; i++)
        {
            Assert.True(history.TryGetSendTimestamp((ushort)i, out var ts));
            Assert.Equal(i, ts);
        }
    }
}
