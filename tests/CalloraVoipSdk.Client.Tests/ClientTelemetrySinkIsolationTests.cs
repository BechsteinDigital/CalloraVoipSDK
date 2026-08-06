using System.IO;
using CalloraVoipSdk.Core.Application.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Client.Tests;

/// <summary>
/// [Client] #166 P1-3: Core publishes telemetry from inside REGISTER/INVITE/cleanup steps, so a faulty
/// monitoring sink or a throwing app subscriber must never steer that signalling path. ClientTelemetrySink
/// previously called the inner sink and the multicast event synchronously and unfiltered, so either could
/// propagate into signalling and a throwing subscriber blocked later ones. This test pins the isolation.
/// </summary>
public sealed class ClientTelemetrySinkIsolationTests
{
    private sealed class ThrowingInnerSink : ISipTelemetrySink
    {
        public void PublishEvent(SipEventRecord record) => throw new IOException("sink-boom");
        public void PublishMetric(SipMetricRecord record) => throw new IOException("sink-boom");
        public void PublishCdr(SipCdrRecord record) => throw new IOException("sink-boom");
    }

    [Fact]
    public void A_throwing_inner_sink_and_subscriber_do_not_propagate_and_do_not_block_other_subscribers()
    {
        var sink = new ClientTelemetrySink(new ThrowingInnerSink(), NullLogger.Instance);

        var goodFired = 0;
        sink.EventPublished += _ => throw new ApplicationException("subscriber-boom");
        sink.EventPublished += _ => goodFired++;

        var exception = Record.Exception(() => sink.PublishEvent(new SipEventRecord { EventType = "test" }));

        Assert.Null(exception);      // neither the inner sink nor a subscriber propagated into the publish path
        Assert.Equal(1, goodFired);  // the good subscriber still ran despite the earlier throwing one
    }
}
