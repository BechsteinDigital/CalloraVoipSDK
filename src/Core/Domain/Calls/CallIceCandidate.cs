namespace CalloraVoipSdk.Core.Domain.Calls;

/// <summary>
/// Represents one ICE candidate associated with a call media leg.
/// </summary>
/// <remarks>
/// Validated on assignment (#165 P3-12). These values are not descriptive — they are inputs to candidate
/// pairing and prioritisation, where an unusable one costs a failed call with nothing pointing at the
/// candidate that caused it. The bounds are the ones the SDP side already enforces on the wire
/// (<c>SdpIceGrammar</c>, RFC 8839 §5.1 / RFC 8445 §5.1), so a candidate parsed off an offer or answer
/// passes them by construction: this guards the objects the SDK itself and its callers build.
/// The wire-grammar character classes stay on the parser — that is the trust boundary for them (K4).
/// </remarks>
public sealed class CallIceCandidate
{
    /// <summary>
    /// Candidate foundation identifier.
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank or longer than 32 characters.</exception>
    public required string Foundation
    {
        get => _foundation;
        init => _foundation = !string.IsNullOrWhiteSpace(value) && value.Length <= 32
            ? value
            : throw new ArgumentException(
                "An ICE foundation is 1..32 characters (RFC 8839 §5.1).", nameof(Foundation));
    }

    private readonly string _foundation = string.Empty;

    /// <summary>
    /// Candidate component identifier (1 = RTP, 2 = RTCP).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1..256.</exception>
    public required int Component
    {
        get => _component;
        init => _component = value is >= 1 and <= 256
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(Component), value, "An ICE component id is 1..256 (RFC 8839 §5.1; 1 = RTP, 2 = RTCP).");
    }

    private readonly int _component = 1;

    /// <summary>
    /// Candidate transport token (for example UDP or TCP).
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    /// <remarks>
    /// Not restricted to UDP/TCP on purpose, matching the parser: deployed endpoints emit <c>ssltcp</c> and
    /// similar, and a transport this stack cannot pair on is filtered where pairing happens.
    /// </remarks>
    public required string Transport
    {
        get => _transport;
        init => _transport = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("An ICE candidate needs a transport token.", nameof(Transport));
    }

    private readonly string _transport = string.Empty;

    /// <summary>
    /// Candidate priority value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1..2^31-1.</exception>
    public required long Priority
    {
        get => _priority;
        // Zero would sort below every real candidate, so it is not a priority (RFC 8445 §5.1.2).
        init => _priority = value is >= 1 and <= int.MaxValue
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(Priority), value, "An ICE priority is 1..2147483647 (RFC 8445 §5.1.2).");
    }

    private readonly long _priority = 1;

    /// <summary>
    /// Candidate address.
    /// </summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    public required string Address
    {
        get => _address;
        init => _address = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("An ICE candidate needs an address.", nameof(Address));
    }

    private readonly string _address = string.Empty;

    /// <summary>
    /// Candidate port.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0..65535.</exception>
    public required int Port
    {
        get => _port;
        // 0 is allowed: it is how a disabled candidate is expressed (RFC 8866 §5.14).
        init => _port = value is >= 0 and <= 65535
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Port), value, "A port is 0..65535.");
    }

    private readonly int _port;

    /// <summary>
    /// Candidate type token (host, srflx, prflx, relay).
    /// </summary>
    /// <exception cref="ArgumentException">The value is not one of host, srflx, prflx, relay.</exception>
    public required string Type
    {
        get => _type;
        init => _type = IsKnownType(value)
            ? value
            : throw new ArgumentException(
                $"'{value}' is not an ICE candidate type; expected host, srflx, prflx or relay (RFC 8445 §5.1.1).",
                nameof(Type));
    }

    private readonly string _type = "host";

    /// <summary>
    /// Related address (raddr) when present.
    /// </summary>
    public string? RelatedAddress { get; init; }

    /// <summary>
    /// Related port (rport) when present.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is set and outside 0..65535.</exception>
    public int? RelatedPort
    {
        get => _relatedPort;
        init => _relatedPort = value is null or (>= 0 and <= 65535)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RelatedPort), value, "A port is 0..65535.");
    }

    private readonly int? _relatedPort;

    /// <summary>
    /// ICE generation value when present.
    /// </summary>
    public int? Generation { get; init; }

    /// <summary>
    /// Per-candidate ICE ufrag extension when present.
    /// </summary>
    public string? Ufrag { get; init; }

    /// <summary>
    /// Network-ID extension when present.
    /// </summary>
    public int? NetworkId { get; init; }

    private static bool IsKnownType(string? type) =>
        type is not null
        && (type.Equals("host", StringComparison.OrdinalIgnoreCase)
            || type.Equals("srflx", StringComparison.OrdinalIgnoreCase)
            || type.Equals("prflx", StringComparison.OrdinalIgnoreCase)
            || type.Equals("relay", StringComparison.OrdinalIgnoreCase));
}
