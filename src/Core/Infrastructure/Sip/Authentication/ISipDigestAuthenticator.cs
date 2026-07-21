namespace CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;

/// <summary>
/// Contract for SIP Digest Authorization header generation.
/// </summary>
internal interface ISipDigestAuthenticator
{
    /// <summary>
    /// Attempts to build an Authorization header from a Digest challenge. When the server offers only
    /// <c>qop="auth-int"</c>, the request entity body is folded into the digest (RFC 7616 §3.4.3), so pass
    /// <paramref name="body"/> for requests that carry one (e.g. an INVITE's SDP); it is otherwise ignored.
    /// </summary>
    /// <param name="body">The request entity body for <c>qop=auth-int</c>, or <see langword="null"/>/empty.</param>
    bool TryCreateAuthorizationHeader(
        string? challengeHeader,
        string username,
        string password,
        string method,
        string requestUri,
        int nonceCount,
        out string authorizationHeader,
        string? body = null);
}
