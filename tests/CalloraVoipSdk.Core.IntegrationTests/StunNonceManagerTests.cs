using CalloraVoipSdk.Core.Infrastructure.Stun.Server;
using Xunit;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// #156 STUN P1-2 (nonce amplification). The long-term-credential nonce manager must be stateless:
/// challenge issuance to unauthenticated peers must not accumulate server memory, while a nonce it
/// minted still round-trips and forgeries/expired values are rejected (RFC 5389 §10.2.2).
/// </summary>
public sealed class StunNonceManagerTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public void A_freshly_generated_nonce_is_valid()
    {
        var manager = new StunNonceManager();

        var nonce = manager.GenerateNonce();

        Assert.True(manager.IsNonceValid(nonce));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 $$$")]
    [InlineData("YWJj")] // valid base64 but far too short to be a nonce
    public void Malformed_or_unknown_values_are_rejected(string value)
    {
        Assert.False(new StunNonceManager().IsNonceValid(value));
    }

    [Fact]
    public void A_random_string_of_the_right_length_is_rejected()
    {
        // A 32-byte payload the attacker did not have the secret to MAC.
        var forged = Convert.ToBase64String(new byte[32]);

        Assert.False(new StunNonceManager().IsNonceValid(forged));
    }

    [Fact]
    public void A_tampered_nonce_is_rejected()
    {
        var manager = new StunNonceManager();
        var bytes = Convert.FromBase64String(manager.GenerateNonce());
        bytes[10] ^= 0xFF; // flip a byte in the signed timestamp region

        Assert.False(manager.IsNonceValid(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void A_nonce_minted_by_another_manager_is_rejected()
    {
        var nonce = new StunNonceManager().GenerateNonce();

        // A different instance has a different signing secret.
        Assert.False(new StunNonceManager().IsNonceValid(nonce));
    }

    [Fact]
    public void A_nonce_is_valid_up_to_and_including_the_ttl_boundary()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clock = now;
        var manager = new StunNonceManager(Ttl, () => clock);
        var nonce = manager.GenerateNonce();

        clock = now + Ttl; // exactly at the boundary
        Assert.True(manager.IsNonceValid(nonce));

        clock = now + Ttl + TimeSpan.FromTicks(1); // one tick past
        Assert.False(manager.IsNonceValid(nonce));
    }

    [Fact]
    public void A_future_dated_nonce_is_rejected()
    {
        var issued = DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(10);
        var validateAt = DateTimeOffset.UnixEpoch;
        var clock = issued;
        var manager = new StunNonceManager(Ttl, () => clock);
        var nonce = manager.GenerateNonce();

        clock = validateAt; // clock rewound below issuance
        Assert.False(manager.IsNonceValid(nonce));
    }

    [Fact]
    public void Issuing_many_nonces_retains_no_per_nonce_state()
    {
        // A stateful store would either grow without bound or evict the first nonce; a stateless
        // manager holds nothing yet the very first nonce stays valid after a large issuance burst.
        var clock = DateTimeOffset.UnixEpoch;
        var manager = new StunNonceManager(Ttl, () => clock);
        var first = manager.GenerateNonce();

        for (var i = 0; i < 100_000; i++)
            _ = manager.GenerateNonce();

        Assert.True(manager.IsNonceValid(first));
    }
}
