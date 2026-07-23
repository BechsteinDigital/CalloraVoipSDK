using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Turn.Server;

/// <summary>
/// Validates TURN requests against long-term auth requirements. Send/Data indications are
/// never authenticated (RFC 5766/8656 §10) and are therefore not handled here.
/// </summary>
internal sealed class TurnServerRequestAuthenticator
{
    private readonly TurnServerOptions _options;
    private readonly TurnAuthOptions? _authOptions;
    private readonly IStunMessageCodec _codec;
    private readonly TurnServerResponseFactory _responseFactory;

    /// <summary>
    /// Creates an authenticator for the active TURN server options.
    /// </summary>
    public TurnServerRequestAuthenticator(
        TurnServerOptions options,
        TurnAuthOptions? authOptions,
        IStunMessageCodec codec,
        TurnServerResponseFactory responseFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(responseFactory);

        _options = options;
        _authOptions = authOptions;
        _codec = codec;
        _responseFactory = responseFactory;
    }

    /// <summary>
    /// Authenticates a TURN request and returns challenge response when required.
    /// </summary>
    public bool TryAuthenticateRequest(
        StunMessage request,
        ReadOnlySpan<byte> rawRequest,
        out StunCredentials? authenticatedCredentials,
        out StunMessage? challengeResponse)
    {
        ArgumentNullException.ThrowIfNull(request);

        authenticatedCredentials = null;
        challengeResponse = null;

        if (!_options.RequireAuthentication)
            return true;

        if (_authOptions is null)
        {
            challengeResponse = _responseFactory.BuildErrorResponse(
                request,
                500,
                "Server Error",
                includeAuthAttributes: false);
            return false;
        }

        var username = request.Attributes.OfType<UsernameAttribute>().FirstOrDefault()?.Value;
        var realm = request.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
        var nonce = request.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;
        bool hasMi = request.Attributes.Any(attribute => attribute.AttributeType == StunAttributeType.MessageIntegrity);

        if (string.IsNullOrWhiteSpace(username) || !hasMi)
        {
            challengeResponse = _responseFactory.BuildUnauthorizedResponse(request, _authOptions.Realm);
            return false;
        }

        var realmForLookup = string.IsNullOrWhiteSpace(realm) ? _authOptions.Realm : realm;
        if (!_authOptions.CredentialProvider.TryGetCredentials(username, realmForLookup, out var credentials))
        {
            challengeResponse = _responseFactory.BuildUnauthorizedResponse(request, realmForLookup);
            return false;
        }

        if (!credentials.IsLongTerm)
        {
            challengeResponse = _responseFactory.BuildUnauthorizedResponse(request, realmForLookup);
            return false;
        }

        var expectedRealm = credentials.Realm ?? realmForLookup;
        if (!string.Equals(realm, expectedRealm, StringComparison.Ordinal))
        {
            challengeResponse = _responseFactory.BuildUnauthorizedResponse(request, expectedRealm);
            return false;
        }

        if (string.IsNullOrWhiteSpace(nonce) || !_authOptions.NonceManager.IsNonceValid(nonce))
        {
            challengeResponse = _responseFactory.BuildStaleNonceResponse(request, expectedRealm);
            return false;
        }

        var resolved = credentials.WithRealmAndNonce(expectedRealm, nonce);
        var key = resolved.DeriveHmacKey();
        if (!_codec.VerifyIntegrity(rawRequest, key))
        {
            challengeResponse = _responseFactory.BuildUnauthorizedResponse(request, expectedRealm);
            return false;
        }

        authenticatedCredentials = resolved;
        return true;
    }
}
