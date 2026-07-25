using System.Net;
using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// RFC3261-oriented helper routines for outbound INVITE retry and redirect handling.
/// </summary>
internal static class SipOutboundInviteRetryPolicy
{
    /// <summary>
    /// Creates one synthetic final-response exception for transaction-layer timeout or transport errors.
    /// </summary>
    public static SipFinalResponseException CreateSyntheticTransactionFailure(
        int statusCode,
        string reasonPhrase,
        string callId,
        Exception innerException)
    {
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Call-ID"] = callId
        };
        var response = new SipResponse(statusCode, reasonPhrase, responseHeaders, body: string.Empty);
        var envelope = new SipResponseEnvelope(new IPEndPoint(IPAddress.None, 0), response);
        return new SipFinalResponseException(
            $"INVITE transaction failed with synthetic status {statusCode} {reasonPhrase}.",
            envelope,
            innerException);
    }

    /// <summary>
    /// Returns true when an error represents a SIP transaction transport-layer failure — a faulted send/
    /// retransmission (<see cref="SipTransactionTransportException"/>), which warrants candidate failover and a
    /// synthetic 503. Any other <see cref="InvalidOperationException"/> (e.g. a state or negotiation error that
    /// merely carries an inner exception, such as a failed PRACK) is not a transport failure and must propagate.
    /// </summary>
    public static bool IsTransportFailure(InvalidOperationException exception) =>
        exception is SipTransactionTransportException;

    /// <summary>
    /// Enqueues Contact URIs from one redirect response while suppressing duplicates. The total number of
    /// distinct targets (initial plus all redirects) is bounded by <paramref name="maxTargets"/> so a malicious
    /// or misbehaving 3xx carrying many Contacts cannot fan out into an unbounded chain of INVITE transactions.
    /// </summary>
    public static void EnqueueRedirectTargets(
        SipResponse response,
        Queue<SipOutboundInviteTarget> pendingTargets,
        HashSet<string> visitedRequestUris,
        int maxTargets)
    {
        foreach (var contactRow in response.HeaderValues("Contact"))
        {
            foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(contactRow))
            {
                if (visitedRequestUris.Count >= maxTargets)
                    return;
                var uri = SipProtocol.ExtractUriFromNameAddr(token);
                if (string.IsNullOrWhiteSpace(uri))
                    continue;
                if (!SipProtocol.TryParseSipUri(uri, out _, out _, out _))
                    continue;
                if (!visitedRequestUris.Add(uri))
                    continue;

                pendingTargets.Enqueue(new SipOutboundInviteTarget(
                    RequestUri: uri,
                    LogicalRemoteUri: uri,
                    RouteSet: [],
                    NextHopUri: uri));
            }
        }
    }

    /// <summary>
    /// Converts a SIPS URI to SIP for 416 retry handling.
    /// </summary>
    public static bool TryDowngradeSipsToSip(string requestUri, out string sipUri)
    {
        sipUri = string.Empty;
        if (!requestUri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return false;

        sipUri = $"sip:{requestUri[5..]}";
        return SipProtocol.TryParseSipUri(sipUri, out _, out _, out _);
    }

    /// <summary>
    /// Removes Unsupported option tags from Require and Proxy-Require headers for 420 retries.
    /// </summary>
    public static bool TryRemoveUnsupportedOptions(
        string? unsupportedHeader,
        string? requireHeader,
        string? proxyRequireHeader,
        out string? nextRequireHeader,
        out string? nextProxyRequireHeader)
    {
        nextRequireHeader = requireHeader;
        nextProxyRequireHeader = proxyRequireHeader;

        if (string.IsNullOrWhiteSpace(unsupportedHeader))
            return false;

        var unsupported = unsupportedHeader
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim().Trim('\"'))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unsupported.Count == 0)
            return false;

        var changed = false;
        if (!string.IsNullOrWhiteSpace(requireHeader))
        {
            var filteredRequire = FilterOptionTokens(requireHeader, unsupported);
            if (!string.Equals(filteredRequire, requireHeader, StringComparison.Ordinal))
            {
                nextRequireHeader = filteredRequire;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(proxyRequireHeader))
        {
            var filteredProxyRequire = FilterOptionTokens(proxyRequireHeader, unsupported);
            if (!string.Equals(filteredProxyRequire, proxyRequireHeader, StringComparison.Ordinal))
            {
                nextProxyRequireHeader = filteredProxyRequire;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Filters comma-separated option-tag lists by removing unsupported tokens.
    /// </summary>
    private static string? FilterOptionTokens(string value, ISet<string> unsupportedTokens)
    {
        var filtered = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !unsupportedTokens.Contains(token))
            .ToArray();
        return filtered.Length == 0 ? null : string.Join(", ", filtered);
    }
}
