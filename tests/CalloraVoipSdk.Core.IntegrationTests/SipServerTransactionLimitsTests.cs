using System.Collections;
using System.Net;
using System.Reflection;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// [SIP] #158 P1-7: the server-transaction table must be bounded. An INVITE answered only with a 100 Trying
/// never arms the RFC cleanup timers (those fire only on a final response), so without a safety net such a
/// transaction lingers forever; and a flood of distinct requests must not grow the table without limit. These
/// tests pin the absolute-expiry safety net and the hard capacity cap.
/// </summary>
public sealed class SipServerTransactionLimitsTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 5060);

    private static SipInboundRequestContext Context() => new(Remote, SipTransportProtocol.Udp, null);

    private static SipRequest Invite(int n)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = $"SIP/2.0/UDP 203.0.113.9:5060;branch=z9hG4bK-tx-{n}",
            ["Max-Forwards"] = "70",
            ["From"] = $"<sip:alice@example.test>;tag=from-{n}",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = $"tx-{n}@example.test",
            ["CSeq"] = "1 INVITE",
            ["Content-Length"] = "0",
        };
        return new SipRequest("INVITE", "sip:bob@example.test", headers, body: string.Empty);
    }

    private static int TransactionCount(SipServerTransactionEngine engine)
    {
        var field = typeof(SipServerTransactionEngine)
            .GetField("_transactions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((IDictionary)field.GetValue(engine)!).Count;
    }

    [Fact]
    public async Task Transaction_with_only_a_provisional_is_reaped_by_the_absolute_expiry_safety_net()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var engine = new SipServerTransactionEngine(
            transport,
            NullLogger.Instance,
            absoluteTransactionLifetime: TimeSpan.FromMilliseconds(150));

        var registration = engine.RegisterInboundRequest(Context(), Invite(1));
        Assert.True(registration.ShouldProcess);
        Assert.Equal(1, TransactionCount(engine));

        // No final response is ever sent (100-Trying-only path). The RFC cleanup timers are never armed; only
        // the absolute-expiry safety net removes it.
        for (var i = 0; i < 100 && TransactionCount(engine) != 0; i++)
            await Task.Delay(20);

        Assert.Equal(0, TransactionCount(engine));
    }

    [Fact]
    public void New_transactions_beyond_the_hard_cap_are_refused_but_existing_keys_still_match()
    {
        using var transport = new CapturingSipTransportRuntime();
        using var engine = new SipServerTransactionEngine(
            transport,
            NullLogger.Instance,
            maxServerTransactions: 2);

        Assert.True(engine.RegisterInboundRequest(Context(), Invite(1)).ShouldProcess);
        Assert.True(engine.RegisterInboundRequest(Context(), Invite(2)).ShouldProcess);
        Assert.Equal(2, TransactionCount(engine));

        // A third distinct transaction exceeds the cap: refused and not tracked.
        var third = engine.RegisterInboundRequest(Context(), Invite(3));
        Assert.True(third.IsOverCapacity);
        Assert.False(third.ShouldProcess);
        Assert.Equal(2, TransactionCount(engine));

        // A retransmission of an already-tracked transaction is never refused by the cap.
        var retransmit = engine.RegisterInboundRequest(Context(), Invite(1));
        Assert.True(retransmit.IsRetransmission);
        Assert.False(retransmit.IsOverCapacity);
        Assert.Equal(2, TransactionCount(engine));
    }
}
