using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Builds the <c>Refer-To</c> value for an RFC 5589 attended transfer: the consultation target's
/// URI with an embedded, URI-escaped RFC 3891 <c>Replaces</c> identifying the consultation dialog
/// from the transfer target's perspective.
/// </summary>
internal static class AttendedTransferReferTo
{
    /// <summary>
    /// Builds the attended-transfer <c>Refer-To</c> value, or <see langword="null"/> when the
    /// consultation dialog is not established (missing tags) or the target URI cannot be parsed —
    /// in which case the caller falls back to a plain REFER. Per RFC 3891 matching, <c>to-tag</c>
    /// is the target's tag (our remote tag) and <c>from-tag</c> is our tag (our local tag).
    /// </summary>
    public static string? Build(string callId, string? localTag, string? remoteTag, string remoteUri)
    {
        if (string.IsNullOrWhiteSpace(callId)
            || string.IsNullOrWhiteSpace(localTag)
            || string.IsNullOrWhiteSpace(remoteTag)
            || !TryBuildAddrSpec(remoteUri, out var addrSpec))
        {
            return null;
        }

        var replaces = new SipReplacesHeaderValue(callId, toTag: remoteTag!, fromTag: localTag!, earlyOnly: false);
        return replaces.BuildReferToUri(addrSpec);
    }

    /// <summary>
    /// Reduces a SIP URI or bracketed name-addr (for example
    /// <c>&lt;sip:bob@host;transport=udp&gt;</c>) to a bare addr-spec (<c>sip:bob@host</c> /
    /// <c>sips:...</c>), dropping URI parameters and embedded headers.
    /// </summary>
    private static bool TryBuildAddrSpec(string? uri, out string addrSpec)
    {
        addrSpec = string.Empty;
        if (string.IsNullOrWhiteSpace(uri) || !SipProtocol.TryParseSipUri(uri, out var user, out var host, out var port))
            return false;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var scheme = SipProtocol.IsSipsUri(uri) ? "sips" : "sip";
        var userPart = string.IsNullOrWhiteSpace(user) ? string.Empty : $"{user}@";
        var portPart = port.HasValue ? $":{port.Value}" : string.Empty;
        addrSpec = $"{scheme}:{userPart}{host}{portPart}";
        return true;
    }
}
