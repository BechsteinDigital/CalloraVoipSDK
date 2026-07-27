using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Parses and formats SIP Reason header values (RFC 3326).
/// </summary>
internal static class SipReasonHeader
{
    /// <summary>
    /// Tries to parse the first reason-value from a Reason header.
    /// Returns false when the header does not contain a valid reason-value.
    /// </summary>
    public static bool TryParseFirst(
        string? headerValue,
        out SipDialogTerminationReason? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var reasonValue = ProtocolCommonUtilities
            .SplitCommaSeparatedRespectingQuotes(headerValue)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(reasonValue))
            return false;

        var segments = reasonValue
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var protocol = segments[0].Trim();
        if (string.IsNullOrWhiteSpace(protocol))
            return false;

        int? cause = null;
        string? text = null;

        for (var i = 1; i < segments.Length; i++)
        {
            var parameter = segments[i];
            var equalsIndex = parameter.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var name = parameter[..equalsIndex].Trim();
            var value = parameter[(equalsIndex + 1)..].Trim();
            if (name.Equals("cause", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var parsedCause))
                    cause = parsedCause;
                continue;
            }

            if (name.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                text = UnquoteAndUnescape(value);
            }
        }

        reason = new SipDialogTerminationReason(protocol, cause, text);
        return true;
    }

    /// <summary>
    /// Formats one reason value for Reason header emission.
    /// </summary>
    public static string Format(SipDialogTerminationReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var formatted = reason.Protocol;
        if (reason.Cause is { } cause)
            formatted = $"{formatted};cause={cause}";
        if (!string.IsNullOrWhiteSpace(reason.Text))
        {
            var escapedText = ProtocolCommonUtilities.EscapeQuotedHeaderValue(reason.Text);
            formatted = $"{formatted};text=\"{escapedText}\"";
        }

        return formatted;
    }

    /// <summary>
    /// Builds one SIP protocol reason from a status code and reason phrase. The status is carried in
    /// <see cref="SipDialogTerminationReason.SipStatusCode"/> as well as in the RFC 3326
    /// <see cref="SipDialogTerminationReason.Cause"/>, so a locally originated termination (a CANCEL 487,
    /// a local 486 reject, a 408 timeout) classifies on its real status — the authoritative signal
    /// (#103) — instead of falling back to the connected-gate and mis-reporting Failed. Only the
    /// protocol/cause/text are serialized onto the wire Reason header, so the status is a classification
    /// hint that never changes the emitted header.
    /// </summary>
    public static SipDialogTerminationReason CreateSipStatusReason(
        int statusCode,
        string reasonPhrase) =>
        new(
            protocol: "SIP",
            cause: statusCode,
            text: reasonPhrase,
            sipStatusCode: statusCode);

    /// <summary>
    /// Removes one optional surrounding quote pair and unescapes quoted-pair escapes.
    /// </summary>
    private static string UnquoteAndUnescape(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && trimmed[0] == '"'
            && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}
