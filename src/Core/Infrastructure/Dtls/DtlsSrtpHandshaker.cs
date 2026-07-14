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
    private readonly ILogger<DtlsSrtpHandshaker> _logger;

    public DtlsSrtpHandshaker(ILogger<DtlsSrtpHandshaker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DtlsSrtpHandshakeResult> HandshakeAsync(
        DtlsRole role,
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("Starting DTLS-SRTP handshake as {Role}.", role);

        // Closing the transport wakes the blocking BC receive and aborts the handshake;
        // this is the only cancellation channel the engine understands.
        var abortRegistration = cancellationToken.Register(static state =>
            ((DatagramTransport)state!).Close(), transport);

        try
        {
            var result = role == DtlsRole.Client
                ? await Task.Run(
                        () => ConnectAsClient(transport, localCertificate, expectedRemoteFingerprint),
                        CancellationToken.None)
                    .ConfigureAwait(false)
                : await Task.Run(
                        () => AcceptAsServer(transport, localCertificate, expectedRemoteFingerprint),
                        CancellationToken.None)
                    .ConfigureAwait(false);

            // Detach the cancellation callback before handing the transport out — Dispose
            // waits for an in-flight callback, so past this point cancellation can no
            // longer close the transport underneath the returned result.
            abortRegistration.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
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
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "DTLS-SRTP handshake as {Role} failed.", role);
            throw new DtlsSrtpHandshakeException($"DTLS-SRTP handshake as {role} failed.", ex);
        }
        finally
        {
            abortRegistration.Dispose();
        }
    }

    private static DtlsSrtpHandshakeResult ConnectAsClient(
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint)
    {
        var client = new DtlsSrtpClient(
            new BcTlsCrypto(new SecureRandom()), localCertificate, expectedRemoteFingerprint);
        var dtlsTransport = new DtlsClientProtocol().Connect(client, transport);
        return BuildResult(client.NegotiatedKeys, dtlsTransport);
    }

    private static DtlsSrtpHandshakeResult AcceptAsServer(
        DatagramTransport transport,
        DtlsCertificate localCertificate,
        DtlsFingerprint expectedRemoteFingerprint)
    {
        var server = new DtlsSrtpServer(
            new BcTlsCrypto(new SecureRandom()), localCertificate, expectedRemoteFingerprint);
        var dtlsTransport = new DtlsServerProtocol().Accept(server, transport);
        return BuildResult(server.NegotiatedKeys, dtlsTransport);
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
