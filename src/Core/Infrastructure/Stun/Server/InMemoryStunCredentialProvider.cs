using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Server;

/// <summary>
/// Immutable in-memory credential provider for STUN authentication.
/// Intended for multi-user deployments and tests where credential sets are preloaded.
/// </summary>
internal sealed class InMemoryStunCredentialProvider : IStunCredentialProvider
{
    private readonly IReadOnlyList<StunCredentials> _credentials;

    /// <summary>
    /// Creates a provider backed by a fixed set of credential entries.
    /// </summary>
    /// <param name="credentials">Credential entries available for lookup.</param>
    public InMemoryStunCredentialProvider(IEnumerable<StunCredentials> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        _credentials = credentials.ToArray();
        if (_credentials.Count == 0)
            throw new ArgumentException("At least one credential entry is required.", nameof(credentials));
    }

    /// <inheritdoc />
    public bool TryGetCredentials(string username, string? realm, out StunCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        // For long-term auth we require exact USERNAME + REALM match.
        if (!string.IsNullOrWhiteSpace(realm))
        {
            var longTerm = _credentials.FirstOrDefault(c =>
                string.Equals(c.Username, username, StringComparison.Ordinal)
                && string.Equals(c.Realm, realm, StringComparison.Ordinal));

            if (longTerm is not null)
            {
                credentials = longTerm;
                return true;
            }

            credentials = null!;
            return false;
        }

        // For short-term auth (no realm), only a short-term entry is valid. A long-term entry with
        // the same username derives a different HMAC key — MD5(user ":" realm ":" pass) (RFC 5389
        // §10.2.3) versus SASLprep(pass) (§10.1.1) — so the earlier "any username match" fallback
        // could hand back the wrong credential *type* and break MESSAGE-INTEGRITY verification. Match
        // strictly on username AND short-term type; never cross the credential-type boundary.
        var shortTerm = _credentials.FirstOrDefault(c =>
            !c.IsLongTerm
            && string.Equals(c.Username, username, StringComparison.Ordinal));

        if (shortTerm is not null)
        {
            credentials = shortTerm;
            return true;
        }

        credentials = null!;
        return false;
    }
}
