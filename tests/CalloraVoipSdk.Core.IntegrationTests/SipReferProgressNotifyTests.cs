using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// CF-045 (RFC 3515 §2.4.4 / RFC 6665): the implicit subscription created by an accepted REFER first reports the
/// referred action as in progress (Subscription-State: active, sipfrag 100 Trying) and then terminated — not a
/// single terminated NOTIFY. A declined REFER (603) yields one terminated NOTIFY.
/// </summary>
public sealed class SipReferProgressNotifyTests
{
    private const string CallId = "call-ack-test";  // AckTestSipCallSessionContext default
    private const string LocalTag = "local-tag";    // AckTestSipCallSessionContext default
    private const string RemoteTag = "remote-tag";

    private static (SipCallSessionInboundService Service, CapturingSipServerTransactionEngine Engine, CapturingSipTransportRuntime Transport)
        Build(bool transferAccepted)
    {
        var engine = new CapturingSipServerTransactionEngine();
        var transport = new CapturingSipTransportRuntime();
        var context = new AckTestSipCallSessionContext(transport)
        {
            ServerTransactions = engine,
            RemoteTag = RemoteTag,
            TransferAccepted = transferAccepted,
        };
        return (new SipCallSessionInboundService(context, new SipCallSessionHeaderService(context)), engine, transport);
    }

    private static SipRequest Refer()
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
        return new SipRequest("REFER", "sip:us@example.test", headers, string.Empty);
    }

    [Fact]
    public async Task An_accepted_refer_notifies_active_then_terminated()
    {
        var (service, engine, transport) = Build(transferAccepted: true);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 202);

        var notifies = transport.SnapshotRequests().Where(r => r.Method == "NOTIFY").ToList();
        Assert.Equal(2, notifies.Count);
        Assert.StartsWith("active", notifies[0].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 100 Trying", notifies[0].Body);
        Assert.StartsWith("terminated", notifies[1].Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 200 OK", notifies[1].Body);
        Assert.All(notifies, n => Assert.Equal("refer", n.Headers["Event"]));
    }

    [Fact]
    public async Task A_declined_refer_sends_a_single_terminated_notify()
    {
        var (service, engine, transport) = Build(transferAccepted: false);

        await service.HandleInboundRequestAsync(new IPEndPoint(IPAddress.Loopback, 5060), Refer(), default);

        Assert.Contains(engine.Responses, r => r.StatusCode == 603);

        var notify = Assert.Single(transport.SnapshotRequests().Where(r => r.Method == "NOTIFY"));
        Assert.StartsWith("terminated", notify.Headers["Subscription-State"]);
        Assert.Equal("SIP/2.0 603 Decline", notify.Body);
    }
}
