using CalloraVoipSdk.Core.Infrastructure.Rtp.CongestionControl;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Transport-cc delay-gradient correlator: pairs reconstructed arrival times with local send times
/// to yield <c>(Δarrival − Δsend)</c> per consecutive correlated packet — zero at constant delay,
/// positive as delay grows, negative as it shrinks — while skipping losses and evicted send times.
/// </summary>
public sealed class TransportCcFeedbackCorrelatorTests
{
    private const long Frequency = 1_000_000; // send ticks are microseconds

    private static TransportCcFeedbackResult Received(ushort seq, long arrivalMicros) =>
        new() { SequenceNumber = seq, Received = true, ArrivalMicros = arrivalMicros };

    private static TransportCcFeedbackResult Lost(ushort seq) =>
        new() { SequenceNumber = seq, Received = false };

    [Fact]
    public void Constant_delay_yields_zero_gradients()
    {
        var history = new TransportCcSendHistory(64);
        history.Record(1, 0);
        history.Record(2, 100);
        history.Record(3, 200);
        var results = new[] { Received(1, 1_000), Received(2, 1_100), Received(3, 1_200) };

        var samples = TransportCcFeedbackCorrelator.Correlate(results, history, Frequency);

        Assert.Equal([(ushort)2, 3], samples.Select(s => s.SequenceNumber).ToArray());
        Assert.All(samples, s => Assert.Equal(0, s.DelayGradientMicros));
    }

    [Fact]
    public void Growing_delay_yields_a_positive_gradient()
    {
        var history = new TransportCcSendHistory(64);
        history.Record(1, 0);
        history.Record(2, 100);
        var results = new[] { Received(1, 1_000), Received(2, 1_250) };

        var sample = Assert.Single(TransportCcFeedbackCorrelator.Correlate(results, history, Frequency));
        Assert.Equal(2, sample.SequenceNumber);
        Assert.Equal(150, sample.DelayGradientMicros); // (1250-1000) - (100-0)
    }

    [Fact]
    public void Shrinking_delay_yields_a_negative_gradient()
    {
        var history = new TransportCcSendHistory(64);
        history.Record(1, 0);
        history.Record(2, 100);
        var results = new[] { Received(1, 1_000), Received(2, 1_050) };

        var sample = Assert.Single(TransportCcFeedbackCorrelator.Correlate(results, history, Frequency));
        Assert.Equal(-50, sample.DelayGradientMicros); // (1050-1000) - (100-0)
    }

    [Fact]
    public void A_lost_packet_breaks_the_chain_and_the_next_gradient_spans_the_gap()
    {
        var history = new TransportCcSendHistory(64);
        history.Record(1, 0);
        history.Record(3, 200);
        var results = new[] { Received(1, 1_000), Lost(2), Received(3, 1_200) };

        var sample = Assert.Single(TransportCcFeedbackCorrelator.Correlate(results, history, Frequency));
        Assert.Equal(3, sample.SequenceNumber);
        Assert.Equal(0, sample.DelayGradientMicros); // relative to seq 1: (1200-1000) - (200-0)
    }

    [Fact]
    public void A_received_packet_with_an_evicted_send_time_is_skipped()
    {
        var history = new TransportCcSendHistory(64);
        history.Record(1, 0);
        history.Record(3, 200); // seq 2 never recorded (evicted)
        var results = new[] { Received(1, 1_000), Received(2, 1_100), Received(3, 1_200) };

        var sample = Assert.Single(TransportCcFeedbackCorrelator.Correlate(results, history, Frequency));
        Assert.Equal(3, sample.SequenceNumber);
        Assert.Equal(0, sample.DelayGradientMicros); // seq 2 skipped → seq 3 relative to seq 1
    }

    [Fact]
    public void Fewer_than_two_correlated_packets_yield_no_samples()
    {
        var history = new TransportCcSendHistory(64);
        history.Record(1, 0);

        Assert.Empty(TransportCcFeedbackCorrelator.Correlate([Received(1, 1_000)], history, Frequency));
    }

    [Fact]
    public void Non_positive_frequency_is_rejected()
    {
        var history = new TransportCcSendHistory(64);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransportCcFeedbackCorrelator.Correlate([Received(1, 1_000)], history, 0));
    }
}
