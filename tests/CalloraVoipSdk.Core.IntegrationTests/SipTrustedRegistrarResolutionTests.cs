using CalloraVoipSdk.Core.Infrastructure.Sip.Adapters;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #13 backlog: the trusted-registrar DNS resolution must not permanently cache an empty result from a transient
/// failure. A configured host that resolved to nothing is retried (not cached); a successful resolution, or the
/// case where no registrar host is configured at all, is cached.
/// </summary>
public sealed class SipTrustedRegistrarResolutionTests
{
    [Fact]
    public void A_configured_host_that_resolved_nothing_is_not_cached_so_it_retries()
    {
        // Transient DNS failure: a host was configured but produced no addresses → retry, do not cache.
        Assert.False(SipLineChannel.ShouldCacheTrustedRegistrars(resolvedCount: 0, hadConfiguredHost: true));
    }

    [Fact]
    public void A_successful_resolution_is_cached()
    {
        Assert.True(SipLineChannel.ShouldCacheTrustedRegistrars(resolvedCount: 2, hadConfiguredHost: true));
    }

    [Fact]
    public void No_configured_host_caches_the_intentionally_empty_set()
    {
        Assert.True(SipLineChannel.ShouldCacheTrustedRegistrars(resolvedCount: 0, hadConfiguredHost: false));
    }
}
