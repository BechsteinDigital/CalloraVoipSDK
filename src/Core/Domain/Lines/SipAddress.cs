namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>Immutable, validated SIP address (e.g. sip:alice@example.com).</summary>
public readonly record struct SipAddress
{
    /// <summary>The full normalised SIP URI including the scheme (e.g. <c>sip:alice@example.com</c>).</summary>
    public string Value    { get; }

    /// <summary>
    /// The user-part (before the <c>@</c>), or empty for a host-only address such as
    /// <c>sip:trunk.example</c>.
    /// </summary>
    public string User     { get; }

    /// <summary>The host-part: the SIP domain or host.</summary>
    public string Host     { get; }

    /// <summary>
    /// Parses a SIP address. A missing <c>sip:</c>/<c>sips:</c> scheme is prefixed with <c>sip:</c>.
    /// </summary>
    /// <param name="value">
    /// The address, with or without scheme. The user-part is optional (RFC 3261 §19.1.1): both
    /// <c>sip:alice@example.com</c> and the host-only <c>sip:trunk.example</c> are valid. What stays
    /// invalid is an empty user-part that still carries the separator — <c>sip:@example.com</c> is not a
    /// SIP URI, and accepting it would put that string on the wire.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank or has an empty user-part before an <c>@</c>.</exception>
    public SipAddress(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));

        var normalised = value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"sip:{value}";

        var afterScheme = normalised[(normalised.IndexOf(':') + 1)..];
        var atIndex     = afterScheme.IndexOf('@');

        if (atIndex == 0)
            throw new ArgumentException($"SIP address has an empty user-part: '{value}'", nameof(value));

        User  = atIndex < 0 ? string.Empty : afterScheme[..atIndex];
        Host  = afterScheme[(atIndex + 1)..];
        Value = normalised;
    }

    /// <summary>Builds a <c>sip:</c> address from an optional user-part and a host.</summary>
    /// <param name="username">
    /// The user-part. Empty or whitespace produces a host-only address (<c>sip:host</c>) — what an
    /// IP-authenticated trunk without an account user needs; interpolating it anyway would yield the
    /// invalid <c>sip:@host</c>.
    /// </param>
    /// <param name="host">The host or SIP domain.</param>
    public static SipAddress From(string username, string host) =>
        new(string.IsNullOrWhiteSpace(username) ? $"sip:{host}" : $"sip:{username}@{host}");

    /// <summary>Returns the full SIP URI (<see cref="Value"/>).</summary>
    public override string ToString() => Value;
}
