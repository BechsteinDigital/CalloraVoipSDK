using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Ingress-level SIP request policy checks shared by stateless UAS fallback handling.
/// </summary>
internal static class SipIngressRequestPolicy
{
    /// <summary>
    /// Detects signaling transport from the top Via header.
    /// </summary>
    public static SipTransportProtocol DetectTransportFromVia(string? viaHeader)
    {
        if (string.IsNullOrWhiteSpace(viaHeader))
            return SipTransportProtocol.Udp;

        var marker = "SIP/2.0/";
        var start = viaHeader.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return SipTransportProtocol.Udp;

        var tail = viaHeader[(start + marker.Length)..];
        var tokenEnd = tail.IndexOfAny([' ', ';']);
        var token = (tokenEnd >= 0 ? tail[..tokenEnd] : tail).Trim();
        return token.ToUpperInvariant() switch
        {
            "TCP" => SipTransportProtocol.Tcp,
            "TLS" => SipTransportProtocol.Tls,
            "WS" => SipTransportProtocol.Ws,
            "WSS" => SipTransportProtocol.Wss,
            _ => SipTransportProtocol.Udp
        };
    }

    /// <summary>
    /// Validates ingress request framing semantics required before dialog or transaction dispatch.
    /// </summary>
    public static bool TryValidateIngressRequest(
        SipRequest request,
        out int rejectionCode,
        out string rejectionReasonPhrase)
    {
        rejectionCode = 0;
        rejectionReasonPhrase = string.Empty;

        if (string.IsNullOrWhiteSpace(request.Header("Via"))
            || string.IsNullOrWhiteSpace(request.Header("From"))
            || string.IsNullOrWhiteSpace(request.Header("To"))
            || string.IsNullOrWhiteSpace(request.Header("Call-ID"))
            || string.IsNullOrWhiteSpace(request.Header("CSeq")))
        {
            rejectionCode = 400;
            rejectionReasonPhrase = "Bad Request";
            return false;
        }

        var cseqMethod = SipProtocol.ExtractCSeqMethod(request.Header("CSeq"));
        if (!string.Equals(cseqMethod, request.Method, StringComparison.Ordinal))
        {
            rejectionCode = 400;
            rejectionReasonPhrase = "Bad Request";
            return false;
        }

        if (SipProtocol.TryParseSipUri(request.RequestUri, out _, out _, out _))
            return true;

        if (IsUnsupportedUriScheme(request.RequestUri))
        {
            rejectionCode = 416;
            rejectionReasonPhrase = "Unsupported URI Scheme";
            return false;
        }

        rejectionCode = 400;
        rejectionReasonPhrase = "Bad Request";
        return false;
    }

    /// <summary>
    /// Returns true when the Via chain shows one of our own locally generated branches
    /// <b>below</b> the top Via — i.e. a request we previously forwarded has spiraled back to us.
    /// The top-most Via belongs to the immediate previous hop (the sender), so its branch is never
    /// evidence that we already handled the request: rejecting on it would break legitimate direct
    /// requests from another SDK instance that shares this process's branch prefix (e.g. two
    /// in-process endpoints on loopback). Genuine spirals always carry our branch beneath a newer
    /// hop's Via, so skipping only the top entry preserves loop protection.
    /// </summary>
    public static bool IsLoopDetected(SipRequest request)
    {
        var viaHeader = request.Header("Via");
        if (string.IsNullOrWhiteSpace(viaHeader))
            return false;

        var isTopVia = true;
        foreach (var viaEntry in viaHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (isTopVia)
            {
                // The top Via is the immediate sender's own branch, not a hop we inserted.
                isTopVia = false;
                continue;
            }

            var marker = "branch=";
            var markerIndex = viaEntry.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                continue;

            var branchTail = viaEntry[(markerIndex + marker.Length)..];
            var endIndex = branchTail.IndexOfAny([';', ' ', '\t', ',']);
            var branch = endIndex >= 0 ? branchTail[..endIndex] : branchTail;
            if (SipProtocol.IsLocalBranch(branch))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validates Max-Forwards for inbound request processing.
    /// </summary>
    public static bool TryValidateMaxForwards(
        SipRequest request,
        out int rejectionCode,
        out string rejectionReasonPhrase)
    {
        rejectionCode = 0;
        rejectionReasonPhrase = string.Empty;
        var maxForwardsHeader = request.Header("Max-Forwards");
        if (string.IsNullOrWhiteSpace(maxForwardsHeader))
            return true;
        if (!int.TryParse(maxForwardsHeader.Trim(), out var maxForwards))
        {
            rejectionCode = 400;
            rejectionReasonPhrase = "Bad Request";
            return false;
        }

        if (maxForwards <= 0)
        {
            rejectionCode = 483;
            rejectionReasonPhrase = "Too Many Hops";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Decrements Max-Forwards when header is present.
    /// </summary>
    public static SipRequest DecrementMaxForwardsIfPresent(SipRequest request)
    {
        var maxForwardsHeader = request.Header("Max-Forwards");
        if (string.IsNullOrWhiteSpace(maxForwardsHeader))
            return request;
        if (!int.TryParse(maxForwardsHeader.Trim(), out var maxForwards))
            return request;

        var updatedHeaders = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Max-Forwards"] = Math.Max(0, maxForwards - 1).ToString()
        };
        return new SipRequest(request.Method, request.RequestUri, updatedHeaders, request.Body);
    }

    /// <summary>
    /// Returns true when request URI carries a non-SIP scheme.
    /// </summary>
    private static bool IsUnsupportedUriScheme(string requestUri)
    {
        if (string.IsNullOrWhiteSpace(requestUri))
            return false;

        var trimmed = requestUri.Trim();
        if (trimmed.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Contains(':', StringComparison.Ordinal);
    }
}
