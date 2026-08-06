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

    /// <summary>
    /// Returns a diagnostic string that deliberately <b>never</b> includes the password (#165 P1-3). The
    /// record-generated <c>ToString()</c>/<c>PrintMembers</c> would otherwise emit every property — including
    /// <see cref="Password"/> — so a structured log, an exception dump or a debugger inspection would persist the
    /// cleartext authentication secret. Only the non-secret <see cref="Username"/> and <see cref="Realm"/> are
    /// surfaced; the presence of a password is marked with a fixed redaction placeholder.
    /// </summary>
    public override string ToString()
        => $"SipCredentials {{ Username = {Username}, Realm = {Realm}, Password = *** }}";
}
