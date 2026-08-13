namespace CalloraVoipSdk.Core.Infrastructure.Sdp.Models;

/// <summary>
/// The grammar rules RFC 8839 §5 defines for the ICE attributes carried in SDP.
/// </summary>
/// <remarks>
/// #160 P2-14: these values used to be taken verbatim off the wire, which matters because they are not
/// descriptive — they are inputs to the ICE agent. A ufrag outside 4..256 characters produces STUN
/// connectivity checks whose USERNAME can never match what the peer computes, so every check fails and
/// the call never connects, with nothing pointing at the SDP that caused it.
///
/// Calibrated against the reference stacks rather than against the RFC alone:
/// <list type="bullet">
/// <item>SIPSorcery validates none of this — <c>ice-ufrag</c>/<c>ice-pwd</c> are stored verbatim and
/// <c>a=candidate</c> is kept as an unparsed string.</item>
/// <item>libwebrtc (<c>IceParameters::Validate</c>) enforces the same length bounds used here and
/// rejects the session description on violation. On the character set it is deliberately laxer than
/// the RFC: <c>-</c>, <c>_</c>, <c>=</c> and <c>#</c> only produce a warning and still pass.</item>
/// </list>
/// The laxer character set is adopted on purpose. Enforcing ice-char strictly would reject peers that
/// Chrome accepts — stricter than the reference, but not better, since the value works perfectly well
/// as a STUN username either way. The length floor is a different matter: it is what gives the
/// short-term credential its entropy.
/// </remarks>
internal static class SdpIceGrammar
{
    // RFC 8839 §5.4: ice-char = ALPHA / DIGIT / "+" / "/". The four extra characters mirror what
    // libwebrtc still accepts from deployed endpoints.
    private static bool IsIceChar(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '+' or '/'
        || c is '-' or '_' or '=' or '#';

    private static bool IsIceCharString(string value, int minLength, int maxLength)
    {
        if (value.Length < minLength || value.Length > maxLength)
            return false;

        foreach (var c in value)
        {
            if (!IsIceChar(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// <c>ice-ufrag</c>: 4..256 characters (RFC 8839 §5.4, same bounds as libwebrtc).
    /// </summary>
    public static bool IsValidUfrag(string? value) =>
        value is not null && IsIceCharString(value, 4, 256);

    /// <summary>
    /// <c>ice-pwd</c>: 22..256 characters (RFC 8839 §5.4, same bounds as libwebrtc).
    /// </summary>
    public static bool IsValidPassword(string? value) =>
        value is not null && IsIceCharString(value, 22, 256);

    /// <summary>
    /// <c>foundation</c>: 1..32 characters (RFC 8839 §5.1).
    /// </summary>
    public static bool IsValidFoundation(string? value) =>
        value is not null && IsIceCharString(value, 1, 32);

    /// <summary>
    /// <c>component-id</c>: 1..256 (RFC 8839 §5.1 — 1 is RTP, 2 is RTCP).
    /// </summary>
    public static bool IsValidComponent(int component) => component is >= 1 and <= 256;

    /// <summary>
    /// <c>priority</c>: 1..2^31-1 (RFC 8445 §5.1.2). Zero would sort below every real candidate.
    /// </summary>
    public static bool IsValidPriority(long priority) => priority is >= 1 and <= int.MaxValue;

    /// <summary>
    /// A transport port, including 0 for a disabled candidate (RFC 8866 §5.14).
    /// </summary>
    public static bool IsValidPort(int port) => port is >= 0 and <= 65535;

    /// <summary>
    /// The candidate types RFC 8445 §5.1.1 defines. An unknown type cannot be prioritised or paired,
    /// so the candidate is dropped — the rest of the description stands.
    /// </summary>
    /// <remarks>
    /// The transport token is deliberately NOT whitelisted. UDP and TCP are what this stack pairs on,
    /// but deployed endpoints also emit <c>ssltcp</c> and similar; rejecting those would discard a
    /// candidate line the reference stacks keep, and the unusable ones are filtered where pairing
    /// happens rather than at the parser.
    /// </remarks>
    public static bool IsKnownCandidateType(string? type) =>
        type is not null
        && (type.Equals("host", StringComparison.OrdinalIgnoreCase)
            || type.Equals("srflx", StringComparison.OrdinalIgnoreCase)
            || type.Equals("prflx", StringComparison.OrdinalIgnoreCase)
            || type.Equals("relay", StringComparison.OrdinalIgnoreCase));
}
