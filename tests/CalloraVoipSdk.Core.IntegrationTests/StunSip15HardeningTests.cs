using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Client;
using CalloraVoipSdk.Core.Infrastructure.Stun.Server;

namespace CalloraVoipSdk.Core.IntegrationTests;

/// <summary>
/// SIP-15 STUN/TURN/ICE hardening gate covering three mechanical fixes:
/// <list type="bullet">
/// <item>A1 — the DNS-SRV query ID is cryptographically random across the full 16-bit range
/// (RFC 5452 §10 anti-spoofing), not <c>Random.Shared.Next(0, 65535)</c> which excluded 0xFFFF.</item>
/// <item>A3 — <see cref="InMemoryStunCredentialProvider"/> never crosses the credential-type
/// boundary: a short-term lookup for a username that also has a long-term entry must not return the
/// long-term credential (which would derive the wrong MESSAGE-INTEGRITY key).</item>
/// <item>A4 — <see cref="Rfc7635AccessTokenValidator.DecodeRfc7635Timestamp"/> divides the 16-bit
/// fraction by 2^16 = 65536 (RFC 7635 §6.2), not 64000.</item>
/// </list>
/// </summary>
public sealed class StunSip15HardeningTests
{
    // ── A1: DNS-SRV transaction ID ────────────────────────────────────────────

    [Fact]
    public void DnsSrv_transaction_id_can_reach_the_full_16_bit_range()
    {
        // Random.Shared.Next(0, 65535) can never return 0xFFFF (exclusive upper bound). The crypto
        // generator draws from the whole 0..0xFFFF range, so over enough draws it must hit 0xFFFF.
        bool sawMax = false;
        for (int i = 0; i < 5_000_000 && !sawMax; i++)
        {
            if (DnsSrvQuery.NextTransactionId() == ushort.MaxValue)
                sawMax = true;
        }

        Assert.True(sawMax, "DNS-SRV transaction ID never reached 0xFFFF — upper bound is still exclusive");
    }

    [Fact]
    public void DnsSrv_transaction_id_is_not_uniformly_predictable()
    {
        // Weak sanity check that the source is not degenerate/constant: many draws yield many
        // distinct values. (Not a statistical CSPRNG proof — that lives in the crypto library.)
        var seen = new HashSet<ushort>();
        for (int i = 0; i < 2_000; i++)
            seen.Add(DnsSrvQuery.NextTransactionId());

        Assert.True(seen.Count > 100, "DNS-SRV transaction ID space is suspiciously small");
    }

    // ── A3: credential-type boundary ──────────────────────────────────────────

    [Fact]
    public void ShortTerm_lookup_does_not_return_a_longterm_credential_of_the_same_username()
    {
        // Same username carries both a long-term (has realm) and a short-term (no realm) entry.
        var provider = new InMemoryStunCredentialProvider(new[]
        {
            new StunCredentials { Username = "alice", Password = "lt-secret", Realm = "example.org" },
            new StunCredentials { Username = "alice", Password = "st-secret" },
        });

        // Short-term request (no realm) must resolve to the short-term entry, never the long-term one.
        var found = provider.TryGetCredentials("alice", realm: null, out var creds);

        Assert.True(found);
        Assert.False(creds.IsLongTerm);
        Assert.Equal("st-secret", creds.Password);
    }

    [Fact]
    public void ShortTerm_lookup_with_only_a_longterm_entry_fails_rather_than_crossing_types()
    {
        // Only a long-term entry exists for the username; a short-term (no-realm) lookup must NOT
        // fall back to it — returning it would derive the MD5 long-term key for a SASLprep request.
        var provider = new InMemoryStunCredentialProvider(new[]
        {
            new StunCredentials { Username = "bob", Password = "lt-secret", Realm = "example.org" },
        });

        var found = provider.TryGetCredentials("bob", realm: null, out _);

        Assert.False(found);
    }

    [Fact]
    public void ShortTerm_lookup_still_resolves_a_plain_shortterm_entry()
    {
        // Behaviour-preserving happy path: the ordinary short-term case is unchanged.
        var provider = new InMemoryStunCredentialProvider(new[]
        {
            new StunCredentials { Username = "carol", Password = "st-secret" },
        });

        Assert.True(provider.TryGetCredentials("carol", realm: null, out var creds));
        Assert.False(creds.IsLongTerm);
        Assert.Equal("st-secret", creds.Password);
    }

    [Fact]
    public void LongTerm_lookup_still_requires_exact_username_and_realm()
    {
        // Behaviour-preserving: long-term path unchanged and never returns a mismatched realm.
        var provider = new InMemoryStunCredentialProvider(new[]
        {
            new StunCredentials { Username = "dave", Password = "lt-secret", Realm = "example.org" },
        });

        Assert.True(provider.TryGetCredentials("dave", "example.org", out var creds));
        Assert.True(creds.IsLongTerm);
        Assert.False(provider.TryGetCredentials("dave", "other.org", out _));
    }

    // ── A4: RFC 7635 timestamp divisor ────────────────────────────────────────

    [Fact]
    public void Rfc7635_timestamp_half_second_fraction_decodes_to_500ms()
    {
        // fraction 0x8000 = 32768 = half of 2^16 → exactly 0.5 s past the whole second.
        ulong raw = (100UL << 16) | 0x8000UL;

        var decoded = Rfc7635AccessTokenValidator.DecodeRfc7635Timestamp(raw);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(100).AddMilliseconds(500), decoded);
    }

    [Fact]
    public void Rfc7635_timestamp_whole_seconds_have_no_fraction()
    {
        ulong raw = 4200UL << 16; // fraction 0

        var decoded = Rfc7635AccessTokenValidator.DecodeRfc7635Timestamp(raw);

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(4200), decoded);
    }
}
