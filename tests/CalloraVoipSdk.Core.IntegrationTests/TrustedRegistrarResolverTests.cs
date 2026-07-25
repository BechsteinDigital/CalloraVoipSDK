using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #13 follow-up: <see cref="TrustedRegistrarResolver"/> resolves trusted registrar addresses off the caller's
/// thread, caches a successful result, and — after a transient DNS failure — retries only once a bounded back-off
/// has elapsed (never permanently stranding the line, never hammering DNS while a host is down). DNS and clock are
/// injected so the back-off/retry path is deterministic.
/// </summary>
public sealed class TrustedRegistrarResolverTests
{
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    private static TrustedRegistrarResolver Resolver(
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<long>? clock = null,
        IReadOnlyList<string>? hosts = null)
        => new(hosts ?? new[] { "sip.example" }, NullLogger.Instance, resolve, clock, Backoff);

    [Fact]
    public void A_successful_resolution_is_cached_and_not_re_resolved()
    {
        var attempts = 0;
        var resolver = Resolver((_, _) => { attempts++; return Task.FromResult(new[] { IPAddress.Loopback }); });

        // The first read triggers the (synchronous fake) resolve and returns empty; the cache is then warm.
        Assert.Empty(resolver.Addresses());
        Assert.Equal(1, attempts);
        Assert.Contains(IPAddress.Loopback, resolver.Addresses());
        Assert.Contains(IPAddress.Loopback, resolver.Addresses());
        Assert.Equal(1, attempts); // no re-resolution once cached
    }

    [Fact]
    public void A_transient_failure_backs_off_and_then_retries_and_caches()
    {
        var now = 0L;
        var attempts = 0;
        var resolver = Resolver(
            (_, _) => ++attempts == 1
                ? Task.FromException<IPAddress[]>(new Exception("dns down"))
                : Task.FromResult(new[] { IPAddress.Loopback }),
            clock: () => now);

        Assert.Empty(resolver.Addresses());   // attempt 1 fails → nothing cached
        Assert.Equal(1, attempts);
        Assert.Empty(resolver.Addresses());   // still within the back-off → no new attempt
        Assert.Equal(1, attempts);

        now = 30_000;                          // back-off elapsed
        Assert.Empty(resolver.Addresses());    // attempt 2 succeeds; cache populated during this call
        Assert.Equal(2, attempts);
        Assert.Contains(IPAddress.Loopback, resolver.Addresses()); // now cached
    }

    [Fact]
    public void No_configured_host_caches_empty_and_never_resolves()
    {
        var attempts = 0;
        var resolver = Resolver(
            (_, _) => { attempts++; return Task.FromResult(new[] { IPAddress.Loopback }); },
            hosts: Array.Empty<string>());

        Assert.Empty(resolver.Addresses());
        Assert.Empty(resolver.Addresses());
        Assert.Equal(0, attempts); // no host configured → resolver never invoked
    }
}
