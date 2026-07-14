using System.Net;
using CalloraVoipSdk.Core.Domain.Calls;
using CalloraVoipSdk.Core.Infrastructure.Srtp.Context;
using Microsoft.Extensions.Logging;

namespace CalloraVoipSdk.Core.Infrastructure.Dtls;

/// <summary>
/// Attaches DTLS-SRTP keying (RFC 5763/5764) to a call media session: bridges DTLS
/// records between the shared RTP socket and the handshake engine, runs the handshake
/// in the negotiated role, derives the four SRTP/SRTCP contexts from the exported keys,
/// and hands them to the session via callback. Owns the derived contexts and the DTLS
/// association (close_notify on dispose). Mirrors the IceMediaAttachment pattern so the
/// media session stays small.
/// </summary>
internal sealed class DtlsMediaAttachment : IAsyncDisposable
{
    private readonly bool _isClient;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly DtlsFingerprint _expectedRemoteFingerprint;
    private readonly IDtlsSrtpHandshaker _handshaker;
    private readonly DtlsCertificate _certificate;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _sendRaw;
    private readonly Action<ISrtpContext, ISrtpContext, ISrtcpContext, ISrtcpContext> _onContextsReady;
    private readonly Action _onHandshakeFailed;
    private readonly ILogger<DtlsMediaAttachment> _logger;
    private readonly QueueDatagramTransport _transport;
    private readonly CancellationTokenSource _lifetimeCts = new();

    // Cached once: the token stays usable for in-flight fire-and-forget sends even
    // after the CTS is disposed during teardown.
    private readonly CancellationToken _lifetimeToken;

    private Task? _handshakeTask;
    private DtlsSrtpHandshakeResult? _result;
    private ISrtpContext? _outboundSrtp;
    private ISrtpContext? _inboundSrtp;
    private ISrtcpContext? _outboundSrtcp;
    private ISrtcpContext? _inboundSrtcp;
    private int _disposed;

    private DtlsMediaAttachment(
        bool isClient,
        IPEndPoint remoteEndPoint,
        DtlsFingerprint expectedRemoteFingerprint,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        Action<ISrtpContext, ISrtpContext, ISrtcpContext, ISrtcpContext> onContextsReady,
        Action onHandshakeFailed,
        ILoggerFactory loggerFactory)
    {
        _isClient = isClient;
        _remoteEndPoint = remoteEndPoint;
        _expectedRemoteFingerprint = expectedRemoteFingerprint;
        _handshaker = handshaker;
        _certificate = certificate;
        _sendRaw = sendRaw;
        _onContextsReady = onContextsReady;
        _onHandshakeFailed = onHandshakeFailed;
        _logger = loggerFactory.CreateLogger<DtlsMediaAttachment>();
        _transport = new QueueDatagramTransport(DispatchOutbound);
        _lifetimeToken = _lifetimeCts.Token;
    }

    /// <summary>
    /// Validates that a DTLS-negotiated leg has everything the handshake needs — the
    /// media session calls this before allocating any resources (socket, contexts) so a
    /// misconfigured leg fails closed without leaking them. No-op when DTLS was not
    /// negotiated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// DTLS was negotiated but the DTLS dependencies or the peer fingerprint are missing.
    /// </exception>
    public static void EnsureDependencies(
        CallMediaParameters parameters,
        IDtlsSrtpHandshaker? handshaker,
        DtlsCertificate? certificate)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (!parameters.IsDtlsNegotiated)
            return;

        if (handshaker is null || certificate is null)
            throw new InvalidOperationException(
                "DTLS-SRTP was negotiated but the media session has no DTLS handshaker/certificate configured.");

        if (string.IsNullOrWhiteSpace(parameters.DtlsRemoteFingerprintAlgorithm)
            || string.IsNullOrWhiteSpace(parameters.DtlsRemoteFingerprintValue))
        {
            throw new InvalidOperationException(
                "DTLS-SRTP was negotiated without a remote certificate fingerprint; refusing to start unauthenticated media (RFC 5763 §6.7.1).");
        }
    }

    /// <summary>
    /// Creates the attachment for a DTLS-negotiated call leg, validating that everything
    /// the handshake needs is present (fail closed: a DTLS-negotiated call without
    /// handshaker, certificate, or peer fingerprint must not start at all).
    /// Returns <see langword="null"/> when the leg did not negotiate DTLS.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// DTLS was negotiated but the DTLS dependencies or the peer fingerprint are missing.
    /// </exception>
    public static DtlsMediaAttachment? TryCreate(
        CallMediaParameters parameters,
        IDtlsSrtpHandshaker? handshaker,
        DtlsCertificate? certificate,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        Action<ISrtpContext, ISrtpContext, ISrtcpContext, ISrtcpContext> onContextsReady,
        Action onHandshakeFailed,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(onContextsReady);
        ArgumentNullException.ThrowIfNull(onHandshakeFailed);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (!parameters.IsDtlsNegotiated)
            return null;

        EnsureDependencies(parameters, handshaker, certificate);

        // Non-null after EnsureDependencies; the flow analysis cannot see through the call.
        var expected = new DtlsFingerprint
        {
            Algorithm = parameters.DtlsRemoteFingerprintAlgorithm!,
            Value = parameters.DtlsRemoteFingerprintValue!,
        };

        return new DtlsMediaAttachment(
            parameters.DtlsIsClient, parameters.RemoteEndPoint, expected,
            handshaker!, certificate!, sendRaw, onContextsReady, onHandshakeFailed, loggerFactory);
    }

    /// <summary>
    /// Inbound DTLS records demultiplexed off the RTP socket (RFC 5764 §5.1.2). Records
    /// from any source other than the negotiated remote media endpoint are dropped —
    /// an off-path sender must not be able to feed the handshake. Deliberate trade-off:
    /// a peer whose NAT rewrites the source port (the symmetric-RTP scenario) will not
    /// complete the handshake against this strict filter; relaxing it safely (the
    /// fingerprint already authenticates the peer) is tracked as follow-up work.
    /// </summary>
    public void OnDtlsPacketReceived(byte[] datagram, IPEndPoint source)
    {
        if (!_remoteEndPoint.Equals(source))
        {
            _logger.LogDebug(
                "Dropping DTLS record from unexpected source {Source}; negotiated remote is {Remote}.",
                source, _remoteEndPoint);
            return;
        }

        _transport.Enqueue(datagram);
    }

    /// <summary>Starts the DTLS handshake in the background in the negotiated role.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        _handshakeTask = RunHandshakeAsync(linked);
    }

    private async Task RunHandshakeAsync(CancellationTokenSource linkedCts)
    {
        using (linkedCts)
        {
            try
            {
                var result = await _handshaker.HandshakeAsync(
                        _isClient ? DtlsRole.Client : DtlsRole.Server,
                        _transport, _certificate, _expectedRemoteFingerprint, linkedCts.Token)
                    .ConfigureAwait(false);

                Volatile.Write(ref _result, result);
                _outboundSrtp = new SrtpContext(result.Keys.LocalKeys);
                _inboundSrtp = new SrtpContext(result.Keys.RemoteKeys);
                _outboundSrtcp = new SrtcpContext(result.Keys.LocalKeys);
                _inboundSrtcp = new SrtcpContext(result.Keys.RemoteKeys);
                _onContextsReady(_outboundSrtp, _inboundSrtp, _outboundSrtcp, _inboundSrtcp);
            }
            catch (OperationCanceledException)
            {
                // Session teardown during the handshake — nothing to key, nothing to report.
                _logger.LogDebug("DTLS handshake aborted by session teardown.");
            }
            catch (DtlsSrtpHandshakeException ex)
            {
                // Fail closed: the session keeps dropping all media (RequireEncryptedMedia);
                // the failure callback lets the owner cease transmission / surface teardown.
                _logger.LogError(ex, "DTLS-SRTP handshake failed; media stays blocked for this call leg.");
                _onHandshakeFailed();
            }
        }
    }

    private void DispatchOutbound(byte[] datagram)
    {
        // The BC engine sends synchronously from its handshake thread; bridge to the async
        // socket without blocking it. Loss is tolerable — DTLS flights retransmit.
        _ = SendOutboundAsync(datagram);
    }

    private async Task SendOutboundAsync(byte[] datagram)
    {
        try
        {
            await _sendRaw(datagram, _remoteEndPoint, _lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Teardown while a flight was in transit.
            _logger.LogTrace("DTLS record send aborted by session teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DTLS record to {Remote}.", _remoteEndPoint);
        }
    }

    /// <summary>
    /// Closes a completed DTLS association (close_notify) while the send bridge is still
    /// usable, aborts a still-running handshake, and zeroes the derived SRTP/SRTCP session
    /// keys. Awaits the handshake task so no callback can fire after disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Completed handshake: close before cancelling — the close_notify record travels
        // through the outbound bridge, which the lifetime cancellation would suppress.
        Volatile.Read(ref _result)?.Dispose();

        _lifetimeCts.Cancel();
        _transport.Close();

        if (_handshakeTask is { } handshakeTask)
        {
            try
            {
                await handshakeTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RunHandshakeAsync handles its own failures; anything escaping here is a
                // teardown race and must not break disposal.
                _logger.LogDebug(ex, "DTLS handshake task faulted during disposal.");
            }
        }

        // Covers a handshake that completed between the early close above and the cancel:
        // the association is closed either way (Dispose is idempotent); only the close
        // record itself may be lost in that narrow race.
        Volatile.Read(ref _result)?.Dispose();
        _lifetimeCts.Dispose();

        _outboundSrtp?.Dispose();
        _inboundSrtp?.Dispose();
        _outboundSrtcp?.Dispose();
        _inboundSrtcp?.Dispose();
    }
}
