using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Bounded, thread-safe transport-cc arrival recorder: preserves arrival order across a drain,
/// empties on drain, overwrites the oldest arrival on overflow while counting the drop, validates
/// capacity, and records correctly under concurrent producers.
/// </summary>
public sealed class TransportCcArrivalRecorderTests
{
    [Fact]
    public void Drain_returns_arrivals_in_record_order_then_empties()
    {
        var recorder = new TransportCcArrivalRecorder(8);
        recorder.Record(10, 100);
        recorder.Record(11, 200);
        recorder.Record(12, 300);

        var drained = recorder.Drain();
        Assert.Equal([(ushort)10, 11, 12], drained.Select(a => a.SequenceNumber).ToArray());
        Assert.Equal([100L, 200, 300], drained.Select(a => a.ArrivalTimestamp).ToArray());
        Assert.Equal(0, recorder.DroppedCount);

        Assert.Empty(recorder.Drain());
    }

    [Fact]
    public void Overflow_overwrites_the_oldest_and_counts_the_drop()
    {
        var recorder = new TransportCcArrivalRecorder(3);
        for (ushort seq = 1; seq <= 5; seq++)
            recorder.Record(seq, seq * 10L);

        var drained = recorder.Drain();
        // Capacity 3: the two oldest (seq 1, 2) were overwritten; 3, 4, 5 remain in order.
        Assert.Equal([(ushort)3, 4, 5], drained.Select(a => a.SequenceNumber).ToArray());
        Assert.Equal(2, recorder.DroppedCount);
    }

    [Fact]
    public void Ring_buffer_wraps_and_keeps_recording_after_a_drain()
    {
        var recorder = new TransportCcArrivalRecorder(4);
        recorder.Record(1, 10);
        recorder.Record(2, 20);
        recorder.Drain(); // start index now advanced past the physical origin

        recorder.Record(3, 30);
        recorder.Record(4, 40);
        recorder.Record(5, 50);

        Assert.Equal([(ushort)3, 4, 5], recorder.Drain().Select(a => a.SequenceNumber).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_capacity_is_rejected(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TransportCcArrivalRecorder(capacity));

    [Fact]
    public void Concurrent_producers_record_without_loss_or_corruption()
    {
        const int producers = 4;
        const int perProducer = 1000;
        var recorder = new TransportCcArrivalRecorder(producers * perProducer);

        var tasks = new Task[producers];
        for (var p = 0; p < producers; p++)
        {
            var basis = p;
            tasks[p] = Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++)
                    recorder.Record((ushort)(basis * perProducer + i), i);
            });
        }

        Task.WaitAll(tasks);

        Assert.Equal(producers * perProducer, recorder.Drain().Count);
        Assert.Equal(0, recorder.DroppedCount);
    }
}
