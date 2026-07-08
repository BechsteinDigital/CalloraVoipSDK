using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Common.Network;
using CalloraVoipSdk.Core.Infrastructure.Common.Protocols;
using CalloraVoipSdk.Core.Infrastructure.Sip.Authentication;
using CalloraVoipSdk.Core.Infrastructure.Sip.Observability;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transport;
using CalloraVoipSdk.Core.Infrastructure.Sip.Transactions;
using CalloraVoipSdk.Core.Infrastructure.Sip.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Sip.Signaling;

/// <summary>
/// Default SIP registration service using REGISTER transactions with digest challenge support.
///
/// Implemented RFC compliance:
///   RFC 3261 §10 – REGISTER/unREGISTER/fetch-bindings lifecycle
///   RFC 3261 §10.2.2 – Wildcard Contact (* + Expires: 0) via <see cref="UnregisterAllAsync"/>
///   RFC 3261 §10.2.3 – Binding fetch (no Contact) via <see cref="FetchBindingsAsync"/>
///   RFC 3261 §10.2.4 – Call-ID stability via <see cref="SipRegistrationRequest.ExistingCallId"/>
///   RFC 3327     – <c>Supported: path</c> advertised in every REGISTER
///   RFC 3608     – <c>Service-Route</c> from 200 OK returned in result
///   RFC 5626 §4  – <c>+sip.instance</c> Contact parameter when InstanceId is provided
/// </summary>
internal sealed class SipRegistrationService : ISipRegistrationService
{
    private readonly ISipTransportRuntime _transport;
    private readonly ISipDigestAuthenticator _digestAuthenticator;
    private readonly ISipClientTransactionExecutor _transactionExecutor;
    private readonly ISipTelemetrySink _telemetry;
    private readonly ILogger<SipRegistrationService> _logger;

    /// <summary>
    /// Creates a registration service with injected transport/authenticator dependencies.
    /// </summary>
    public SipRegistrationService(
        ISipTransportRuntime transport,
        ISipDigestAuthenticator digestAuthenticator,
        ILoggerFactory loggerFactory,
        ISipTelemetrySink? telemetry = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _digestAuthenticator = digestAuthenticator ?? throw new ArgumentNullException(nameof(digestAuthenticator));
        _telemetry = telemetry ?? NullSipTelemetrySink.Instance;
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<SipRegistrationService>();
        _transactionExecutor = new SipClientTransactionExecutor(_transport, _logger);
    }

    /// <inheritdoc />
    public Task<SipRegistrationResult> RegisterAsync(
        SipRegistrationRequest request,
        CancellationToken ct = default) =>
        ExecuteRegisterAsync(request, mode: RegisterMode.Register, ct);

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAsync(
        SipRegistrationRequest request,
        CancellationToken ct = default) =>
        ExecuteRegisterAsync(request, mode: RegisterMode.Unregister, ct);

    /// <inheritdoc />
    public Task<SipRegistrationResult> UnregisterAllAsync(
        SipRegistrationRequest request,
        CancellationToken ct = default) =>
        ExecuteRegisterAsync(request, mode: RegisterMode.UnregisterAll, ct);

    /// <inheritdoc />
    public Task<SipRegistrationResult> FetchBindingsAsync(
        SipRegistrationRequest request,
        CancellationToken ct = default) =>
        ExecuteRegisterAsync(request, mode: RegisterMode.Fetch, ct);

    // -------------------------------------------------------------------------
    // Core execution
    // -------------------------------------------------------------------------

    private enum RegisterMode { Register, Unregister, UnregisterAll, Fetch }

    /// <summary>
    /// Executes one REGISTER-family transaction including optional digest retry.
    /// </summary>
    private async Task<SipRegistrationResult> ExecuteRegisterAsync(
        SipRegistrationRequest request,
        RegisterMode mode,
        CancellationToken ct)
    {
        ValidateRequest(request);

        var boundLocalEndPoint = _transport.GetLocalEndPoint(request.Transport);
        // RFC 3261 §10.2.4: reuse the same Call-ID when refreshing an existing binding.
        var callId = !string.IsNullOrWhiteSpace(request.ExistingCallId)
            ? request.ExistingCallId
            : SipProtocol.NewCallId();
        var localTag = SipProtocol.NewTag();
        var traceId = ResolveTraceId(callId);

        var eventType = mode switch
        {
            RegisterMode.Unregister or RegisterMode.UnregisterAll => "sip.registration.unregister.started",
            RegisterMode.Fetch => "sip.registration.fetch.started",
            _ => "sip.registration.register.started"
        };
        _telemetry.PublishEvent(new SipEventRecord
        {
            EventType = eventType,
            CallId = callId,
            CorrelationId = $"{callId}:REGISTER:{localTag}",
            TraceId = traceId,
            Attributes = new Dictionary<string, string>
            {
                ["domain"] = request.Domain,
                ["username"] = request.Username,
                ["transport"] = request.Transport.ToString()
            }
        });

        var requestUri = $"sip:{request.Domain}";
        var pendingTargets = new Queue<string>();
        var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pendingTargets.Enqueue(requestUri);
        visitedTargets.Add(requestUri);

        var fromUri = $"sip:{request.Username}@{request.Domain}";
        var toHeader = SipProtocol.FormatNameAddr(displayName: null, fromUri);
        var fromHeader = SipProtocol.FormatNameAddr(request.DisplayName, fromUri, localTag);

        // RFC 3261 §10.2.4: start CSeq from the caller-supplied value for refreshes.
        var cseq = request.StartCSeq > 0 ? request.StartCSeq : 1;
        var authAttempted = false;
        var nonceCount = 1;
        var effectiveExpiresSeconds = mode is RegisterMode.Register
            ? Math.Max(1, request.ExpiresSeconds)
            : 0;
        var reducedBodyRetryUsed = false;
        var schemeDowngradeRetryUsed = false;
        string? authorizationHeader = null;
        string? authorizationHeaderName = null;
        Exception? lastTransportFailure = null;

        while (pendingTargets.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            requestUri = pendingTargets.Dequeue();
            if (!SipProtocol.TryParseSipUri(requestUri, out _, out var targetHost, out var targetPortFromUri))
                continue;

            var secureTarget = SipProtocol.IsSipsUri(requestUri);
            if (secureTarget && request.Transport is not SipTransportProtocol.Tls and not SipTransportProtocol.Wss)
            {
                throw new InvalidOperationException(
                    $"REGISTER target '{requestUri}' requires TLS-capable transport.");
            }

            var targetPort = targetPortFromUri ?? ResolveDefaultRemotePort(request.Port, secureTarget);
            var routeCandidates = await _transport.ResolveRemoteRouteCandidatesAsync(
                    targetHost,
                    targetPort,
                    request.Transport,
                    ct)
                .ConfigureAwait(false);
            var targetRetried = false;

            foreach (var routeCandidate in routeCandidates)
            {
                if (secureTarget
                    && routeCandidate.Transport is not SipTransportProtocol.Tls and not SipTransportProtocol.Wss)
                {
                    continue;
                }

                var branch = SipProtocol.NewBranch();
                var advertisedLocalEndPoint = LocalEndPointAdvertisementResolver.ResolveAdvertisedLocalEndPoint(
                    boundLocalEndPoint,
                    routeCandidate.EndPoint);
                var contactUri = SipSignalingFormat.BuildContactUri(
                    request.Username,
                    advertisedLocalEndPoint,
                    routeCandidate.Transport,
                    forceSecureScheme: false,
                    advertisedHost: request.PublicHost,
                    advertisedPort: request.PublicPort);
                var headers = BuildRegisterHeaders(
                    mode,
                    advertisedLocalEndPoint,
                    branch,
                    routeCandidate.Transport,
                    fromHeader,
                    toHeader,
                    callId,
                    cseq,
                    contactUri,
                    effectiveExpiresSeconds,
                    request.UserAgent,
                    request.InstanceId,
                    traceId,
                    request.PublicHost,
                    request.PublicPort);

                if (!string.IsNullOrWhiteSpace(authorizationHeader)
                    && !string.IsNullOrWhiteSpace(authorizationHeaderName))
                {
                    headers[authorizationHeaderName] = authorizationHeader;
                }

                SipResponse response;
                try
                {
                    var transactionResult = await _transactionExecutor.ExecuteAsync(
                            new SipClientTransactionRequest
                            {
                                Method = "REGISTER",
                                RequestUri = requestUri,
                                Headers = headers,
                                Body = null,
                                RemoteEndPoint = routeCandidate.EndPoint,
                                Transport = routeCandidate.Transport,
                                Timeout = request.Timeout
                            },
                            ct)
                        .ConfigureAwait(false);
                    response = transactionResult.FinalResponse.Response;
                }
                catch (TimeoutException timeoutEx)
                {
                    lastTransportFailure = timeoutEx;
                    _logger.LogDebug(
                        timeoutEx,
                        "SIP REGISTER timed out for target {RequestUri} via {RemoteEndPoint}.",
                        requestUri,
                        routeCandidate.EndPoint);
                    continue;
                }
                catch (InvalidOperationException transportEx) when (transportEx.InnerException is not null)
                {
                    lastTransportFailure = transportEx;
                    _logger.LogDebug(
                        transportEx,
                        "SIP REGISTER transport failure for target {RequestUri} via {RemoteEndPoint}.",
                        requestUri,
                        routeCandidate.EndPoint);
                    continue;
                }

                if (SipProtocol.IsSuccess(response.StatusCode))
                {
                    var effectiveExpires = TryGetEffectiveExpires(response, effectiveExpiresSeconds);
                    _logger.LogDebug(
                        "SIP REGISTER succeeded for {User}@{Domain} with {Status}.",
                        request.Username,
                        request.Domain,
                        response.StatusCode);
                    _telemetry.PublishMetric(new SipMetricRecord
                    {
                        Name = "sip.registration.success",
                        Value = 1,
                        TraceId = traceId,
                        Labels = new Dictionary<string, string>
                        {
                            ["domain"] = request.Domain,
                            ["status"] = response.StatusCode.ToString()
                        }
                    });

                    var (observedHost, observedPort) =
                        SipProtocol.ExtractViaReceivedRport(response.Header("Via"));

                    return new SipRegistrationResult
                    {
                        CallId = callId,
                        StatusCode = response.StatusCode,
                        EffectiveExpiresSeconds = effectiveExpires,
                        ContactUri = contactUri,
                        Authenticated = authAttempted,
                        NextCSeq = cseq + 1,
                        ServiceRoute = ExtractServiceRoute(response),
                        RegisteredBindings = ParseRegisteredBindings(response),
                        ObservedPublicHost = observedHost,
                        ObservedPublicPort = observedPort
                    };
                }

                if (response.StatusCode is >= 300 and < 400)
                {
                    if (EnqueueRedirectTargets(response, pendingTargets, visitedTargets))
                    {
                        cseq++;
                        targetRetried = true;
                        break;
                    }
                }

                if (response.StatusCode == 423)
                {
                    var minExpires = TryGetMinExpires(response);
                    if (minExpires > 0 && minExpires > effectiveExpiresSeconds)
                    {
                        _logger.LogDebug(
                            "SIP REGISTER received 423 for {User}@{Domain}; retrying with Min-Expires={MinExpires}.",
                            request.Username,
                            request.Domain,
                            minExpires);
                        effectiveExpiresSeconds = minExpires;
                        cseq++;
                        _telemetry.PublishMetric(new SipMetricRecord
                        {
                            Name = "sip.registration.min_expires_retry",
                            Value = 1,
                            TraceId = traceId,
                            Labels = new Dictionary<string, string>
                            {
                                ["domain"] = request.Domain,
                                ["min_expires"] = minExpires.ToString()
                            }
                        });
                        targetRetried = true;
                        pendingTargets.Enqueue(requestUri);
                        break;
                    }
                }

                if (!authAttempted && response.StatusCode is 401 or 407)
                {
                    if (SipDigestChallengeSelector.TrySelect(
                            response,
                            out var challengeHeader,
                            out var nextAuthorizationHeaderName)
                        && _digestAuthenticator.TryCreateAuthorizationHeader(
                            challengeHeader,
                            username: request.Username,
                            password: request.Password,
                            method: "REGISTER",
                            requestUri: requestUri,
                            nonceCount: nonceCount++,
                            out var generatedAuthorization))
                    {
                        authAttempted = true;
                        cseq++;
                        authorizationHeader = generatedAuthorization;
                        authorizationHeaderName = nextAuthorizationHeaderName;
                        _telemetry.PublishMetric(new SipMetricRecord
                        {
                            Name = "sip.registration.auth_retry",
                            Value = 1,
                            TraceId = traceId,
                            Labels = new Dictionary<string, string>
                            {
                                ["domain"] = request.Domain,
                                ["status"] = response.StatusCode.ToString()
                            }
                        });
                        targetRetried = true;
                        pendingTargets.Enqueue(requestUri);
                        break;
                    }
                }

                if (authAttempted && response.StatusCode is 401 or 407)
                {
                    if (SipDigestChallengeSelector.TrySelect(
                            response,
                            out var challengeHeader,
                            out var nextAuthorizationHeaderName)
                        && SipDigestChallengeSelector.IsStaleChallenge(challengeHeader)
                        && _digestAuthenticator.TryCreateAuthorizationHeader(
                            challengeHeader,
                            username: request.Username,
                            password: request.Password,
                            method: "REGISTER",
                            requestUri: requestUri,
                            nonceCount: nonceCount++,
                            out var generatedAuthorization))
                    {
                        cseq++;
                        authorizationHeader = generatedAuthorization;
                        authorizationHeaderName = nextAuthorizationHeaderName;
                        _telemetry.PublishMetric(new SipMetricRecord
                        {
                            Name = "sip.registration.stale_retry",
                            Value = 1,
                            TraceId = traceId,
                            Labels = new Dictionary<string, string>
                            {
                                ["domain"] = request.Domain
                            }
                        });
                        targetRetried = true;
                        pendingTargets.Enqueue(requestUri);
                        break;
                    }
                }

                if (response.StatusCode is 413 or 415)
                {
                    if (!reducedBodyRetryUsed)
                    {
                        reducedBodyRetryUsed = true;
                        cseq++;
                        targetRetried = true;
                        pendingTargets.Enqueue(requestUri);
                        break;
                    }
                }

                if (response.StatusCode == 416
                    && !schemeDowngradeRetryUsed
                    && TryDowngradeSipsToSip(requestUri, out var downgradedUri)
                    && visitedTargets.Add(downgradedUri))
                {
                    schemeDowngradeRetryUsed = true;
                    pendingTargets.Enqueue(downgradedUri);
                    cseq++;
                    targetRetried = true;
                    break;
                }

                _telemetry.PublishMetric(new SipMetricRecord
                {
                    Name = "sip.registration.failed",
                    Value = 1,
                    TraceId = traceId,
                    Labels = new Dictionary<string, string>
                    {
                        ["domain"] = request.Domain,
                        ["status"] = response.StatusCode.ToString()
                    }
                });

                // RFC 3261 §20.33: log Retry-After from 503/480/600 responses.
                var retryAfterRaw = response.Header("Retry-After");
                var retrySuffix = string.Empty;
                if (!string.IsNullOrWhiteSpace(retryAfterRaw)
                    && int.TryParse(retryAfterRaw.Split(';')[0].Trim(), out var retryAfterSec))
                {
                    retrySuffix = $" Retry-After={retryAfterSec}s.";
                    _logger.LogInformation(
                        "SIP REGISTER rejected with Retry-After={RetryAfterSeconds}s (status {StatusCode}) for {User}@{Domain}.",
                        retryAfterSec, response.StatusCode, request.Username, request.Domain);
                }

                throw new SipRegistrationFailedException(
                    $"REGISTER failed for {request.Username}@{request.Domain} with status {response.StatusCode} {response.ReasonPhrase}.{retrySuffix}",
                    response.StatusCode,
                    response.ReasonPhrase);
            }

            if (targetRetried)
                continue;
        }

        if (lastTransportFailure is TimeoutException timeoutFailure)
            throw new TimeoutException("REGISTER failed with synthetic status 408 Request Timeout.", timeoutFailure);
        if (lastTransportFailure is InvalidOperationException transportFailure)
            throw new InvalidOperationException("REGISTER failed with synthetic status 503 Service Unavailable.", transportFailure);

        throw new InvalidOperationException(
            $"REGISTER failed for {request.Username}@{request.Domain}: no routable target remained.");
    }

    // -------------------------------------------------------------------------
    // Header construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the complete REGISTER header dictionary for one transaction attempt.
    /// </summary>
    private static Dictionary<string, string> BuildRegisterHeaders(
        RegisterMode mode,
        IPEndPoint localEndPoint,
        string branch,
        SipTransportProtocol transport,
        string fromHeader,
        string toHeader,
        string callId,
        int cseq,
        string contactUri,
        int expiresSeconds,
        string userAgent,
        string? instanceId,
        string traceId,
        string? publicHost = null,
        int? publicPort = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Via"] = SipSignalingFormat.BuildVia(localEndPoint, branch, transport, publicHost, publicPort),
            ["Max-Forwards"] = "70",
            ["From"] = fromHeader,
            ["To"] = toHeader,
            ["Call-ID"] = callId,
            ["CSeq"] = $"{cseq} REGISTER",
            // RFC 3327: path; RFC 3261: 100rel/timer/replaces; RFC 5626 §5: outbound when +sip.instance is used.
            ["Supported"] = string.IsNullOrWhiteSpace(instanceId)
                ? "path, 100rel, timer, replaces"
                : "path, 100rel, timer, replaces, outbound",
            ["Expires"] = expiresSeconds.ToString(),
            ["User-Agent"] = userAgent,
            ["X-CalloraVoipSdk-Trace-Id"] = traceId
        };

        switch (mode)
        {
            case RegisterMode.UnregisterAll:
                // RFC 3261 §10.2.2: wildcard contact removes all bindings.
                headers["Contact"] = "*";
                headers["Expires"] = "0";
                break;

            case RegisterMode.Fetch:
                // RFC 3261 §10.2.3: no Contact header → fetch binding list.
                break;

            default:
                // Normal REGISTER or per-binding unregister.
                // RFC 3261 §10.2.1.1 + RFC 5626 §4: include per-binding expires parameter
                // and optional +sip.instance Contact parameter.
                var contactValue = BuildContactHeaderValue(contactUri, expiresSeconds, instanceId);
                headers["Contact"] = contactValue;
                break;
        }

        return headers;
    }

    /// <summary>
    /// Builds the Contact header value including <c>expires</c> and optional
    /// <c>+sip.instance</c> and <c>reg-id</c> parameters (RFC 5626 §4).
    /// </summary>
    private static string BuildContactHeaderValue(
        string contactUri,
        int expiresSeconds,
        string? instanceId)
    {
        // RFC 3261 §10.2.1.1: include expires as a Contact parameter for per-binding control.
        var value = $"<{contactUri}>;expires={expiresSeconds}";

        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            // RFC 5626 §4.1: +sip.instance identifies the UA instance for the registrar.
            value += $";\"+sip.instance\"=\"<{instanceId}>\"";
            // RFC 5626 §4.1: reg-id identifies the flow; starts at 1 for the first registration.
            value += ";reg-id=1";
        }

        return value;
    }

    // -------------------------------------------------------------------------
    // Response parsing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts the <c>Service-Route</c> header value from the 200 OK (RFC 3608).
    /// </summary>
    private static string? ExtractServiceRoute(SipResponse response)
    {
        var serviceRoute = response.Header("Service-Route");
        return string.IsNullOrWhiteSpace(serviceRoute) ? null : serviceRoute.Trim();
    }

    /// <summary>
    /// Parses the Contact headers in the 200 OK into a list of active bindings.
    /// </summary>
    private static IReadOnlyList<SipRegistrationBinding> ParseRegisteredBindings(SipResponse response)
    {
        var bindings = new List<SipRegistrationBinding>();
        foreach (var contactRow in response.HeaderValues("Contact"))
        {
            foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(contactRow))
            {
                var uri = SipProtocol.ExtractUriFromNameAddr(token);
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                var expiresSeconds = TryExtractContactExpires(token);
                bindings.Add(new SipRegistrationBinding
                {
                    ContactUri = uri,
                    ExpiresSeconds = expiresSeconds
                });
            }
        }
        return bindings;
    }

    /// <summary>
    /// Extracts the <c>expires</c> parameter from a single Contact entry token.
    /// Returns -1 when not present.
    /// </summary>
    private static int TryExtractContactExpires(string contactToken)
    {
        var markerIndex = contactToken.IndexOf("expires=", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return -1;

        var tail = contactToken[(markerIndex + 8)..];
        var end = tail.IndexOfAny([',', ';']);
        var value = end >= 0 ? tail[..end] : tail;
        return int.TryParse(value.Trim(), out var parsed) ? Math.Max(0, parsed) : -1;
    }

    // -------------------------------------------------------------------------
    // Helpers (unchanged from original)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enqueues Contact URIs from one redirect response while suppressing duplicates.
    /// </summary>
    private static bool EnqueueRedirectTargets(
        SipResponse response,
        Queue<string> pendingTargets,
        HashSet<string> visitedTargets)
    {
        var enqueuedAny = false;
        foreach (var contactRow in response.HeaderValues("Contact"))
        {
            foreach (var token in ProtocolCommonUtilities.SplitCommaSeparatedRespectingQuotes(contactRow))
            {
                var uri = SipProtocol.ExtractUriFromNameAddr(token);
                if (string.IsNullOrWhiteSpace(uri))
                    continue;
                if (!SipProtocol.TryParseSipUri(uri, out _, out _, out _))
                    continue;
                if (!visitedTargets.Add(uri))
                    continue;

                pendingTargets.Enqueue(uri);
                enqueuedAny = true;
            }
        }

        return enqueuedAny;
    }

    /// <summary>
    /// Converts a SIPS URI to SIP for 416 retry handling.
    /// </summary>
    private static bool TryDowngradeSipsToSip(string requestUri, out string sipUri)
    {
        sipUri = string.Empty;
        if (!requestUri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return false;

        sipUri = $"sip:{requestUri[5..]}";
        return SipProtocol.TryParseSipUri(sipUri, out _, out _, out _);
    }

    /// <summary>
    /// Resolves effective remote port with SIPS default handling.
    /// </summary>
    private static int ResolveDefaultRemotePort(int configuredPort, bool secureTarget)
    {
        if (secureTarget && configuredPort == 5060)
            return 5061;
        return configuredPort;
    }

    /// <summary>
    /// Extracts effective expires value from REGISTER response headers.
    /// </summary>
    private static int TryGetEffectiveExpires(SipResponse response, int fallback)
    {
        if (int.TryParse(response.Header("Expires"), out var expires))
            return Math.Max(0, expires);

        var contactHeader = response.Header("Contact");
        if (string.IsNullOrWhiteSpace(contactHeader))
            return fallback;
        var markerIndex = contactHeader.IndexOf("expires=", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return fallback;
        var tail = contactHeader[(markerIndex + 8)..];
        var separatorIndex = tail.IndexOfAny([',', ';']);
        var value = separatorIndex >= 0 ? tail[..separatorIndex] : tail;
        return int.TryParse(value.Trim(), out var parsed) ? Math.Max(0, parsed) : fallback;
    }

    /// <summary>
    /// Parses Min-Expires header from 423 responses.
    /// </summary>
    private static int TryGetMinExpires(SipResponse response)
    {
        if (int.TryParse(response.Header("Min-Expires"), out var minExpires))
            return Math.Max(0, minExpires);
        return 0;
    }

    /// <summary>
    /// Validates registration request shape before network I/O begins.
    /// </summary>
    private static void ValidateRequest(SipRegistrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username))
            throw new ArgumentException("Username is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Password))
            throw new ArgumentException("Password is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Domain))
            throw new ArgumentException("Domain is required.", nameof(request));
        if (request.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "Port must be between 1 and 65535.");
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive.");
    }

    /// <summary>
    /// Resolves a deterministic trace identifier for registration telemetry.
    /// </summary>
    private static string ResolveTraceId(string fallback) =>
        Activity.Current?.TraceId.ToString() ?? fallback;
}
