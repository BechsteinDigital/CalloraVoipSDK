using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Builds the minimal header set for ingress-level SIP responses (provisional handling and early
/// validation rejections). Symmetric to <see cref="SipIngressRequestPolicy"/> on the request side —
/// extracted from <c>SipCallSignalingService</c> so response-header shaping is one focused unit.
/// </summary>
internal static class SipIngressResponseHeaders
{
    /// <summary>
    /// Creates minimal response headers for ingress-level replies.
    /// </summary>
    public static Dictionary<string, string> Create(
        SipRequest request,
        int statusCode,
        IPEndPoint? remoteEndPoint = null)
    {
        // RFC 3581 §4: reflect rport/received into the Via header of responses.
        var viaValue = request.Header("Via") ?? string.Empty;
        if (remoteEndPoint is not null)
            viaValue = SipProtocol.ReflectViaRport(viaValue, remoteEndPoint);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = viaValue,
            ["From"] = request.Header("From") ?? string.Empty,
            ["To"] = request.Header("To") ?? string.Empty,
            ["Call-ID"] = request.Header("Call-ID") ?? string.Empty,
            ["CSeq"] = request.Header("CSeq") ?? string.Empty,
            ["Supported"] = "100rel, timer, replaces",
            ["Server"] = "CalloraVoipSdk/1.0",
            ["Date"] = DateTimeOffset.UtcNow.ToString("r"),
            ["User-Agent"] = "CalloraVoipSdk/1.0"
        };

        // RFC 3261 §8.2.6.2: Record-Route MUST be copied verbatim from request to response.
        var recordRoute = request.Header("Record-Route");
        if (!string.IsNullOrWhiteSpace(recordRoute))
            headers["Record-Route"] = recordRoute;

        return EnsureToTag(headers, statusCode);
    }

    /// <summary>
    /// Ensures To tag presence for non-100 UAS responses.
    /// </summary>
    public static Dictionary<string, string> EnsureToTag(
        IReadOnlyDictionary<string, string> headers,
        int statusCode)
    {
        var mutable = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        if (statusCode <= 100)
            return mutable;

        var currentTo = mutable.TryGetValue("To", out var toHeaderValue)
            ? toHeaderValue
            : string.Empty;
        mutable["To"] = SipCallSessionHeaderService.EnsureTag(currentTo, SipProtocol.NewTag());
        return mutable;
    }
}
