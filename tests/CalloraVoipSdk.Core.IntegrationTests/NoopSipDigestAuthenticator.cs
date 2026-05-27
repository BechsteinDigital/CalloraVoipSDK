using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;

namespace CalloraVoipSdk.Core.IntegrationTests;

internal sealed class NoopSipDigestAuthenticator : ISipDigestAuthenticator
{
    public bool TryCreateAuthorizationHeader(
        string? challengeHeader,
        string username,
        string password,
        string method,
        string requestUri,
        int nonceCount,
        out string authorizationHeader)
    {
        authorizationHeader = string.Empty;
        return false;
    }
}
