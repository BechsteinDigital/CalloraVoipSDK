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
    // Opt-in RFC 6347 §4.2.1 stateless cookie on the server role: true for SIP legs without ICE
    // source validation, false for WebRTC legs (ICE already validates the source, and a browser
    // DTLS client stalls on a server-sent HelloVerifyRequest).
    private readonly bool _useServerCookie;
    // The peer media endpoint DTLS records are exchanged with and the source inbound records are accepted
    // from. Mutable: a bundled transport whose ICE agent nominates (or re-nominates) a different candidate
    // pair updates it via UpdateRemoteEndPoint so the strict inbound source filter follows the nominated
    // remote. Accessed via Volatile — written from the nomination path, read on the receive loop. The SIP
    // path never updates it (fixed nominated remote), so its strict behaviour is unchanged.
    private IPEndPoint _remoteEndPoint;
    private readonly DtlsFingerprint _expectedRemoteFingerprint;
    private readonly IDtlsSrtpHandshaker _handshaker;
    private readonly DtlsCertificate _certificate;
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> _sendRaw;
    private readonly Action<ISrtpContext, ISrtpContext, ISrtcpContext, ISrtcpContext> _onContextsReady;
    private readonly Action<ISrtpContext, ISrtpContext>? _onSecondaryContextsReady;
    private readonly Action _onHandshakeFailed;
    // Invoked when the peer closes the DTLS association (close_notify/fatal alert) after key export, so
    // the owner ceases media for this leg — media must not keep flowing under a keying channel the peer
    // considers closed (#190). A no-op when the owner does not wire it (the association is still serviced
    // and the close logged; stray application_data is still discarded).
    private readonly Action _onPeerClosed;
    private readonly ILogger<DtlsMediaAttachment> _logger;
    private readonly QueueDatagramTransport _transport;
    // Bounded single-writer egress (#191): BouncyCastle sends records synchronously from its
    // handshake thread; this pump orders them, applies backpressure, and propagates a send failure
    // back into the handshake instead of the old unordered, error-swallowing fire-and-forget bridge.
    private readonly DtlsEgressPump _egress;
    private readonly CancellationTokenSource _lifetimeCts = new();

    // A completed handshake's close_notify travels through the egress pump. Teardown drains it with
    // this deadline before cancelling the rest of the egress, so a live peer actually receives it —
    // DTLS does not retransmit alerts (RFC 6347 §4.2.7). Bounded so a dead socket cannot stall it.
    private static readonly TimeSpan CloseNotifyDrainDeadline = TimeSpan.FromMilliseconds(500);

    private Task? _handshakeTask;
    private DtlsSrtpHandshakeResult? _result;
    // Services the association after key export (#190): notices a peer close_notify/alert and discards
    // stray application_data. Null until the handshake completes; set once, read on teardown.
    private DtlsAssociationReceiver? _associationReceiver;
    private ISrtpContext? _outboundSrtp;
    private ISrtpContext? _inboundSrtp;
    private ISrtcpContext? _outboundSrtcp;
    private ISrtcpContext? _inboundSrtcp;
    private ISrtpContext? _rtxOutboundSrtp;
    private ISrtpContext? _rtxInboundSrtp;
    private int _disposed;

    private DtlsMediaAttachment(
        bool isClient,
        IPEndPoint remoteEndPoint,
        DtlsFingerprint expectedRemoteFingerprint,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        Action<ISrtpContext, ISrtpContext, ISrtcpContext, ISrtcpContext> onContextsReady,
        Action<ISrtpContext, ISrtpContext>? onSecondaryContextsReady,
        Action onHandshakeFailed,
        Action? onPeerClosed,
        ILoggerFactory loggerFactory,
        bool useServerCookie)
    {
        _isClient = isClient;
        _useServerCookie = useServerCookie;
        _remoteEndPoint = remoteEndPoint;
        _expectedRemoteFingerprint = expectedRemoteFingerprint;
        _handshaker = handshaker;
        _certificate = certificate;
        _sendRaw = sendRaw;
        _onContextsReady = onContextsReady;
        _onSecondaryContextsReady = onSecondaryContextsReady;
        _onHandshakeFailed = onHandshakeFailed;
        _onPeerClosed = onPeerClosed ?? (() => { });
        _logger = loggerFactory.CreateLogger<DtlsMediaAttachment>();
        _egress = new DtlsEgressPump(SendRawToRemoteAsync, _logger);
        _transport = new QueueDatagramTransport(DispatchOutbound);
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
    /// <param name="remoteEndPointOverride">
    /// Remote transport address of the media stream this attachment keys; defaults to
    /// the audio remote endpoint. A video m-line runs its own DTLS association on its
    /// own socket (RFC 5763: one association per m-line without BUNDLE) and passes its
    /// video remote endpoint here.
    /// </param>
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
        ILoggerFactory loggerFactory,
        IPEndPoint? remoteEndPointOverride = null,
        Action<ISrtpContext, ISrtpContext>? onSecondaryContextsReady = null,
        Action? onPeerClosed = null,
        bool useServerCookie = true)
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

        // SIP entry point: legs here have no ICE source validation, so the stateless cookie is on
        // by default (RFC 6347 §4.2.1); the WebRTC bundle uses Create directly with it off.
        return Create(
            parameters.DtlsIsClient, remoteEndPointOverride ?? parameters.RemoteEndPoint, expected,
            handshaker!, certificate!, sendRaw, onContextsReady, onHandshakeFailed, loggerFactory,
            onSecondaryContextsReady, onPeerClosed, useServerCookie);
    }

    /// <summary>
    /// Creates the attachment from explicit DTLS parameters, independent of any SIP call context, so a
    /// bundled transport (RFC 8843) can key its one shared DTLS association the same way (ADR-011 B3-2).
    /// <see cref="TryCreate"/> is the SIP entry point that derives these inputs from the negotiated call.
    /// </summary>
    /// <param name="isClient">Whether this side runs the DTLS client role (RFC 5763 setup:active).</param>
    /// <param name="remoteEndPoint">The peer media endpoint DTLS records are exchanged with.</param>
    /// <param name="expectedRemoteFingerprint">The peer certificate fingerprint that authenticates the handshake.</param>
    /// <param name="sendRaw">Sends a raw DTLS record to the peer over the media socket.</param>
    /// <param name="onContextsReady">Receives the derived outbound/inbound SRTP and SRTCP contexts.</param>
    /// <param name="onSecondaryContextsReady">Optional RTX (RFC 4588) SRTP contexts from the same keys.</param>
    /// <param name="onHandshakeFailed">Invoked when the handshake fails, so the owner keeps media blocked.</param>
    /// <param name="onPeerClosed">Invoked when the peer closes the association after key export, so the owner ceases media (#190).</param>
    public static DtlsMediaAttachment Create(
        bool isClient,
        IPEndPoint remoteEndPoint,
        DtlsFingerprint expectedRemoteFingerprint,
        IDtlsSrtpHandshaker handshaker,
        DtlsCertificate certificate,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask> sendRaw,
        Action<ISrtpContext, ISrtpContext, ISrtcpContext, ISrtcpContext> onContextsReady,
        Action onHandshakeFailed,
        ILoggerFactory loggerFactory,
        Action<ISrtpContext, ISrtpContext>? onSecondaryContextsReady = null,
        Action? onPeerClosed = null,
        bool useServerCookie = false)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(expectedRemoteFingerprint);
        ArgumentNullException.ThrowIfNull(handshaker);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(sendRaw);
        ArgumentNullException.ThrowIfNull(onContextsReady);
        ArgumentNullException.ThrowIfNull(onHandshakeFailed);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return new DtlsMediaAttachment(
            isClient, remoteEndPoint, expectedRemoteFingerprint, handshaker, certificate,
            sendRaw, onContextsReady, onSecondaryContextsReady, onHandshakeFailed, onPeerClosed, loggerFactory,
            useServerCookie);
    }

    /// <summary>
    /// Updates the peer media endpoint DTLS records are accepted from and sent to, so the inbound source
    /// filter follows an ICE nomination or re-nomination (RFC 8445 §8) onto a different candidate pair.
    /// Thread-safe. The SIP path leaves it fixed at the negotiated remote.
    /// </summary>
    /// <param name="remoteEndPoint">The nominated remote endpoint DTLS now runs against.</param>
    public void UpdateRemoteEndPoint(IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        Volatile.Write(ref _remoteEndPoint, remoteEndPoint);
    }

    /// <summary>
    /// Inbound DTLS records demultiplexed off the RTP socket (RFC 5764 §5.1.2). Records
    /// from any source other than the current remote media endpoint are dropped — an
    /// off-path sender must not be able to feed the handshake. The remote endpoint follows
    /// ICE nomination via <see cref="UpdateRemoteEndPoint"/>, so switching to a
    /// connectivity-checked candidate pair keeps the handshake flowing; the fingerprint
    /// remains the authentication boundary (RFC 5763 §6.7.1).
    /// </summary>
    public void OnDtlsPacketReceived(byte[] datagram, IPEndPoint source)
    {
        var remote = Volatile.Read(ref _remoteEndPoint);
        if (!remote.Equals(source))
        {
            _logger.LogDebug(
                "Dropping DTLS record from unexpected source {Source}; current remote is {Remote}.",
                source, remote);
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
                // Bind the server-role DTLS cookie to the current nominated remote endpoint on legs
                // that opted in (SIP without ICE, RFC 6347 §4.2.1); WebRTC legs pass an empty id so
                // no HelloVerifyRequest is sent (ICE already validated the source, and a browser
                // DTLS client stalls on one). Ignored for the client role. Snapshotted at handshake
                // start: an ICE re-nomination inside the brief cookie window would bind to the stale
                // address and time out — acceptable, and only the opt-in SIP path uses the cookie.
                var cookieClientId = _useServerCookie
                    ? BuildCookieClientId(Volatile.Read(ref _remoteEndPoint))
                    : ReadOnlyMemory<byte>.Empty;
                var result = await _handshaker.HandshakeAsync(
                        _isClient ? DtlsRole.Client : DtlsRole.Server,
                        _transport, _certificate, _expectedRemoteFingerprint, linkedCts.Token,
                        cookieClientId)
                    .ConfigureAwait(false);

                Volatile.Write(ref _result, result);
                try
                {
                    _outboundSrtp = new SrtpContext(result.Keys.LocalKeys);
                    _inboundSrtp = new SrtpContext(result.Keys.RemoteKeys);
                    _outboundSrtcp = new SrtcpContext(result.Keys.LocalKeys);
                    _inboundSrtcp = new SrtcpContext(result.Keys.RemoteKeys);
                    _onContextsReady(_outboundSrtp, _inboundSrtp, _outboundSrtcp, _inboundSrtcp);

                    // RTX repair stream (RFC 4588 §9): its own SRTP contexts from the same keys,
                    // so its independent sequence space has its own replay window / ROC.
                    if (_onSecondaryContextsReady is { } onRtx)
                    {
                        _rtxOutboundSrtp = new SrtpContext(result.Keys.LocalKeys);
                        _rtxInboundSrtp = new SrtpContext(result.Keys.RemoteKeys);
                        onRtx(_rtxOutboundSrtp, _rtxInboundSrtp);
                    }
                }
                finally
                {
                    // Every context above has now derived its session keys from these master halves,
                    // and this SDK never re-keys within a session — wipe the master key/salt so the
                    // exported DTLS-SRTP secret does not linger on the managed heap (RFC 3711 §9.4).
                    // The retained _result keeps only the (non-secret) DTLS transport alive for teardown.
                    //
                    // #157 P2-6: in a finally, because a throwing context constructor or owner callback
                    // would otherwise skip the wipe entirely and leave the exported secret behind on
                    // precisely the path where nothing else cleans up.
                    result.Keys.Dispose();
                }

                // Serve the association from here on (#190): media now flows directly over SRTP, so
                // nobody would otherwise read the DTLS channel again and a peer close_notify would go
                // unnoticed. The receiver notices a peer close (→ the owner ceases media) and discards
                // stray DTLS application_data. It runs under the lifetime token and is drained on
                // teardown before the association is closed (see DisposeAsync).
                var receiver = new DtlsAssociationReceiver(
                    new BouncyCastleDtlsControlChannel(result.Transport, _transport),
                    _transport.GetReceiveLimit(),
                    _onPeerClosed,
                    _logger,
                    _lifetimeCts.Token);
                Volatile.Write(ref _associationReceiver, receiver);
                receiver.Start();
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
            catch (Exception ex)
            {
                // #157 P2-6: keying does not end at the handshake. A throwing SRTP context constructor
                // or owner callback used to escape both typed catches — leaving the handshake task
                // faulted and unobserved, and the owner never told that keying failed, so it would sit
                // waiting for media that can never be authenticated. Treat it exactly like a handshake
                // failure: log and fail closed. The master keys are already wiped by the finally above.
                _logger.LogError(
                    ex, "DTLS-SRTP keying failed after the handshake completed; media stays blocked for this call leg.");
                _onHandshakeFailed();
            }
        }
    }

    private static byte[] BuildCookieClientId(IPEndPoint endpoint)
    {
        // Binds the stateless DTLS cookie to the peer's transport address (RFC 6347 §4.2.1): the
        // IP bytes (4 or 16) followed by the port. A source that spoofs a different address cannot
        // echo a cookie the server will accept, so it never reaches the certificate flight.
        // Canonicalise an IPv4-mapped-IPv6 address (dual-stack sockets) to its 4-byte form so the
        // client id is one stable value per peer, matching the RelayEndPoint normalisation.
        var address = endpoint.Address.IsIPv4MappedToIPv6
            ? endpoint.Address.MapToIPv4()
            : endpoint.Address;
        var addressBytes = address.GetAddressBytes();
        var clientId = new byte[addressBytes.Length + 2];
        addressBytes.CopyTo(clientId, 0);
        clientId[addressBytes.Length] = (byte)(endpoint.Port >> 8);
        clientId[addressBytes.Length + 1] = (byte)endpoint.Port;
        return clientId;
    }

    private void DispatchOutbound(byte[] datagram)
    {
        // BouncyCastle calls this synchronously from its handshake thread. Hand the record to the
        // bounded single-writer pump, which sends records in order and re-throws a prior transport
        // failure here so the handshake aborts fail-closed instead of losing it to a log line (#191).
        _egress.Enqueue(datagram);
    }

    private ValueTask SendRawToRemoteAsync(byte[] datagram, CancellationToken cancellationToken)
        => _sendRaw(datagram, Volatile.Read(ref _remoteEndPoint), cancellationToken);

    /// <summary>
    /// Closes a completed DTLS association (close_notify) while the send bridge is still
    /// usable, aborts a still-running handshake, and zeroes the derived SRTP/SRTCP session
    /// keys. Awaits the handshake task so no callback can fire after disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();

        // Stop the association receiver first, and cleanly: cancelling its (linked) token makes the
        // loop exit after its current bounded receive TIMES OUT — a clean -1 that does NOT fault
        // BouncyCastle's record layer. That ordering matters, because a faulted record layer makes
        // BouncyCastle skip the close_notify we send below (it only warns close_notify while healthy).
        // So we must NOT close the transport before the receiver has stopped.
        var receiver = Volatile.Read(ref _associationReceiver);
        if (receiver is not null)
            await receiver.DisposeAsync().ConfigureAwait(false);

        // Unblock a still-running handshake (no receiver was started in that case) and wait for it to
        // end, so _result is final. Harmless once the handshake has already completed.
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

        // A handshake that completed during teardown may have started a receiver after the snapshot
        // above — stop that one too, so no worker is left behind.
        var lateReceiver = Volatile.Read(ref _associationReceiver);
        if (lateReceiver is not null && !ReferenceEquals(lateReceiver, receiver))
            await lateReceiver.DisposeAsync().ConfigureAwait(false);

        // Handshake is done and the receiver is stopped (the record layer is still healthy): closing
        // the association enqueues close_notify onto the still-live egress pump. Drain it with a tight
        // deadline so a live peer actually receives it — DTLS does not retransmit alerts
        // (RFC 6347 §4.2.7) — THEN cancel the remaining egress.
        Volatile.Read(ref _result)?.Dispose();
        await _egress.DrainAsync(CloseNotifyDrainDeadline).ConfigureAwait(false);
        await _egress.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();

        _outboundSrtp?.Dispose();
        _inboundSrtp?.Dispose();
        _outboundSrtcp?.Dispose();
        _inboundSrtcp?.Dispose();
        _rtxOutboundSrtp?.Dispose();
        _rtxInboundSrtp?.Dispose();
    }
}
