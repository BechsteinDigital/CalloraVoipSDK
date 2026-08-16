using CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #158 P2-12 — in-dialog SUBSCRIBE state was peer-controlled in both directions. Without a subscription
/// handler every well-formed event package is accepted, and each (package, id) pair becomes its own lease
/// holding a linked CTS and a background delay; the requested Expires was taken verbatim, so one request
/// could pin those resources for as long as it liked.
/// </summary>
public sealed class SipSubscriptionLeaseLimitTests
{
    private static SipSubscriptionLifecycleManager NewManager() =>
        new(NullLogger.Instance, (_, _, _) => Task.CompletedTask);

    private static SipSubscriptionIdentifier Id(string suffix) => new("presence", suffix);

    [Fact]
    public void An_excessive_expires_is_shortened()
    {
        // RFC 6665 §4.2.1 lets the notifier grant less than asked. A year would otherwise arm a timer for
        // a year.
        using var manager = NewManager();

        var update = manager.ActivateOrRefresh(Id("a"), 31_536_000);

        Assert.Equal(3600, update.EffectiveExpiresSeconds);
        Assert.Contains("expires=3600", update.SubscriptionStateHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reasonable_expires_is_granted_as_requested()
    {
        using var manager = NewManager();

        var update = manager.ActivateOrRefresh(Id("a"), 600);

        Assert.Equal(600, update.EffectiveExpiresSeconds);
    }

    [Fact]
    public void New_leases_are_capped()
    {
        using var manager = NewManager();
        for (var i = 0; i < 32; i++)
            manager.ActivateOrRefresh(Id($"id{i}"), 60);

        Assert.Throws<InvalidOperationException>(() => manager.ActivateOrRefresh(Id("one-too-many"), 60));
    }

    [Fact]
    public void Refreshing_an_established_lease_works_even_at_the_cap()
    {
        // The limit must bound growth, not evict working subscriptions: a peer at the cap still has to be
        // able to renew what it already holds, or the cap would terminate healthy dialogs.
        using var manager = NewManager();
        for (var i = 0; i < 32; i++)
            manager.ActivateOrRefresh(Id($"id{i}"), 60);

        var refreshed = manager.ActivateOrRefresh(Id("id0"), 120);

        Assert.Equal(120, refreshed.EffectiveExpiresSeconds);
    }

    [Fact]
    public void Terminating_a_lease_frees_the_slot()
    {
        using var manager = NewManager();
        for (var i = 0; i < 32; i++)
            manager.ActivateOrRefresh(Id($"id{i}"), 60);

        manager.Terminate(Id("id0"), reason: "deactivated");

        // Room again — the cap counts live leases, not lifetime arrivals.
        var update = manager.ActivateOrRefresh(Id("fresh"), 60);
        Assert.Equal(60, update.EffectiveExpiresSeconds);
    }
}
