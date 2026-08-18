using CalloraVoipSdk.Core.Infrastructure.Turn.Server;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// The RFC 8016 mobility-ticket store: an allocation survives the client moving to a new address by
/// presenting an opaque ticket that resolves back to the same allocation (#336).
/// </summary>
/// <remarks>
/// Mobility is the difference between a call dropping and not dropping when a phone hands off WLAN to
/// mobile. The ticket must resolve only while valid, only to its own allocation, and must be revocable —
/// a ticket that outlives its allocation would hand a new client someone else's relay.
/// </remarks>
public sealed class TurnMobilityTicketStoreTests
{
    private static readonly DateTimeOffset FarFuture = DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public void A_freshly_issued_ticket_resolves_to_its_allocation()
    {
        var store = new TurnMobilityTicketStore();
        var ticket = store.Issue("alloc-A", FarFuture);

        Assert.True(store.TryResolve(ticket, out var allocationKey));
        Assert.Equal("alloc-A", allocationKey);
    }

    [Fact]
    public void An_expired_ticket_does_not_resolve()
    {
        // The client presenting a stale ticket must re-allocate, not silently reattach.
        var store = new TurnMobilityTicketStore();
        var ticket = store.Issue("alloc-A", DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.False(store.TryResolve(ticket, out _));
    }

    [Fact]
    public void An_unknown_ticket_does_not_resolve()
    {
        var store = new TurnMobilityTicketStore();

        Assert.False(store.TryResolve(new byte[24], out _));
    }

    [Fact]
    public void Two_allocations_get_distinct_tickets_that_resolve_to_their_own()
    {
        var store = new TurnMobilityTicketStore();
        var a = store.Issue("alloc-A", FarFuture);
        var b = store.Issue("alloc-B", FarFuture);

        Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b));
        store.TryResolve(a, out var resolvedA);
        store.TryResolve(b, out var resolvedB);
        Assert.Equal("alloc-A", resolvedA);
        Assert.Equal("alloc-B", resolvedB);
    }

    [Fact]
    public void A_removed_ticket_stops_resolving()
    {
        var store = new TurnMobilityTicketStore();
        var ticket = store.Issue("alloc-A", FarFuture);

        store.Remove(ticket);

        Assert.False(store.TryResolve(ticket, out _));
    }

    [Fact]
    public void Removing_an_allocation_invalidates_every_ticket_it_issued()
    {
        // When an allocation is torn down, every mobility ticket for it must die with it — otherwise a later
        // client could present one and be handed a relay that is gone or reassigned.
        var store = new TurnMobilityTicketStore();
        var first = store.Issue("alloc-A", FarFuture);
        var second = store.Issue("alloc-A", FarFuture);
        var other = store.Issue("alloc-B", FarFuture);

        store.RemoveByAllocation("alloc-A");

        Assert.False(store.TryResolve(first, out _));
        Assert.False(store.TryResolve(second, out _));
        Assert.True(store.TryResolve(other, out _));   // an unrelated allocation is untouched
    }
}
