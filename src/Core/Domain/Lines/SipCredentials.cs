namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>Immutable SIP authentication credentials.</summary>
public sealed record SipCredentials
{
    /// <summary>The authentication user name.</summary>
    public string Username { get; }

    /// <summary>The authentication password (may be empty for non-authenticating accounts).</summary>
    public string Password { get; }

    /// <summary>The authentication realm; empty when not yet known (filled from the registrar challenge).</summary>
    public string Realm    { get; }

    /// <summary>Creates SIP credentials.</summary>
    /// <param name="username">The authentication user name (required).</param>
    /// <param name="password">The password; may be empty but not <see langword="null"/>.</param>
    /// <param name="realm">The authentication realm; optional.</param>
    /// <exception cref="ArgumentException"><paramref name="username"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="password"/> is <see langword="null"/>.</exception>
    public SipCredentials(string username, string password, string realm = "")
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username required.", nameof(username));
        Username = username;
        Password = password ?? throw new ArgumentNullException(nameof(password));
        Realm    = realm;
    }
}
