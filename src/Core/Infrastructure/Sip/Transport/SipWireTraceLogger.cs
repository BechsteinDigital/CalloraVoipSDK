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
        string.IsNullOrWhiteSpace(body) ? string.Empty : $"\n{body.TrimEnd()}";
}
