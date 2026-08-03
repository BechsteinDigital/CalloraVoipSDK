using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Default <see cref="IDtlsSrtpHandshaker"/>: drives the blocking BouncyCastle DTLS
/// engine on a worker thread, wires up the <c>use_srtp</c>-enabled client/server peers,
/// and surfaces the exported SRTP keys. Fingerprint verification happens inside the
/// handshake (fatal alert on mismatch), so a returned result is always authenticated.
/// </summary>
internal sealed class DtlsSrtpHandshaker : IDtlsSrtpHandshaker
{
    // Upper bound on a single blocking receive while awaiting the cookie'd ClientHello. Closing
    // the transport (the handshake deadline) wakes a blocked receive immediately, so this only
    // caps idle polling; it never governs the overall handshake timeout (see HandshakeAsync).
    private const int CookieReceivePollMillis = 1000;

    private readonly ILogger<DtlsSrtpHandshaker> _logger;
    private readonly DtlsHandshakeOptions _options;

    public DtlsSrtpHandshaker(
        ILogger<DtlsSrtpHandshaker> logger,
        DtlsHandshakeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = options ?? DtlsHandshakeOptions.Default;
    }

    /// <inheritdoc />
    public async Task<DtlsSrtpHandshakeResult> HandshakeAsync(
        DtlsRole role,
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        CancellationToken cancellationToken = default,
        ReadOnlyMemory<byte> serverCookieClientId = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);

        // The server role must bind the stateless cookie to the peer address, so an empty client
        // id (which would key the cookie MAC on nothing) is a wiring error, not a degraded mode
        // (#163 P1-2). Fail loudly at the call site rather than silently losing source binding.
        if (role == DtlsRole.Server && serverCookieClientId.IsEmpty)
            throw new ArgumentException(
                "A server-role DTLS handshake requires a non-empty cookie client id so the "
                + "stateless cookie binds to the peer address (RFC 6347 §4.2.1).",
                nameof(serverCookieClientId));

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("Starting DTLS-SRTP handshake as {Role}.", role);

        // Product deadline (#163 P1-1): a silent or stalling peer must not pin the worker
        // thread or the shared media socket open forever. The linked source fires on caller
        // cancellation (session teardown) OR the handshake timeout, whichever comes first.
        // Closing the transport wakes the blocking BC receive — the only cancellation channel
        // the engine understands.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_options.HandshakeTimeout);
        var linkedToken = linkedCts.Token;

        var abortRegistration = linkedToken.Register(static state =>
            ((DatagramTransport)state!).Close(), transport);

        try
        {
            var engineTimeoutMillis = ResolveEngineTimeoutMillis(_options.HandshakeTimeout);
            var result = role == DtlsRole.Client
                ? await Task.Run(
                        () => ConnectAsClient(
                            transport, localCertificate, expectedRemoteFingerprint, engineTimeoutMillis),
                        CancellationToken.None)
                    .ConfigureAwait(false)
                : await Task.Run(
                        () => AcceptAsServer(
                            transport, localCertificate, expectedRemoteFingerprint, engineTimeoutMillis,
                            serverCookieClientId),
                        CancellationToken.None)
                    .ConfigureAwait(false);

            // Detach the abort callback before handing the transport out — Dispose waits for
            // an in-flight callback, so past this point neither cancellation nor the deadline
            // can close the transport underneath the returned result.
            abortRegistration.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
                // Caller tore the session down as the handshake completed — discard the result
                // and surface teardown. The deadline is deliberately NOT checked on this path:
                // a handshake that produced valid keys has succeeded, so a deadline firing in
                // the same instant must not fail a working media leg with a false-positive
                // timeout. The genuine silent-peer timeout never reaches here — it surfaces from
                // the engine (closed transport) into the catch below.
                result.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _logger.LogInformation(
                "DTLS-SRTP handshake completed as {Role}; negotiated suite {Suite}.",
                role, result.Keys.Suite);
            return result;
        }
        catch (Exception ex) when (ex is not DtlsSrtpHandshakeException and not OperationCanceledException)
        {
            // A closed transport surfaces from the engine as an IO/TLS error: classify it as
            // caller cancellation, a deadline abort, or a genuine protocol failure.
            ThrowIfAborted(cancellationToken, linkedToken, role);
            _logger.LogWarning(ex, "DTLS-SRTP handshake as {Role} failed.", role);
            throw new DtlsSrtpHandshakeException($"DTLS-SRTP handshake as {role} failed.", ex);
        }
        finally
        {
            abortRegistration.Dispose();
        }
    }

    /// <summary>
    /// Distinguishes the two abort channels after the transport was closed. Caller cancellation
    /// (session teardown) rethrows the original <see cref="OperationCanceledException"/>; the
    /// product deadline firing on its own — the linked token is cancelled but the caller's is
    /// not — raises a typed <see cref="DtlsSrtpHandshakeTimeoutException"/> so the owner fails
    /// closed exactly once and can tell a dead peer apart from a torn-down session. Returns
    /// without throwing when neither fired, i.e. the handshake failed for a real protocol reason.
    /// </summary>
    private static void ThrowIfAborted(
        CancellationToken caller, CancellationToken linked, DtlsRole role)
    {
        caller.ThrowIfCancellationRequested();
        if (linked.IsCancellationRequested)
            throw new DtlsSrtpHandshakeTimeoutException(
                $"DTLS-SRTP handshake as {role} exceeded the configured handshake timeout.");
    }

    private static DtlsSrtpHandshakeResult ConnectAsClient(
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        int handshakeTimeoutMillis)
    {
        var client = new DtlsSrtpClient(
            new BcTlsCrypto(new SecureRandom()), localCertificate, expectedRemoteFingerprint,
            handshakeTimeoutMillis);
        var dtlsTransport = new DtlsClientProtocol().Connect(client, transport);
        return BuildResult(client.NegotiatedKeys, dtlsTransport);
    }

    private static DtlsSrtpHandshakeResult AcceptAsServer(
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        int handshakeTimeoutMillis,
        ReadOnlyMemory<byte> cookieClientId)
    {
        var crypto = new BcTlsCrypto(new SecureRandom());
        var server = new DtlsSrtpServer(
            crypto, localCertificate, expectedRemoteFingerprint, handshakeTimeoutMillis);

        // RFC 6347 §4.2.1: complete the stateless cookie exchange before the certificate flight,
        // so a spoofed source is never handed the amplified server flight. Only a peer that echoes
        // a cookie MAC-bound to its own address (cookieClientId) reaches the real handshake.
        var request = VerifyClientCookie(transport, crypto, cookieClientId.ToArray());

        var dtlsTransport = new DtlsServerProtocol().Accept(server, transport, request);
        return BuildResult(server.NegotiatedKeys, dtlsTransport);
    }

    /// <summary>
    /// Runs the stateless DTLS cookie exchange (RFC 6347 §4.2.1) until the peer presents a valid
    /// cookie, then returns the verified <see cref="DtlsRequest"/> for the certificate handshake.
    /// A ClientHello without a valid cookie is answered with a HelloVerifyRequest (sent by
    /// <see cref="DtlsVerifier.VerifyRequest"/> over the transport) and creates no per-client state
    /// — a spoofed-source flood stays cheap. The loop is bounded by the handshake deadline: when it
    /// elapses the transport is closed, which makes the next receive throw and aborts the handshake.
    /// </summary>
    private static DtlsRequest VerifyClientCookie(
        DatagramTransport transport, BcTlsCrypto crypto, byte[] clientId)
    {
        var verifier = new DtlsVerifier(crypto);
        var buffer = new byte[transport.GetReceiveLimit()];
        while (true)
        {
            var received = transport.Receive(buffer, 0, buffer.Length, CookieReceivePollMillis);
            if (received < 0)
                continue; // Retransmit-timer tick; the handshake deadline closes the transport.

            var request = verifier.VerifyRequest(clientId, buffer, 0, received, (DatagramSender)transport);
            if (request is not null)
                return request;

            // null: a HelloVerifyRequest was sent (or a non-ClientHello record ignored). Keep
            // waiting for the cookie'd ClientHello — deliberately no per-packet log (flood safety).
        }
    }

    /// <summary>
    /// Maps the product handshake deadline to BouncyCastle's own <c>GetHandshakeTimeoutMillis</c>
    /// ceiling. This engine-level deadline is only reached if the transport-close failsafe ever
    /// fails to wake the blocking receive, so it is given generous headroom (2x) above the
    /// product deadline: the outer, typed failsafe fires first, and by the time this elapses the
    /// linked token is already cancelled, so both paths classify as a timeout. Clamped to a
    /// positive <see cref="int"/> (BouncyCastle takes milliseconds as <see cref="int"/>).
    /// </summary>
    private static int ResolveEngineTimeoutMillis(TimeSpan deadline)
    {
        var doubledMillis = deadline.TotalMilliseconds * 2d;
        return doubledMillis >= int.MaxValue ? int.MaxValue : (int)Math.Max(1d, doubledMillis);
    }

    private static DtlsSrtpHandshakeResult BuildResult(
        DtlsSrtpNegotiatedKeys? keys, DtlsTransport dtlsTransport)
    {
        if (keys is null)
        {
            // Handshake "succeeded" without exported keys — cannot happen with the SDK's
            // peers (export runs in NotifyHandshakeComplete), but never return unkeyed.
            dtlsTransport.Close();
            throw new DtlsSrtpHandshakeException(
                "DTLS handshake completed without exported SRTP keying material.");
        }

        return new DtlsSrtpHandshakeResult(keys, dtlsTransport);
    }
}
