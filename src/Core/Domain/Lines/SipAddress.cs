namespace CalloraVoipSdk.Core.Domain.Lines;

/// <summary>Immutable, validated SIP address (e.g. sip:alice@example.com).</summary>
public readonly record struct SipAddress
{
    /// <summary>The full normalised SIP URI including the scheme (e.g. <c>sip:alice@example.com</c>).</summary>
    public string Value    { get; }

    /// <summary>The user-part (before the <c>@</c>).</summary>
    public string User     { get; }

    /// <summary>The host-part (after the <c>@</c>): the SIP domain or host.</summary>
    public string Host     { get; }

    /// <summary>
    /// Parses a SIP address. A missing <c>sip:</c>/<c>sips:</c> scheme is prefixed with <c>sip:</c>.
    /// </summary>
    /// <param name="value">The address, with or without scheme; must contain <c>user@host</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is blank or has no <c>user@host</c> form.</exception>
    public SipAddress(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));

        var normalised = value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"sip:{value}";

        var afterScheme = normalised[(normalised.IndexOf(':') + 1)..];
        var atIndex     = afterScheme.IndexOf('@');

        if (atIndex <= 0)
            throw new ArgumentException($"SIP address must contain user@host: '{value}'", nameof(value));

        User  = afterScheme[..atIndex];
        Host  = afterScheme[(atIndex + 1)..];
        Value = normalised;
    }

    /// <summary>Builds a <c>sip:</c> address from a user-part and host.</summary>
    /// <param name="username">The user-part.</param>
    /// <param name="host">The host or SIP domain.</param>
    public static SipAddress From(string username, string host) =>
        new($"sip:{username}@{host}");

    /// <summary>Returns the full SIP URI (<see cref="Value"/>).</summary>
    public override string ToString() => Value;
}
