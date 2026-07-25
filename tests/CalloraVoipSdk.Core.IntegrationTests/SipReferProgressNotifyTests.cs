using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-045 (RFC 3515 §2.4 / RFC 6665): an accepted REFER creates an implicit subscription. The referee sends an
/// immediate <c>active</c>/100 Trying NOTIFY and then relays the progress and final outcome the application reports
/// through the <see cref="IReferSubscription"/> handle (e.g. 180 Ringing, then 200 OK or a failure), rather than an
/// optimistic single terminated NOTIFY. A declined REFER (603) yields one terminated NOTIFY, and RFC 4488
/// <c>Refer-Sub: false</c> suppresses the subscription entirely.
/// </summary>
public sealed class SipReferProgressNotifyTests
{
    private const string CallId = "call-ack-test";  // AckTestSipCallSessionContext default
    private const string LocalTag = "local-tag";    // AckTestSipCallSessionContext default
    private const string RemoteTag = "remote-tag";

    private static (SipCallSessionInboundService Service, CapturingSipServerTransactionEngine Engine, CapturingSipTransportRuntime Transport)
        Build(bool transferAccepted, Action<IReferSubscription>? report = null)
    {
        var engine = new CapturingSipServerTransactionEngine();
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport)
        {
            ServerTransactions = engine,
            RemoteTag = RemoteTag,
            TransferAccepted = transferAccepted,
            OnTransferSubscription = report,
        };
        return (new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context)), engine, transport);
    }

    private static SipRequest Refer(string? referSub = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.1:5060;branch=z9hG4bK-refer",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:them@example.test>;tag={RemoteTag}",
            ["To"] = $"<sip:us@example.test>;tag={LocalTag}",
            ["Call-ID"] = CallId,
            ["CSeq"] = "2 REFER",
            ["Refer-To"] = "<sip:transfer-target@example.test>",
        };
        if (referSub is not null)
            headers["Refer-Sub"] = referSub;
        return new SipRequest("REFER", "sip:us@example.test", headers, string.Empty);
    }

    private static List<CapturedSipRequest> Notifies(CapturingSipTransportRuntime transport) =>
        transport.SnapshotRequests().Where(r => r.Method == "NOTIFY").ToList();

    [Fact]
    public async Task An_accepted_refer_notifies_active_then_terminated()
    {
        // Default consumer reports success → active/100 Trying then terminated/200 OK.
        var (service, engine, transport) = Build(transferAccepted: true);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 202);

        var notifies = Notifies(transport);
        Assert.Equal(2, notifies.Count);
        Assert.StartsWith("active", notifies[0].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 100 Trying", notifies[0].Body);
        Assert.StartsWith("terminated", notifies[1].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 200 OK", notifies[1].Body);
        Assert.All(notifies, n => Assert.Equal("refer", n.Headers["Event"]));
    }

    [Fact]
    public async Task An_accepted_refer_relays_reported_progress_before_success()
    {
        var (service, engine, transport) = Build(
            transferAccepted: true,
            report: s => { s.ReportRinging(); s.ReportSuccess(); });

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 202);

        var notifies = Notifies(transport);
        Assert.Equal(3, notifies.Count);
        Assert.StartsWith("active", notifies[0].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 100 Trying", notifies[0].Body);
        Assert.StartsWith("active", notifies[1].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 180 Ringing", notifies[1].Body);
        Assert.StartsWith("terminated", notifies[2].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 200 OK", notifies[2].Body);
    }

    [Fact]
    public async Task An_accepted_refer_relays_a_reported_referred_call_failure()
    {
        var (service, _, transport) = Build(transferAccepted: true, report: s => s.ReportFailure(486));

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        var notifies = Notifies(transport);
        Assert.Equal(2, notifies.Count);
        Assert.Equal("SIP/2.0 100 Trying", notifies[0].Body);
        Assert.StartsWith("terminated", notifies[1].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 486 Busy Here", notifies[1].Body);
    }

    [Fact]
    public async Task An_accepted_refer_without_a_report_sends_only_the_immediate_active_notify()
    {
        // Consumer accepts but never reports → the subscription lapses at its expiry; no optimistic terminated.
        var (service, _, transport) = Build(transferAccepted: true, report: _ => { });

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        var notify = Assert.Single(Notifies(transport));
        Assert.StartsWith("active", notify.Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 100 Trying", notify.Body);
    }

    [Fact]
    public async Task A_declined_refer_sends_a_single_terminated_notify()
    {
        var (service, engine, transport) = Build(transferAccepted: false);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 603);

        var notify = Assert.Single(Notifies(transport));
        Assert.StartsWith("terminated", notify.Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 603 Decline", notify.Body);
    }

    [Fact]
    public async Task A_refer_sub_false_suppresses_all_notifies_even_when_the_consumer_reports()
    {
        // RFC 4488: Refer-Sub: false means no implicit subscription — consumer reports must produce no NOTIFY.
        var (service, engine, transport) = Build(transferAccepted: true, report: s => s.ReportSuccess());

        await service.HandleInboundRequestAsync(
            new IPEndPoint(IPAddress.Loopback, 5060), Refer(referSub: "false"), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 202);
        Assert.Empty(Notifies(transport));
    }
}
