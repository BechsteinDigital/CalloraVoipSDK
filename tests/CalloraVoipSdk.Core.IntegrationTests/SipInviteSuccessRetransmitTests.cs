using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Timing;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions.Server;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// 2xx retransmission for INVITE server transactions (RFC 3261 §13.3.1.4), driven from a fake clock (#336).
/// </summary>
/// <remarks>
/// A 2xx is the one response the transaction layer does not own: it leaves the transaction and becomes the
/// UAS core's responsibility, retransmitted until the ACK arrives. Skip it and a lost 200 OK strands a call
/// that both sides believe is up — the caller hears nothing and neither end tears down.
/// </remarks>
public sealed class SipInviteSuccessRetransmitTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("192.0.2.50"), 5060);

    [Fact]
    public async Task A_2xx_over_udp_is_retransmitted_until_the_ack_arrives()
    {
        var (engine, transport, clock) = Engine();
        var invite = Invite();
        engine.RegisterInboundRequest(new SipInboundRequestContext(Peer, SipTransportProtocol.Udp, null), invite);

        await Respond(engine, invite, 200);
        Assert.Equal(1, SentCount(transport, 200));

        // RFC 3261 §13.3.1.4: intervals start at T1 and double, capped at T2. The retransmit send is
        // deliberately fire-and-forget in production, so the count is awaited rather than read straight after.
        clock.Advance(TimeSpan.FromMilliseconds(500));   // T1
        Assert.True(await WaitForCount(transport, 200, 2), "The 2xx was not retransmitted when its timer fired.");
        clock.Advance(TimeSpan.FromSeconds(1));          // T1 doubled
        Assert.True(await WaitForCount(transport, 200, 3), "The 2xx stopped retransmitting before the ACK.");

        // The ACK ends it — that is the whole termination condition for this timer.
        engine.RegisterInboundRequest(
            new SipInboundRequestContext(Peer, SipTransportProtocol.Udp, null), Ack(invite));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);   // give a wrongly-armed retransmit the chance to show up

        Assert.Equal(3, SentCount(transport, 200));
    }

    [Fact]
    public async Task A_2xx_over_tcp_is_not_retransmitted()
    {
        // The transport already guarantees delivery; retransmitting would duplicate the response on the wire.
        var (engine, transport, clock) = Engine();
        var invite = Invite();
        engine.RegisterInboundRequest(new SipInboundRequestContext(Peer, SipTransportProtocol.Tcp, null), invite);

        await Respond(engine, invite, 200, SipTransportProtocol.Tcp);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Equal(1, SentCount(transport, 200));
    }

    [Fact]
    public async Task A_provisional_response_arms_nothing()
    {
        // Only a final response starts a transaction timer; a 180 may repeat for other reasons but not this one.
        var (engine, transport, clock) = Engine();
        var invite = Invite();
        engine.RegisterInboundRequest(new SipInboundRequestContext(Peer, SipTransportProtocol.Udp, null), invite);

        await Respond(engine, invite, 180);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Equal(1, SentCount(transport, 180));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static (SipServerTransactionEngine Engine, CapturingSipTransportRuntime Transport, FakeClock Clock) Engine()
    {
        var transport = new CapturingSipTransportRuntime();
        var clock = new FakeClock();
        return (new SipServerTransactionEngine(transport, NullLogger.Instance, timerScheduler: clock), transport, clock);
    }

    private static Task Respond(
        SipServerTransactionEngine engine, SipRequest invite, int status,
        SipTransportProtocol transport = SipTransportProtocol.Udp) =>
        engine.SendResponseAsync(
            invite, Peer, transport, status, status == 200 ? "OK" : "Ringing",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Via"] = invite.Headers["Via"],
                ["From"] = invite.Headers["From"],
                ["To"] = invite.Headers["To"] + ";tag=to-1",
                ["Call-ID"] = invite.Headers["Call-ID"],
                ["CSeq"] = invite.Headers["CSeq"],
                ["Content-Length"] = "0",
            },
            body: null);

    private static async Task<bool> WaitForCount(CapturingSipTransportRuntime transport, int status, int expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (SentCount(transport, status) >= expected)
                return true;
            await Task.Delay(20);
        }

        return false;
    }

    private static int SentCount(CapturingSipTransportRuntime transport, int status) =>
        transport.SnapshotResponses().Count(r => r.StatusCode == status);

    private static SipRequest Invite() => new(
        "INVITE", "sip:bob@example.test",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = "SIP/2.0/UDP 192.0.2.50:5060;branch=z9hG4bK-retransmit",
            ["Max-Forwards"] = "70",
            ["From"] = "<sip:alice@example.test>;tag=from-1",
            ["To"] = "<sip:bob@example.test>",
            ["Call-ID"] = "retransmit@example.test",
            ["CSeq"] = "1 INVITE",
            ["Content-Length"] = "0",
        },
        body: string.Empty);

    private static SipRequest Ack(SipRequest invite) => new(
        "ACK", invite.RequestUri,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = invite.Headers["Via"],
            ["Max-Forwards"] = "70",
            ["From"] = invite.Headers["From"],
            ["To"] = invite.Headers["To"] + ";tag=to-1",
            ["Call-ID"] = invite.Headers["Call-ID"],
            ["CSeq"] = "1 ACK",
            ["Content-Length"] = "0",
        },
        body: string.Empty);

    /// <summary>
    /// A scheduler with a virtual clock: callbacks run only once time is advanced past their delay, in due
    /// order. Firing everything armed regardless of delay would run the 5-minute lifetime reap before the
    /// 500 ms retransmit and tear the transaction down first — which is exactly what it did.
    /// </summary>
    private sealed class FakeClock : IScheduledActionScheduler
    {
        private readonly List<(TimeSpan DueAt, Entry Entry)> _pending = [];
        private TimeSpan _now = TimeSpan.Zero;

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new Entry(callback);
            lock (_pending) _pending.Add((_now + delay, entry));
            return entry;
        }

        /// <summary>Advances the clock and runs whatever came due, earliest first.</summary>
        public void Advance(TimeSpan by)
        {
            _now += by;
            while (true)
            {
                (TimeSpan DueAt, Entry Entry) next;
                lock (_pending)
                {
                    var index = -1;
                    for (var i = 0; i < _pending.Count; i++)
                    {
                        if (_pending[i].DueAt > _now)
                            continue;
                        if (index < 0 || _pending[i].DueAt < _pending[index].DueAt)
                            index = i;
                    }

                    if (index < 0)
                        return;

                    next = _pending[index];
                    _pending.RemoveAt(index);
                }

                if (!next.Entry.Cancelled)
                    next.Entry.Callback();
            }
        }

        public void Dispose() { }

        private sealed class Entry(Action callback) : IDisposable
        {
            public Action Callback { get; } = callback;
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }
    }
}
