using CalloraVoipSdk.Core.Application.Media.Ice;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// Verifies the deterministic ICE tie-breaker derivation (RFC 8445 §5.2): the same per-session
/// seed (local ICE password) yields the same 64-bit value, so the outbound agent and the inbound
/// handler independently agree on one tie-breaker and a role conflict resolves consistently.
/// </summary>
public sealed class IceTieBreakerTests
{
    [Fact]
    public void Derive_is_deterministic_for_the_same_seed()
        => Assert.Equal(IceTieBreaker.Derive("localPwd"), IceTieBreaker.Derive("localPwd"));

    [Fact]
    public void Derive_differs_for_different_seeds()
        => Assert.NotEqual(IceTieBreaker.Derive("pwdA"), IceTieBreaker.Derive("pwdB"));

    [Fact]
    public void Derive_is_non_zero_for_a_typical_password()
        => Assert.NotEqual(0ul, IceTieBreaker.Derive("a-typical-ice-password"));
}
