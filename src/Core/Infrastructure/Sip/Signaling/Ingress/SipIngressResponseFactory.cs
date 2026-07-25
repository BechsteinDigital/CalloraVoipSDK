using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Builds minimal SIP response headers for ingress-level replies (early validation, out-of-dialog
/// requests). Stateless helpers extracted from the signaling service so the response-header rules
/// (Via rport reflection, To-tag generation, dialog-scope classification) live in one focused place.
/// </summary>
internal static class SipIngressResponseFactory
{
    /// <summary>Creates minimal response headers for an ingress-level reply.</summary>
    public static Dictionary<string, string> CreateIngressResponseHeaders(
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

        return EnsureIngressResponseToTag(headers, statusCode);
    }

    /// <summary>Ensures To-tag presence for non-100 UAS responses (RFC 3261 §8.2.6.2).</summary>
    public static Dictionary<string, string> EnsureIngressResponseToTag(
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

    /// <summary>Returns true when the method semantically requires an existing SIP dialog.</summary>
    public static bool IsDialogScopedMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized is "BYE" or "INFO" or "UPDATE" or "PRACK" or "REFER" or "NOTIFY" or "SUBSCRIBE";
    }
}
