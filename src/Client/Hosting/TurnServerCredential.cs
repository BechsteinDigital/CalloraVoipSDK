namespace CalloraVoipSdk.Hosting;

/// <summary>
/// One long-term credential the hosted TURN server accepts (RFC 8656 / RFC 5389 §10.2). The realm is set once
/// on the host configuration (<see cref="TurnServerHostConfiguration.Realm"/>); each credential carries only the
/// username and password.
/// </summary>
public sealed class TurnServerCredential
{
    /// <summary>The username the client authenticates with.</summary>
    public required string Username { get; init; }

    /// <summary>The password the long-term MESSAGE-INTEGRITY key is derived from.</summary>
    public required string Password { get; init; }
}
