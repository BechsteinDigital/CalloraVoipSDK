using CalloraVoipSdk.Core.Infrastructure.Common.Relay;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// <see cref="CompositeRelayKeepAlive"/> drives several keepalive loops through one seam: it starts every member
/// in order and disposes every member in reverse start order — all of them even when one throws — aggregating
/// the dispose failures.
/// </summary>
public sealed class CompositeRelayKeepAliveTests
{
    private sealed class FakeKeepAlive(string name, List<string> log, bool throwOnDispose = false) : IRelayKeepAlive
    {
        public void Start()
        {
            lock (log) log.Add($"start:{name}");
        }

        public ValueTask DisposeAsync()
        {
            lock (log) log.Add($"dispose:{name}");
            return throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException(name))
                : ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Start_starts_every_member_in_order()
    {
        var log = new List<string>();
        var composite = new CompositeRelayKeepAlive(new FakeKeepAlive("a", log), new FakeKeepAlive("b", log));

        composite.Start();

        Assert.Equal(new[] { "start:a", "start:b" }, log);
    }

    [Fact]
    public async Task Dispose_disposes_every_member_in_reverse_start_order()
    {
        var log = new List<string>();
        var composite = new CompositeRelayKeepAlive(new FakeKeepAlive("a", log), new FakeKeepAlive("b", log));

        await composite.DisposeAsync();

        Assert.Equal(new[] { "dispose:b", "dispose:a" }, log);
    }

    [Fact]
    public async Task Dispose_disposes_all_members_even_when_one_throws_and_aggregates_the_failures()
    {
        var log = new List<string>();
        var composite = new CompositeRelayKeepAlive(
            new FakeKeepAlive("a", log),
            new FakeKeepAlive("b", log, throwOnDispose: true),
            new FakeKeepAlive("c", log));

        var error = await Assert.ThrowsAsync<AggregateException>(async () => await composite.DisposeAsync());

        // c and a are still disposed despite b throwing; b's failure surfaces in the aggregate.
        Assert.Equal(new[] { "dispose:c", "dispose:b", "dispose:a" }, log);
        Assert.Single(error.InnerExceptions);
        Assert.IsType<InvalidOperationException>(error.InnerExceptions[0]);
    }

    [Fact]
    public void A_null_member_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CompositeRelayKeepAlive(new FakeKeepAlive("a", []), null!));
    }
}
