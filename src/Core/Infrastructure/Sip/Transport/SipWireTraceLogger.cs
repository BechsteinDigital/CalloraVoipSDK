using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Transport;

/// <summary>
/// Trace-level wire logging for SIP messages (method or status, remote, transport, CSeq,
/// and the body — SDP offers/answers appear verbatim). All entry points guard on
/// <see cref="LogLevel.Trace"/> so the hot signaling path pays no formatting or boxing
/// cost when tracing is disabled.
/// </summary>
internal static class SipWireTraceLogger
{
    public static void RequestReceived(ILogger logger, SipRequest request, IPEndPoint remote, SipTransportProtocol transport)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace(
            "SIP {Method} received from {Remote} on {Transport} (CSeq: {CSeq}).{Body}",
            request.Method, remote, transport, request.Header("CSeq"), FormatBody(request.Body));
    }

    public static void ResponseReceived(ILogger logger, SipResponse response, IPEndPoint remote, SipTransportProtocol transport)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace(
            "SIP {Status} {Reason} received from {Remote} on {Transport} (CSeq: {CSeq}).{Body}",
            response.StatusCode, response.ReasonPhrase, remote, transport, response.Header("CSeq"), FormatBody(response.Body));
    }

    public static void RequestSent(ILogger logger, string method, IReadOnlyDictionary<string, string> headers, string? body, IPEndPoint remote, SipTransportProtocol transport)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace(
            "SIP {Method} sent to {Remote} on {Transport} (CSeq: {CSeq}).{Body}",
            method, remote, transport, HeaderOrNull(headers, "CSeq"), FormatBody(body));
    }

    public static void ResponseSent(ILogger logger, int statusCode, string reasonPhrase, IReadOnlyDictionary<string, string> headers, string? body, IPEndPoint remote, SipTransportProtocol transport)
    {
        if (!logger.IsEnabled(LogLevel.Trace)) return;
        logger.LogTrace(
            "SIP {Status} {Reason} sent to {Remote} on {Transport} (CSeq: {CSeq}, Contact: {Contact}).{Body}",
            statusCode, reasonPhrase, remote, transport,
            HeaderOrNull(headers, "CSeq"), HeaderOrNull(headers, "Contact"), FormatBody(body));
    }

    private static string? HeaderOrNull(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static string FormatBody(string? body) =>
        string.IsNullOrWhiteSpace(body) ? string.Empty : $"\n{RedactSensitiveSdp(body.TrimEnd())}";

    /// <summary>
    /// Redacts secrets that SDP carries in the clear — SDES SRTP keys
    /// (<c>a=crypto ... inline:</c>) and ICE passwords (<c>a=ice-pwd:</c>) — so Trace wire logs
    /// (often shipped to central log systems) cannot leak key material. Non-secret lines
    /// (suite name, ICE ufrag, codecs) are preserved for diagnostics.
    /// </summary>
    internal static string RedactSensitiveSdp(string body)
    {
        if (body.IndexOf("inline:", StringComparison.OrdinalIgnoreCase) < 0
            && body.IndexOf("a=ice-pwd:", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return body;
        }

        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("a=crypto:", StringComparison.OrdinalIgnoreCase))
                lines[i] = RedactInlineKeys(lines[i]);
            else if (trimmed.StartsWith("a=ice-pwd:", StringComparison.OrdinalIgnoreCase))
                lines[i] = RedactAfterPrefix(lines[i], "a=ice-pwd:");
        }

        return string.Join('\n', lines);
    }

    private static string RedactInlineKeys(string line)
    {
        // a=crypto:<tag> <suite> inline:<key-params> [inline:<key-params> ...] [session-params]
        var result = new System.Text.StringBuilder(line.Length);
        var index = 0;
        while (index < line.Length)
        {
            var inlineIndex = line.IndexOf("inline:", index, StringComparison.OrdinalIgnoreCase);
            if (inlineIndex < 0)
            {
                result.Append(line, index, line.Length - index);
                break;
            }

            var keyStart = inlineIndex + "inline:".Length;
            var keyEnd = keyStart;
            while (keyEnd < line.Length && !char.IsWhiteSpace(line[keyEnd]))
                keyEnd++;

            result.Append(line, index, keyStart - index);
            result.Append("<redacted>");
            index = keyEnd;
        }

        return result.ToString();
    }

    private static string RedactAfterPrefix(string line, string prefix)
    {
        var prefixIndex = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return prefixIndex < 0
            ? line
            : string.Concat(line.AsSpan(0, prefixIndex + prefix.Length), "<redacted>");
    }
}
