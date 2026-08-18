using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Accepts an inbound request only when its Request-URI addresses one of a configured set of served
/// addresses-of-record (RFC 3261 §8.2.2.1: a UAS that does not serve the addressed user answers 404).
/// </summary>
/// <remarks>
/// <para>
/// The match is a full RFC 3261 §19.1.4 URI comparison, not a string comparison, because the two are genuinely
/// different: <c>sip:alice@Example.COM</c> addresses the same user as <c>sip:%61lice@example.com</c>, while
/// <c>sip:alice@example.com</c> and <c>sip:alice@example.com:5060</c> are <em>not</em> the same address —
/// omitting the port leaves it free to resolve elsewhere. A stack that compares these as strings either turns
/// away calls it serves or accepts calls it does not.
/// </para>
/// <para>
/// An empty served set is not "serve nobody" — it is "no policy configured", which the composition root
/// expresses by using <see cref="AcceptAllSipUasUserIdentityPolicy"/> instead. Reading emptiness as a total
/// block here would turn an unset option into a silent outage.
/// </para>
/// </remarks>
internal sealed class ServedUserSipIdentityPolicy : ISipUasUserIdentityPolicy
{
    private readonly IReadOnlyList<string> _servedAors;

    /// <param name="servedAors">The addresses-of-record this UAS answers for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="servedAors"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="servedAors"/> is empty.</exception>
    public ServedUserSipIdentityPolicy(IReadOnlyList<string> servedAors)
    {
        ArgumentNullException.ThrowIfNull(servedAors);
        if (servedAors.Count == 0)
            throw new ArgumentException(
                "At least one served address-of-record is required; an unconfigured set must use the accept-all policy.",
                nameof(servedAors));

        _servedAors = servedAors;
    }

    /// <inheritdoc />
    public bool IsServedUser(string requestUri)
    {
        if (string.IsNullOrWhiteSpace(requestUri))
            return false;

        foreach (var aor in _servedAors)
        {
            if (SipUriProtocol.SipUriEqual(aor, requestUri))
                return true;
        }

        return false;
    }
}
