using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Builds SIP dialog headers for requests and responses.
/// </summary>
internal sealed class SipCallSessionHeaderService
{
    private readonly ISipCallSessionContext _context;

    /// <summary>
    /// Creates a new header service for one call session context.
    /// </summary>
    public SipCallSessionHeaderService(ISipCallSessionContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Creates dialog request headers for INVITE/BYE/ACK/CANCEL.
    /// </summary>
    public Dictionary<string, string> CreateDialogRequestHeaders(
        string method,
        int cseq,
        string branch,
        string? authorizationHeaderName,
        string? authorizationHeader,
        bool includeContentType)
    {
        if (string.IsNullOrWhiteSpace(_context.LocalTag))
            throw new InvalidOperationException("Local tag is missing.");

        var localEndPoint = _context.Transport.GetLocalEndPoint(_context.SignalingTransport);
        var advertisedLocalEndPoint = LocalEndPointAdvertisementResolver.ResolveAdvertisedLocalEndPoint(
            localEndPoint,
            _context.RemoteEndPoint);
        var requiresSecureContact = RequiresSecureContact(method);
        var localUser = SipProtocol.TryParseSipUri(_context.LocalUri, out var parsedUser, out _, out _)
            ? parsedUser
            : "user";
        var contactUri = SipSignalingFormat.BuildContactUri(
            localUser,
            advertisedLocalEndPoint,
            _context.SignalingTransport,
            forceSecureScheme: requiresSecureContact);

        var toHeader = SipProtocol.FormatNameAddr(displayName: null, _context.RemoteUri);
        if (!string.IsNullOrWhiteSpace(_context.RemoteTag))
            toHeader = $"{toHeader};tag={_context.RemoteTag}";

        // RFC 3323 §4.1: when Privacy: id is set, the From header MUST be anonymized.
        var fromHeader = IsPrivacyIdRequested(_context.PrivacyHeader)
            ? SipProtocol.FormatNameAddr("Anonymous", "sip:anonymous@anonymous.invalid", _context.LocalTag ?? string.Empty)
            : SipProtocol.FormatNameAddr(_context.LocalDisplayName, _context.LocalUri, _context.LocalTag);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = SipSignalingFormat.BuildVia(advertisedLocalEndPoint, branch, _context.SignalingTransport),
            ["Max-Forwards"] = "70",
            ["From"] = fromHeader,
            ["To"] = toHeader,
            ["Call-ID"] = _context.CallId,
            ["CSeq"] = $"{cseq} {method}",
            ["Contact"] = $"<{contactUri}>",
            ["Supported"] = "100rel, timer, replaces",
            ["User-Agent"] = _context.UserAgent,
            ["X-CalloraVoipSdk-Trace-Id"] = _context.CallId
        };

        if (!string.IsNullOrWhiteSpace(authorizationHeader)
            && !string.IsNullOrWhiteSpace(authorizationHeaderName))
        {
            headers[authorizationHeaderName] = authorizationHeader;
        }

        if (_context.RouteSet.Count > 0)
            headers["Route"] = string.Join(", ", _context.RouteSet.Select(uri => $"<{uri}>"));

        if (!string.IsNullOrWhiteSpace(_context.RequireHeader))
            headers["Require"] = _context.RequireHeader!;

        if (!string.IsNullOrWhiteSpace(_context.ProxyRequireHeader))
            headers["Proxy-Require"] = _context.ProxyRequireHeader!;

        if (method.Equals("INVITE", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_context.PreferredIdentityUri))
        {
            headers["P-Preferred-Identity"] = SipAssertedIdentityHeader.FormatIdentityValue(_context.PreferredIdentityUri);
        }

        if (method.Equals("INVITE", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_context.PrivacyHeader))
        {
            headers["Privacy"] = _context.PrivacyHeader.Trim();
        }

        if (method.Equals("INVITE", StringComparison.Ordinal))
            headers["Accept"] = "application/sdp";

        if (method.Equals("INVITE", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(_context.ReferredBy))
        {
            headers["Referred-By"] = _context.ReferredBy!;
        }

        if (includeContentType)
        {
            headers["Content-Type"] = "application/sdp";
            headers["Content-Disposition"] = "session";
        }

        return headers;
    }

    /// <summary>
    /// Returns true when RFC3261 requires a SIPS Contact URI for the outbound request.
    /// </summary>
    private bool RequiresSecureContact(string method)
    {
        if (!method.Equals("INVITE", StringComparison.Ordinal))
            return false;

        if (SipProtocol.IsSipsUri(_context.RemoteRequestUri))
            return true;

        if (_context.RouteSet.Count == 0)
            return false;

        return SipProtocol.IsSipsUri(_context.RouteSet[0]);
    }

    /// <summary>
    /// Creates response headers from inbound request while ensuring local To tag.
    /// </summary>
    public Dictionary<string, string> CreateResponseHeadersFromRequest(
        SipRequest request,
        string localTag,
        bool includeContentType)
    {
        var localEndPoint = _context.Transport.GetLocalEndPoint(_context.SignalingTransport);
        var advertisedLocalEndPoint = LocalEndPointAdvertisementResolver.ResolveAdvertisedLocalEndPoint(
            localEndPoint,
            _context.RemoteEndPoint);
        var localUser = SipProtocol.TryParseSipUri(_context.LocalUri, out var parsedUser, out _, out _)
            ? parsedUser
            : "user";
        var contactUri = SipSignalingFormat.BuildContactUri(
            localUser,
            advertisedLocalEndPoint,
            _context.SignalingTransport,
            forceSecureScheme: false,
            advertisedHost: _context.AdvertisedPublicHost,
            advertisedPort: _context.AdvertisedPublicPort);
        var toHeader = EnsureTag(request.Header("To"), localTag);

        // RFC 3581 §4: reflect rport/received into the top Via of responses to inbound requests.
        var viaValue = SipProtocol.ReflectViaRport(request.Header("Via"), _context.RemoteEndPoint);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = viaValue,
            ["From"] = request.Header("From") ?? string.Empty,
            ["To"] = toHeader,
            ["Call-ID"] = request.Header("Call-ID") ?? string.Empty,
            ["CSeq"] = request.Header("CSeq") ?? string.Empty,
            ["Contact"] = $"<{contactUri}>",
            ["Supported"] = "100rel, timer, replaces",
            ["Server"] = _context.UserAgent,
            ["Date"] = DateTimeOffset.UtcNow.ToString("r"),
            ["X-CalloraVoipSdk-Trace-Id"] =
                request.Header("X-CalloraVoipSdk-Trace-Id")
                ?? request.Header("Call-ID")
                ?? _context.CallId
        };

        if (includeContentType)
        {
            headers["Content-Type"] = "application/sdp";
            headers["Content-Disposition"] = "session";
        }

        return headers;
    }

    /// <summary>
    /// Returns a To header value that always includes a tag parameter.
    /// </summary>
    public static string EnsureTag(string? headerValue, string tag)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return $";tag={tag}";
        if (!string.IsNullOrWhiteSpace(SipProtocol.ExtractTag(headerValue)))
            return headerValue;
        return $"{headerValue};tag={tag}";
    }

    /// <summary>
    /// Returns true when the Privacy header contains the <c>id</c> token (RFC 3323 §4.1).
    /// </summary>
    private static bool IsPrivacyIdRequested(string? privacyHeader)
    {
        if (string.IsNullOrWhiteSpace(privacyHeader))
            return false;

        foreach (var token in privacyHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("id", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
