using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Server;

/// <summary>
/// RFC 5389-compliant handler for STUN Binding Requests.
/// <para>
/// Processing order per RFC 5389 §7.3.1:
/// 1. Reject non-Binding methods with 400 Bad Request.
/// 2. Detect unknown comprehension-required attributes; respond with 420 Unknown Attribute.
/// 3. When credentials are configured: verify authentication attributes and MESSAGE-INTEGRITY.
///    Long-term mode validates REALM/NONCE and returns 438 Stale Nonce for unknown/expired nonce values.
/// 4. Build Binding Success Response with XOR-MAPPED-ADDRESS of the sender.
/// </para>
/// </summary>
internal sealed class StunBindingRequestHandler : IStunRequestHandler
{
    private readonly IStunMessageCodec                  _codec;
    private readonly IStunCredentialProvider?           _credentialProvider;
    private readonly StunCredentials?                   _fallbackCredentials;
    private readonly IStunNonceManager?                 _nonceManager;
    private readonly string?                            _defaultRealm;
    private readonly StunThirdPartyAuthorizationOptions? _thirdPartyAuthorization;
    private readonly ILogger<StunBindingRequestHandler> _logger;

    /// <summary>
    /// Initialises a handler that processes Binding Requests <b>without authentication</b>.
    /// <para>
    /// Every Binding Request is answered regardless of origin — appropriate only for a public STUN
    /// endpoint on a trusted network. Where authentication is expected this is a spoofing risk, so
    /// selecting this constructor must be a deliberate choice; construction emits a warning log.
    /// </para>
    /// </summary>
    public StunBindingRequestHandler(
        IStunMessageCodec                  codec,
        ILogger<StunBindingRequestHandler> logger)
        : this(
            codec,
            credentialProvider: null,
            fallbackCredentials: null,
            defaultRealm: null,
            nonceManager: null,
            thirdPartyAuthorization: null,
            logger) { }

    /// <summary>
    /// Initialises a handler that validates MESSAGE-INTEGRITY using the supplied credentials.
    /// </summary>
    public StunBindingRequestHandler(
        IStunMessageCodec                  codec,
        StunCredentials?                   credentials,
        ILogger<StunBindingRequestHandler> logger)
        : this(codec, credentials, nonceManager: null, logger) { }

    /// <summary>
    /// Initialises a handler with optional nonce manager support for long-term credential flow.
    /// When long-term credentials are used, <paramref name="nonceManager"/> should be provided
    /// to issue and validate challenge nonces dynamically.
    /// </summary>
    public StunBindingRequestHandler(
        IStunMessageCodec                  codec,
        StunCredentials?                   credentials,
        IStunNonceManager?                 nonceManager,
        ILogger<StunBindingRequestHandler> logger)
        : this(
            codec,
            credentialProvider: credentials is null ? null : new InMemoryStunCredentialProvider([credentials]),
            fallbackCredentials: credentials,
            defaultRealm: credentials?.Realm,
            nonceManager: nonceManager,
            thirdPartyAuthorization: null,
            logger)
    {
        if (credentials?.IsLongTerm == true
            && nonceManager is null
            && string.IsNullOrWhiteSpace(credentials.Nonce))
        {
            throw new ArgumentException(
                "Long-term STUN credentials require either a static Nonce or an IStunNonceManager.",
                nameof(credentials));
        }
    }

    /// <summary>
    /// Initialises a handler that resolves credentials per USERNAME/REALM via provider lookup.
    /// This constructor enables multi-user STUN authentication. Passing a null
    /// <paramref name="credentialProvider"/> with no third-party authorization yields an
    /// unauthenticated handler — see the no-authentication constructor's remarks.
    /// </summary>
    public StunBindingRequestHandler(
        IStunMessageCodec                  codec,
        IStunCredentialProvider?           credentialProvider,
        string?                            defaultRealm,
        IStunNonceManager?                 nonceManager,
        StunThirdPartyAuthorizationOptions? thirdPartyAuthorization,
        ILogger<StunBindingRequestHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        if (thirdPartyAuthorization is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(thirdPartyAuthorization.ServerName);
            ArgumentNullException.ThrowIfNull(thirdPartyAuthorization.AccessTokenValidator);
        }

        _codec                   = codec;
        _credentialProvider      = credentialProvider;
        _fallbackCredentials     = null;
        _defaultRealm            = string.IsNullOrWhiteSpace(defaultRealm) ? null : defaultRealm;
        _nonceManager            = nonceManager;
        _thirdPartyAuthorization = thirdPartyAuthorization;
        _logger                  = logger;

        WarnIfUnauthenticated();
    }

    private StunBindingRequestHandler(
        IStunMessageCodec                  codec,
        IStunCredentialProvider?           credentialProvider,
        StunCredentials?                   fallbackCredentials,
        string?                            defaultRealm,
        IStunNonceManager?                 nonceManager,
        StunThirdPartyAuthorizationOptions? thirdPartyAuthorization,
        ILogger<StunBindingRequestHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);

        if (thirdPartyAuthorization is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(thirdPartyAuthorization.ServerName);
            ArgumentNullException.ThrowIfNull(thirdPartyAuthorization.AccessTokenValidator);
        }

        _codec                   = codec;
        _credentialProvider      = credentialProvider;
        _fallbackCredentials     = fallbackCredentials;
        _defaultRealm            = string.IsNullOrWhiteSpace(defaultRealm) ? null : defaultRealm;
        _nonceManager            = nonceManager;
        _thirdPartyAuthorization = thirdPartyAuthorization;
        _logger                  = logger;

        WarnIfUnauthenticated();
    }

    // Surfaces a misconfiguration where the handler was built without any authentication mechanism:
    // Binding is then answered for every sender. That is valid for a public STUN endpoint but a
    // spoofing risk if authentication was intended, so the choice is logged once at construction.
    private void WarnIfUnauthenticated()
    {
        if (_credentialProvider is null
            && _fallbackCredentials is null
            && _thirdPartyAuthorization is null)
        {
            _logger.LogWarning(
                "STUN Binding handler constructed WITHOUT authentication: every Binding Request is answered " +
                "unauthenticated. Ensure this is intentional (public STUN) and not a missing-credentials misconfiguration.");
        }
    }

    /// <inheritdoc />
    public StunRequestHandlingResult? Handle(StunMessage request, ReadOnlySpan<byte> rawRequest, IPEndPoint sender)
    {
        // RFC 7635 §7: ACCESS-TOKEN is unexpected unless third-party authorization
        // was previously advertised by this server.
        var accessToken = request.Attributes.OfType<AccessTokenAttribute>().FirstOrDefault();
        if (_thirdPartyAuthorization is null && accessToken is not null)
        {
            _logger.LogWarning(
                "STUN 420 Unknown Attribute — ACCESS-TOKEN without third-party mode from {Sender}",
                sender);
            return BuildResult(
                BuildError(
                    request,
                    420,
                    "Unknown Attribute",
                    new UnknownAttributesAttribute
                    {
                        UnknownTypeCodes = [(ushort)StunAttributeType.AccessToken]
                    }),
                responseIntegrityKey: null);
        }

        byte[]? thirdPartyResponseIntegrityKey = null;
        if (!TryValidateThirdPartyAuthorization(
                request,
                rawRequest,
                sender,
                out var validatedThirdPartyMacKey,
                out var thirdPartyErrorResponse))
        {
            return BuildResult(thirdPartyErrorResponse!, responseIntegrityKey: null);
        }

        thirdPartyResponseIntegrityKey = validatedThirdPartyMacKey;

        // Only the Binding method is handled here.
        if (request.MessageMethod != StunMessageMethod.Binding)
        {
            _logger.LogWarning("STUN server received unsupported method {Method} from {Sender}", request.MessageMethod, sender);
            return BuildResult(BuildError(request, 400, "Bad Request"), thirdPartyResponseIntegrityKey);
        }

        // Detect unknown comprehension-required attributes (RFC 5389 §7.3.1).
        var unknownRequired = request.Attributes
            .OfType<UnknownRawAttribute>()
            .Where(a => a.RawAttributeType < 0x8000) // comprehension-required range
            .Select(a => a.RawAttributeType)
            .ToList();

        if (unknownRequired.Count > 0)
        {
            _logger.LogWarning(
                "STUN 420 Unknown Attribute from {Sender}: unknown types [{Types}]",
                sender,
                string.Join(", ", unknownRequired.Select(t => $"0x{t:X4}")));

            return BuildResult(
                BuildError(
                    request,
                    420,
                    "Unknown Attribute",
                    new UnknownAttributesAttribute { UnknownTypeCodes = unknownRequired }),
                thirdPartyResponseIntegrityKey);
        }

        // Validate MESSAGE-INTEGRITY when credentials are configured.
        if ((_credentialProvider is not null || _fallbackCredentials is not null)
            && _thirdPartyAuthorization is null)
        {
            var hasMi = request.Attributes.Any(a => a.AttributeType == StunAttributeType.MessageIntegrity);
            if (!hasMi)
            {
                _logger.LogWarning("STUN 401 Unauthorized — missing MESSAGE-INTEGRITY from {Sender}", sender);
                return BuildResult(
                    BuildUnauthenticatedChallenge(request, realmOverride: _defaultRealm),
                    thirdPartyResponseIntegrityKey);
            }

            if (!TryResolveCredentials(request, out var resolvedCredentials))
            {
                _logger.LogWarning(
                    "STUN 401 Unauthorized — no credential entry for USERNAME/REALM from {Sender}",
                    sender);
                var requestRealm = request.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
                return BuildResult(
                    BuildUnauthenticatedChallenge(request, realmOverride: requestRealm ?? _defaultRealm),
                    thirdPartyResponseIntegrityKey);
            }

            if (resolvedCredentials.IsLongTerm)
            {
                var username = request.Attributes.OfType<UsernameAttribute>().FirstOrDefault()?.Value;
                var realm    = request.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
                var nonce    = request.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;

                if (!string.Equals(username, resolvedCredentials.Username, StringComparison.Ordinal)
                    || !string.Equals(realm, resolvedCredentials.Realm, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "STUN 401 Unauthorized — invalid USERNAME/REALM from {Sender}",
                        sender);
                    return BuildResult(
                        BuildUnauthenticatedChallenge(request, realmOverride: resolvedCredentials.Realm),
                        thirdPartyResponseIntegrityKey);
                }

                if (!IsNonceValid(nonce))
                {
                    _logger.LogWarning("STUN 438 Stale Nonce from {Sender}", sender);
                    return BuildResult(
                        BuildStaleNonce(request, realmOverride: resolvedCredentials.Realm),
                        thirdPartyResponseIntegrityKey);
                }
            }

            try
            {
                var key = resolvedCredentials.DeriveHmacKey();
                if (!_codec.VerifyIntegrity(rawRequest, key))
                {
                    _logger.LogWarning("STUN 401 Unauthorized — HMAC mismatch from {Sender}", sender);
                    return BuildResult(
                        BuildUnauthenticatedChallenge(request, realmOverride: resolvedCredentials.Realm ?? _defaultRealm),
                        thirdPartyResponseIntegrityKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "STUN credential verification threw unexpectedly for {Sender}", sender);
                return BuildResult(BuildError(request, 500, "Server Error"), thirdPartyResponseIntegrityKey);
            }
        }

        // Success: include XOR-MAPPED-ADDRESS of the sender.
        _logger.LogDebug("STUN Binding Response → {Sender}", sender);
        return BuildResult(
            StunMessage.CreateBindingResponse(
                request.TransactionId,
                [new XorMappedAddressAttribute { EndPoint = sender }]),
            thirdPartyResponseIntegrityKey);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 401 Unauthorized challenge including REALM and NONCE attributes
    /// when long-term credentials are configured, or a plain 401 for short-term.
    /// </summary>
    private StunMessage BuildUnauthenticatedChallenge(StunMessage request, string? realmOverride)
        => BuildChallengeResponse(
            request,
            code: 401,
            reason: "Unauthorized",
            realmOverride,
            includeThirdPartyAuthorization: false);

    /// <summary>
    /// Builds a 438 Stale Nonce challenge including a fresh NONCE for long-term credentials.
    /// </summary>
    private StunMessage BuildStaleNonce(StunMessage request, string? realmOverride)
        => BuildChallengeResponse(
            request,
            code: 438,
            reason: "Stale Nonce",
            realmOverride,
            includeThirdPartyAuthorization: false);

    /// <summary>
    /// Builds a 401 challenge advertising RFC 7635 third-party authorization support.
    /// </summary>
    private StunMessage BuildThirdPartyChallenge(StunMessage request)
        => BuildChallengeResponse(
            request,
            code: 401,
            reason: "Unauthorized",
            realmOverride: _defaultRealm,
            includeThirdPartyAuthorization: true);

    /// <summary>
    /// Builds an authentication challenge response and includes REALM/NONCE for long-term mode.
    /// </summary>
    private StunMessage BuildChallengeResponse(
        StunMessage request,
        int code,
        string reason,
        string? realmOverride,
        bool includeThirdPartyAuthorization)
    {
        var attrs = new List<StunAttribute>();

        if (includeThirdPartyAuthorization)
        {
            attrs.Add(new ThirdPartyAuthorizationAttribute
            {
                ServerName = _thirdPartyAuthorization!.ServerName
            });
        }

        var challengeRealm = string.IsNullOrWhiteSpace(realmOverride) ? null : realmOverride;
        if (!string.IsNullOrWhiteSpace(challengeRealm))
        {
            attrs.Add(new RealmAttribute { Value = challengeRealm });
        }

        // RFC 7635 recommends issuing NONCE in 401 responses.
        // In first-party mode NONCE is only added when long-term challenge data exists.
        if (includeThirdPartyAuthorization || challengeRealm is not null)
        {
            var nonce = includeThirdPartyAuthorization
                ? IssueNonce() ?? GenerateEphemeralNonce()
                : IssueNonce();
            if (!string.IsNullOrWhiteSpace(nonce))
                attrs.Add(new NonceAttribute { Value = nonce });
        }

        attrs.Add(new ErrorCodeAttribute { Code = code, Reason = reason });
        return BuildErrorResponse(request, attrs);
    }

    /// <summary>
    /// Returns true when the incoming nonce is valid for long-term credential requests.
    /// </summary>
    private bool IsNonceValid(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return false;

        if (_nonceManager is not null)
            return _nonceManager.IsNonceValid(nonce);

        return string.Equals(nonce, _fallbackCredentials?.Nonce, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issues a nonce for a challenge response, either from nonce manager or static credentials.
    /// </summary>
    private string? IssueNonce()
    {
        if (_nonceManager is not null)
            return _nonceManager.GenerateNonce();

        return _fallbackCredentials?.Nonce;
    }

    /// <summary>
    /// Generates an ephemeral nonce for RFC 7635 challenges when no nonce manager is configured.
    /// </summary>
    private static string GenerateEphemeralNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Resolves credentials for the current request using provider lookup,
    /// with legacy single-credential fallback for backward compatibility.
    /// </summary>
    private bool TryResolveCredentials(StunMessage request, out StunCredentials credentials)
    {
        var username = request.Attributes.OfType<UsernameAttribute>().FirstOrDefault()?.Value;
        var realm    = request.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;

        if (!string.IsNullOrWhiteSpace(username)
            && _credentialProvider is not null
            && _credentialProvider.TryGetCredentials(username, realm, out credentials))
        {
            return true;
        }

        if (_fallbackCredentials is not null)
        {
            credentials = _fallbackCredentials;
            return true;
        }

        credentials = null!;
        return false;
    }

    /// <summary>
    /// Validates RFC 7635 third-party authorization when configured.
    /// </summary>
    private bool TryValidateThirdPartyAuthorization(
        StunMessage request,
        ReadOnlySpan<byte> rawRequest,
        IPEndPoint sender,
        out byte[]? responseIntegrityKey,
        out StunMessage? errorResponse)
    {
        responseIntegrityKey = null;
        errorResponse = null;

        if (_thirdPartyAuthorization is null)
            return true;

        var accessToken = request.Attributes.OfType<AccessTokenAttribute>().FirstOrDefault();
        if (accessToken is null)
        {
            _logger.LogWarning(
                "STUN 401 Unauthorized — missing ACCESS-TOKEN in third-party mode from {Sender}",
                sender);
            errorResponse = BuildThirdPartyChallenge(request);
            return false;
        }

        var hasMi = request.Attributes.Any(a => a.AttributeType == StunAttributeType.MessageIntegrity);
        if (!hasMi)
        {
            _logger.LogWarning(
                "STUN 401 Unauthorized — missing MESSAGE-INTEGRITY in third-party mode from {Sender}",
                sender);
            errorResponse = BuildThirdPartyChallenge(request);
            return false;
        }

        var username = request.Attributes.OfType<UsernameAttribute>().FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(username))
        {
            _logger.LogWarning(
                "STUN 401 Unauthorized — missing USERNAME in third-party mode from {Sender}",
                sender);
            errorResponse = BuildThirdPartyChallenge(request);
            return false;
        }

        try
        {
            if (!_thirdPartyAuthorization.AccessTokenValidator.TryResolveHmacKey(accessToken.Token, username, out var key))
            {
                _logger.LogWarning(
                    "STUN 401 Unauthorized — ACCESS-TOKEN rejected for USERNAME {Username} from {Sender}",
                    username,
                    sender);
                errorResponse = BuildThirdPartyChallenge(request);
                return false;
            }

            if (!_codec.VerifyIntegrity(rawRequest, key))
            {
                _logger.LogWarning(
                    "STUN 401 Unauthorized — ACCESS-TOKEN MESSAGE-INTEGRITY mismatch from {Sender}",
                    sender);
                errorResponse = BuildThirdPartyChallenge(request);
                return false;
            }

            responseIntegrityKey = key;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STUN third-party authorization failed unexpectedly for {Sender}", sender);
            errorResponse = BuildError(request, 500, "Server Error");
            return false;
        }

        return true;
    }

    private static StunRequestHandlingResult BuildResult(StunMessage response, byte[]? responseIntegrityKey)
        => new()
        {
            Response = response,
            ResponseIntegrityKey = responseIntegrityKey
        };

    /// <summary>Builds an error response with the given error code and optional extra attributes.</summary>
    private static StunMessage BuildError(
        StunMessage      request,
        int              code,
        string           reason,
        params StunAttribute[] extras)
    {
        var attrs = new List<StunAttribute>(extras) { new ErrorCodeAttribute { Code = code, Reason = reason } };
        return BuildErrorResponse(request, attrs);
    }

    private static StunMessage BuildErrorResponse(StunMessage request, IReadOnlyList<StunAttribute> attributes)
        => new()
        {
            MessageClass  = StunMessageClass.ErrorResponse,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes    = attributes
        };
}
