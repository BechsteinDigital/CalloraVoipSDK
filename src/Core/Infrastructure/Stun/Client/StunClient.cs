using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using CalloraVoipSdk.Core.Infrastructure.Stun.Attributes;
using CalloraVoipSdk.Core.Infrastructure.Stun.Auth;
using CalloraVoipSdk.Core.Infrastructure.Stun.Messages;
using CalloraVoipSdk.Core.Infrastructure.Stun.Wire;

namespace CalloraVoipSdk.Core.Infrastructure.Stun.Client;

/// <summary>
/// STUN client implementing Binding Requests over UDP/TCP/TLS:
/// retransmission schedule for UDP (RFC 5389 §7.2.1), stream framing for TCP/TLS (§7.2.2),
/// 300 Try Alternate redirects (§11), and long-term credential challenge/response (§10.2).
/// <para>
/// UDP retransmission schedule: initial RTO of 500 ms, doubled on each timeout,
/// capped at 16 000 ms, with a maximum of 7 total transmission attempts.
/// </para>
/// <para>
/// Long-term credential flow (RFC 5389 §10.2.2):
/// 1. Send unauthenticated Binding Request.
/// 2. On 401 with REALM + NONCE: re-send with USERNAME / REALM / NONCE / MESSAGE-INTEGRITY.
/// 3. On 438 Stale Nonce: update nonce from the server's response and retry once more.
/// </para>
/// </summary>
internal sealed class StunClient : IStunClient
{
    private readonly IStunMessageCodec _codec;
    private readonly ILogger<StunClient> _logger;

    /// <summary>Initial retransmission timeout (RTO₀) in milliseconds for UDP (RFC 5389 §7.2.1).</summary>
    private const int InitialRtoMs = 500;

    /// <summary>Maximum per-attempt retransmission timeout in milliseconds for UDP.</summary>
    private const int MaxRtoMs = 16_000;

    /// <summary>Total UDP transmission attempts (1 original + 6 retransmissions).</summary>
    private const int MaxAttempts = 7;

    /// <summary>Single-response timeout for reliable transports (TCP/TLS) in milliseconds.</summary>
    private const int ReliableResponseTimeoutMs = 8_000;

    /// <summary>Initialises the client with its required dependencies.</summary>
    public StunClient(IStunMessageCodec codec, ILogger<StunClient> logger)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(logger);
        _codec = codec;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<StunBindingResult> QueryBindingAsync(
        IPEndPoint serverEndPoint,
        StunCredentials? credentials = null,
        StunTransport transport = StunTransport.Udp,
        string? tlsTargetHost = null,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback = null,
        IPEndPoint? localEndPoint = null,
        Socket? sharedUdpSocket = null,
        IReadOnlyList<StunAttribute>? additionalAttributes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serverEndPoint);
        return QueryWithAuthAsync(
            serverEndPoint,
            credentials: credentials,
            transport: transport,
            allowRedirect: true,
            tlsTargetHost: tlsTargetHost,
            tlsRemoteCertificateValidationCallback: tlsRemoteCertificateValidationCallback,
            localEndPoint: localEndPoint,
            sharedUdpSocket: sharedUdpSocket,
            additionalAttributes: additionalAttributes,
            ct: ct);
    }

    // ── Auth-aware dispatch ─────────────────────────────────────────────────

    /// <summary>
    /// Handles the long-term credential challenge/response on top of transport-specific send/receive loops.
    /// Short-term credentials and unauthenticated requests go straight to <see cref="QueryCoreAsync"/>.
    /// </summary>
    private async Task<StunBindingResult> QueryWithAuthAsync(
        IPEndPoint server,
        StunCredentials? credentials,
        StunTransport transport,
        bool allowRedirect,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        IPEndPoint? localEndPoint,
        Socket? sharedUdpSocket,
        IReadOnlyList<StunAttribute>? additionalAttributes,
        CancellationToken ct)
    {
        if (credentials is null || !credentials.IsLongTerm)
        {
            return await QueryCoreAsync(
                    server,
                    credentials,
                    transport,
                    allowRedirect,
                    tlsTargetHost,
                    tlsRemoteCertificateValidationCallback,
                    localEndPoint,
                    sharedUdpSocket,
                    additionalAttributes,
                    ct)
                .ConfigureAwait(false);
        }

        // Long-term step 1: send unauthenticated (RFC 5389 §10.2.2).
        try
        {
            return await QueryCoreAsync(
                    server,
                    credentialsToSend: null,
                    transport,
                    allowRedirect,
                    tlsTargetHost,
                    tlsRemoteCertificateValidationCallback,
                    localEndPoint,
                    sharedUdpSocket,
                    additionalAttributes,
                    ct)
                .ConfigureAwait(false);
        }
        catch (StunChallengeException challenge) when (challenge.ErrorCode == 401
                                                       && challenge.Realm is not null
                                                       && challenge.Nonce is not null)
        {
            _logger.LogDebug(
                "STUN long-term auth: 401 challenge from {Server} realm={Realm}",
                server, challenge.Realm);
            credentials = credentials.WithRealmAndNonce(challenge.Realm, challenge.Nonce);
        }

        // Long-term step 2: retry with credentials.
        try
        {
            return await QueryCoreAsync(
                    server,
                    credentials,
                    transport,
                    allowRedirect,
                    tlsTargetHost,
                    tlsRemoteCertificateValidationCallback,
                    localEndPoint,
                    sharedUdpSocket,
                    additionalAttributes,
                    ct)
                .ConfigureAwait(false);
        }
        catch (StunChallengeException stale) when (stale.ErrorCode == 438 && stale.Nonce is not null)
        {
            _logger.LogDebug("STUN long-term auth: 438 stale nonce from {Server}, refreshing nonce", server);
            credentials = credentials.WithNonce(stale.Nonce);
        }

        // Long-term step 3: final retry after nonce refresh.
        return await QueryCoreAsync(
                server,
                credentials,
                transport,
                allowRedirect,
                tlsTargetHost,
                tlsRemoteCertificateValidationCallback,
                localEndPoint,
                sharedUdpSocket,
                additionalAttributes,
                ct)
            .ConfigureAwait(false);
    }

    // ── Core transport loop ────────────────────────────────────────────────

    private Task<StunBindingResult> QueryCoreAsync(
        IPEndPoint server,
        StunCredentials? credentialsToSend,
        StunTransport transport,
        bool allowRedirect,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        IPEndPoint? localEndPoint,
        Socket? sharedUdpSocket,
        IReadOnlyList<StunAttribute>? additionalAttributes,
        CancellationToken ct)
        => transport switch
        {
            StunTransport.Udp => QueryCoreUdpAsync(server, credentialsToSend, allowRedirect, localEndPoint, sharedUdpSocket, additionalAttributes, ct),
            StunTransport.Tcp => QueryCoreStreamAsync(
                server, credentialsToSend, transport, allowRedirect, tlsTargetHost, tlsRemoteCertificateValidationCallback, localEndPoint, sharedUdpSocket, additionalAttributes, ct),
            StunTransport.Tls => QueryCoreStreamAsync(
                server, credentialsToSend, transport, allowRedirect, tlsTargetHost, tlsRemoteCertificateValidationCallback, localEndPoint, sharedUdpSocket, additionalAttributes, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unsupported STUN transport.")
        };

    private async Task<StunBindingResult> QueryCoreUdpAsync(
        IPEndPoint server,
        StunCredentials? credentialsToSend,
        bool allowRedirect,
        IPEndPoint? localEndPoint,
        Socket? sharedUdpSocket,
        IReadOnlyList<StunAttribute>? additionalAttributes,
        CancellationToken ct)
    {
        var request = BuildRequest(credentialsToSend, additionalAttributes);
        var requestBytes = EncodeRequest(request, credentialsToSend);

        _logger.LogDebug(
            "STUN Binding Request → {Server}, transport={Transport}, txId={Tx}, auth={Auth}",
            server,
            StunTransport.Udp,
            Convert.ToHexString(request.TransactionId),
            credentialsToSend is not null
                ? (credentialsToSend.IsLongTerm ? "long-term" : "short-term")
                : "none");

        // A shared socket (e.g. the reserved RTP media port during ICE gathering) is
        // used as-is via SendTo/ReceiveFrom: binding a second socket to that port would
        // fail with EADDRINUSE, and connecting the shared socket would filter later
        // inbound traffic. Ownership stays with the caller.
        using var udp = sharedUdpSocket is not null
            ? null
            : localEndPoint is null
                ? new UdpClient(server.AddressFamily)
                : new UdpClient(localEndPoint);
        udp?.Connect(server);

        var responseIntegrityKey = credentialsToSend?.DeriveHmacKey();

        int rto = InitialRtoMs;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (sharedUdpSocket is not null)
                    await sharedUdpSocket.SendToAsync(requestBytes, SocketFlags.None, server, ct).ConfigureAwait(false);
                else
                    await udp!.SendAsync(requestBytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "STUN UDP send failed on attempt {Attempt}", attempt);
                throw new StunException($"STUN UDP send failed: {ex.Message}", ex);
            }

            var response = sharedUdpSocket is not null
                ? await ReceiveMatchingSharedUdpAsync(sharedUdpSocket, request.TransactionId, responseIntegrityKey, server, rto, ct)
                    .ConfigureAwait(false)
                : await ReceiveMatchingUdpAsync(udp!, request.TransactionId, responseIntegrityKey, server, rto, ct)
                    .ConfigureAwait(false);

            if (response is null)
            {
                _logger.LogTrace("STUN attempt {Attempt} timed out after {Rto} ms", attempt, rto);
                rto = Math.Min(rto * 2, MaxRtoMs);
                continue;
            }

            return await ProcessResponseAsync(
                    response,
                    server,
                    StunTransport.Udp,
                    allowRedirect,
                    tlsTargetHost: null,
                    tlsRemoteCertificateValidationCallback: null,
                    localEndPoint,
                    sharedUdpSocket,
                    ct)
                .ConfigureAwait(false);
        }

        throw new StunException($"STUN server {server} did not respond after {MaxAttempts} attempts.");
    }

    private async Task<StunBindingResult> QueryCoreStreamAsync(
        IPEndPoint server,
        StunCredentials? credentialsToSend,
        StunTransport transport,
        bool allowRedirect,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        IPEndPoint? localEndPoint,
        Socket? sharedUdpSocket,
        IReadOnlyList<StunAttribute>? additionalAttributes,
        CancellationToken ct)
    {
        var request = BuildRequest(credentialsToSend, additionalAttributes);
        var requestBytes = EncodeRequest(request, credentialsToSend);

        _logger.LogDebug(
            "STUN Binding Request → {Server}, transport={Transport}, txId={Tx}, auth={Auth}",
            server,
            transport,
            Convert.ToHexString(request.TransactionId),
            credentialsToSend is not null
                ? (credentialsToSend.IsLongTerm ? "long-term" : "short-term")
                : "none");

        using var tcp = new TcpClient(server.AddressFamily);
        if (localEndPoint is not null)
            tcp.Client.Bind(localEndPoint);
        try
        {
            await tcp.ConnectAsync(server.Address, server.Port, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "STUN {Transport} connect failed to {Server}", transport, server);
            throw new StunException($"STUN {transport} connect failed: {ex.Message}", ex);
        }

        using var networkStream = tcp.GetStream();
        Stream stream = networkStream;
        if (transport == StunTransport.Tls)
        {
            var targetHost = !string.IsNullOrWhiteSpace(tlsTargetHost)
                ? tlsTargetHost
                : server.Address.ToString();

            var tlsStream = new SslStream(
                networkStream,
                leaveInnerStreamOpen: true,
                tlsRemoteCertificateValidationCallback);

            try
            {
                await tlsStream.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = targetHost,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "STUN TLS handshake failed for {Server}", server);
                throw new StunException($"STUN TLS handshake failed: {ex.Message}", ex);
            }

            stream = tlsStream;
        }

        try
        {
            await stream.WriteAsync(requestBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "STUN {Transport} send failed", transport);
            throw new StunException($"STUN {transport} send failed: {ex.Message}", ex);
        }

        var responseIntegrityKey = credentialsToSend?.DeriveHmacKey();
        var response = await ReceiveMatchingStreamAsync(stream, request.TransactionId, responseIntegrityKey, server, ReliableResponseTimeoutMs, ct)
            .ConfigureAwait(false);
        if (response is null)
            throw new StunException($"STUN server {server} did not respond on {transport}.");

        return await ProcessResponseAsync(
                response,
                server,
                transport,
                allowRedirect,
                tlsTargetHost,
                tlsRemoteCertificateValidationCallback,
                localEndPoint,
                sharedUdpSocket,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a Binding Request, inserting the optional additional attributes and the credential
    /// attributes ahead of MESSAGE-INTEGRITY. Additional attributes are covered by
    /// MESSAGE-INTEGRITY only when credentials are present (ICE checks always send credentials).
    /// </summary>
    private static StunMessage BuildRequest(
        StunCredentials? credentials,
        IReadOnlyList<StunAttribute>? additionalAttributes = null)
    {
        var request = StunMessage.CreateBindingRequest();

        var hasAdditional = additionalAttributes is { Count: > 0 };
        if (credentials is null && !hasAdditional)
            return request;

        // Attributes covered by MESSAGE-INTEGRITY must precede it. ICE check attributes
        // (PRIORITY, ICE-CONTROLLING/CONTROLLED; RFC 8445 §7.2.2) go first, then the credential
        // attributes: USERNAME (always), then REALM + NONCE for long-term (RFC 5389 §10.1, §10.2.2).
        var attrs = new List<StunAttribute>();

        if (hasAdditional)
            attrs.AddRange(additionalAttributes!);

        if (credentials is not null)
        {
            attrs.Add(new UsernameAttribute { Value = credentials.Username });
            if (credentials.IsLongTerm)
            {
                if (credentials.Realm is not null)
                    attrs.Add(new RealmAttribute { Value = credentials.Realm });
                if (credentials.Nonce is not null)
                    attrs.Add(new NonceAttribute { Value = credentials.Nonce });
            }
        }

        attrs.AddRange(request.Attributes);

        return new StunMessage
        {
            MessageClass = request.MessageClass,
            MessageMethod = request.MessageMethod,
            TransactionId = request.TransactionId,
            Attributes = attrs
        };
    }

    /// <summary>Encodes the request, adding MESSAGE-INTEGRITY when credentials are present.</summary>
    private byte[] EncodeRequest(StunMessage request, StunCredentials? credentials)
    {
        if (credentials is null)
            return _codec.Encode(request);

        return _codec.EncodeWithIntegrity(request, credentials.DeriveHmacKey(), addFingerprint: false);
    }

    /// <summary>
    /// Waits for a UDP response whose transaction ID matches the request within <paramref name="timeoutMs"/>.
    /// Non-matching packets are discarded silently; only the timeout counts as a failure.
    /// Returns null on timeout.
    /// </summary>
    private async Task<StunMessage?> ReceiveMatchingUdpAsync(
        UdpClient udp,
        byte[] transactionId,
        byte[]? responseIntegrityKey,
        IPEndPoint server,
        int timeoutMs,
        CancellationToken ct)
    {
        using var rtoSource = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(rtoSource.Token, ct);

        while (!linked.Token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate caller cancellation.
            }
            catch (OperationCanceledException)
            {
                return null; // RTO expired.
            }

            var msg = _codec.Decode(received.Buffer);
            if (msg is not null
                && IsAcceptableResponse(msg, received.Buffer, transactionId, responseIntegrityKey, server, received.RemoteEndPoint))
                return msg;

            _logger.LogTrace("STUN discarded non-matching packet ({Bytes} bytes)", received.Buffer.Length);
        }

        return null;
    }

    /// <summary>
    /// Waits for a matching response on a caller-owned (shared) UDP socket via
    /// ReceiveFrom, without connecting or disposing it. Returns null when the RTO expires.
    /// </summary>
    private async Task<StunMessage?> ReceiveMatchingSharedUdpAsync(
        Socket sharedSocket,
        byte[] transactionId,
        byte[]? responseIntegrityKey,
        IPEndPoint server,
        int timeoutMs,
        CancellationToken ct)
    {
        using var rtoSource = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(rtoSource.Token, ct);
        var buffer = new byte[1500];
        var anyEndPoint = new IPEndPoint(
            sharedSocket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!linked.Token.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await sharedSocket
                    .ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate caller cancellation.
            }
            catch (OperationCanceledException)
            {
                return null; // RTO expired.
            }

            var datagram = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
            var msg = _codec.Decode(datagram);
            if (msg is not null
                && IsAcceptableResponse(
                    msg, datagram, transactionId, responseIntegrityKey, server, (IPEndPoint)received.RemoteEndPoint))
                return msg;

            _logger.LogTrace(
                "STUN discarded non-matching packet on shared socket ({Bytes} bytes from {Remote})",
                received.ReceivedBytes,
                received.RemoteEndPoint);
        }

        return null;
    }

    /// <summary>
    /// Waits for a TCP/TLS response whose transaction ID matches the request within <paramref name="timeoutMs"/>.
    /// Returns null on timeout or clean end-of-stream.
    /// </summary>
    private async Task<StunMessage?> ReceiveMatchingStreamAsync(
        Stream stream,
        byte[] transactionId,
        byte[]? responseIntegrityKey,
        IPEndPoint server,
        int timeoutMs,
        CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, ct);

        while (!linked.Token.IsCancellationRequested)
        {
            byte[]? raw;
            try
            {
                raw = await StunTcpFramer.ReadMessageAsync(stream, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "STUN stream receive failed");
                throw new StunException($"STUN stream receive failed: {ex.Message}", ex);
            }

            if (raw is null)
                return null;

            var msg = _codec.Decode(raw);
            // The stream is connected to the server, so the source is inherently bound (actualSource: null).
            if (msg is not null
                && IsAcceptableResponse(msg, raw, transactionId, responseIntegrityKey, server, actualSource: null))
                return msg;

            _logger.LogTrace("STUN discarded non-matching stream message ({Bytes} bytes)", raw.Length);
        }

        return null;
    }

    /// <summary>
    /// Decides whether a received datagram is a legitimate response to the outstanding request,
    /// beyond the transaction-id demux key. A datagram that fails any check is discarded and the
    /// receive loop keeps waiting for the real response (RFC 5389 §7.3.3 / §10.1.2), so a spoofed
    /// or corrupted packet cannot masquerade as the answer:
    /// <list type="number">
    /// <item>transaction id matches the request;</item>
    /// <item>the message is a Success or Error <em>response</em> (not a reflected request/indication);</item>
    /// <item>the method is Binding (the only method this client sends);</item>
    /// <item>the source transport address matches the queried server, when the caller supplies it
    /// (connected sockets are already OS-filtered; the shared UDP socket is not);</item>
    /// <item>FINGERPRINT, when present, is valid (RFC 5389 §15.5);</item>
    /// <item>when the request carried credentials, a Success response carries a valid
    /// MESSAGE-INTEGRITY over the same key (RFC 5389 §10.1.2 / §10.2.2). Error responses
    /// (401/438 challenges) are pre-auth and pass through for the long-term flow to interpret.</item>
    /// </list>
    /// </summary>
    internal bool IsAcceptableResponse(
        StunMessage message,
        ReadOnlySpan<byte> raw,
        byte[] transactionId,
        byte[]? responseIntegrityKey,
        IPEndPoint expectedServer,
        IPEndPoint? actualSource)
    {
        if (!message.TransactionId.AsSpan().SequenceEqual(transactionId))
            return false;

        if (message.MessageClass is not (StunMessageClass.SuccessResponse or StunMessageClass.ErrorResponse))
        {
            _logger.LogTrace("STUN discarded response: unexpected class {Class}", message.MessageClass);
            return false;
        }

        if (message.MessageMethod != StunMessageMethod.Binding)
        {
            _logger.LogTrace("STUN discarded response: unexpected method {Method}", message.MessageMethod);
            return false;
        }

        // A response arrives from the address the request was sent to; a mismatch on the shared
        // media socket is another flow's traffic or an off-path injection (RFC 5389 §7.3.3).
        if (actualSource is not null && !EndPointsEquivalent(actualSource, expectedServer))
        {
            _logger.LogTrace("STUN discarded response from unexpected source {Source}", actualSource);
            return false;
        }

        // FINGERPRINT is optional, but present-and-invalid means the datagram is corrupted or not
        // really STUN (e.g. demultiplexed non-STUN traffic on the shared socket) — discard it.
        if (message.Attributes.Any(a => a.AttributeType == StunAttributeType.Fingerprint)
            && !_codec.VerifyFingerprint(raw))
        {
            _logger.LogTrace("STUN discarded response: FINGERPRINT mismatch");
            return false;
        }

        // Authenticated transaction: a spoofed Success response without a valid MESSAGE-INTEGRITY
        // must never be accepted as our mapped address. Distinguish a missing attribute (a server
        // that does not honour the credentials) from an invalid one (corruption or an injection
        // attempt) so the two are separable in operational logs.
        if (responseIntegrityKey is not null && message.MessageClass == StunMessageClass.SuccessResponse)
        {
            if (!message.Attributes.Any(a => a.AttributeType == StunAttributeType.MessageIntegrity))
            {
                _logger.LogTrace("STUN discarded Success response: MESSAGE-INTEGRITY missing");
                return false;
            }

            if (!_codec.VerifyIntegrity(raw, responseIntegrityKey))
            {
                _logger.LogTrace("STUN discarded Success response: MESSAGE-INTEGRITY invalid");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares two transport addresses for equivalence, canonicalising IPv4-mapped IPv6 addresses
    /// so a dual-stack socket's mapped source compares equal to the IPv4 server it was sent to.
    /// </summary>
    private static bool EndPointsEquivalent(IPEndPoint actual, IPEndPoint expected)
        => actual.Port == expected.Port && Canonicalise(actual.Address).Equals(Canonicalise(expected.Address));

    private static IPAddress Canonicalise(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Interprets a matched response: extracts the mapped address, follows one redirect,
    /// or throws an appropriate exception for error responses.
    /// </summary>
    private async Task<StunBindingResult> ProcessResponseAsync(
        StunMessage response,
        IPEndPoint originalServer,
        StunTransport transport,
        bool allowRedirect,
        string? tlsTargetHost,
        RemoteCertificateValidationCallback? tlsRemoteCertificateValidationCallback,
        IPEndPoint? localEndPoint,
        Socket? sharedUdpSocket,
        CancellationToken ct)
    {
        if (response.MessageClass == StunMessageClass.ErrorResponse)
        {
            var error = response.Attributes.OfType<ErrorCodeAttribute>().FirstOrDefault();
            int code = error?.Code ?? 0;

            // 300 Try Alternate — follow once (RFC 5389 §11).
            if (code == 300 && allowRedirect)
            {
                var alt = response.Attributes.OfType<AlternateServerAttribute>().FirstOrDefault();
                if (alt is not null)
                {
                    _logger.LogInformation(
                        "STUN 300 Try Alternate: redirecting from {Original} to {Alternate}",
                        originalServer, alt.EndPoint);
                    return await QueryWithAuthAsync(
                            alt.EndPoint,
                            credentials: null,
                            transport: transport,
                            allowRedirect: false,
                            tlsTargetHost: null,
                            tlsRemoteCertificateValidationCallback: tlsRemoteCertificateValidationCallback,
                            localEndPoint: localEndPoint,
                            sharedUdpSocket: sharedUdpSocket,
                            additionalAttributes: null,
                            ct: ct)
                        .ConfigureAwait(false);
                }
            }

            // 401 / 438 — surfaced as challenge exceptions for the long-term auth flow.
            if (code is 401 or 438)
            {
                var realm = response.Attributes.OfType<RealmAttribute>().FirstOrDefault()?.Value;
                var nonce = response.Attributes.OfType<NonceAttribute>().FirstOrDefault()?.Value;
                throw new StunChallengeException(code, error?.Reason ?? string.Empty, realm, nonce);
            }

            var msg = $"STUN error {code}: {error?.Reason ?? "(no reason)"}";
            _logger.LogWarning("{Message}", msg);
            throw new StunException(msg);
        }

        var xorMapped = response.Attributes.OfType<XorMappedAddressAttribute>().FirstOrDefault();
        if (xorMapped is not null)
        {
            _logger.LogDebug("STUN mapped address (XOR): {EndPoint}", xorMapped.EndPoint);
            return new StunBindingResult { MappedEndPoint = xorMapped.EndPoint, IsXorMapped = true };
        }

        var mapped = response.Attributes.OfType<MappedAddressAttribute>().FirstOrDefault();
        if (mapped is not null)
        {
            _logger.LogDebug("STUN mapped address: {EndPoint}", mapped.EndPoint);
            return new StunBindingResult { MappedEndPoint = mapped.EndPoint, IsXorMapped = false };
        }

        throw new StunException("STUN success response contained no address attribute.");
    }
}
